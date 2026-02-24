#!/usr/bin/dotnet run

#:sdk Microsoft.NET.Sdk

#:package Anthropic@12.2.0
#:package dotenv.net@4.0.0

#:property LangVersion=latest
#:property ImplicitUsings=enable
#:property PublishAot=false

/*
 * v11_worktree_task_isolation.cs - Mini Claude Code: Worktree + Task Isolation (~700 lines)
 *
 * 对应上游: s12_worktree_task_isolation.py
 *
 * 核心哲学: "目录隔离, 任务 ID 协调"
 * ======================================
 * 当多个任务并行执行时，需要隔离执行环境,
 * 防止互相污染。Git worktree 提供了目录级隔离。
 *
 * 架构:
 *     .tasks/          ← 控制平面: 持久化任务板
 *     .worktrees/      ← 执行平面: Git worktree 索引 + 事件日志
 *         index.json   ← worktree 注册表
 *         events.jsonl ← 生命周期事件总线 (append-only)
 *         <name>/      ← 实际 worktree 目录
 *
 * 工作流:
 *     1. task_create -> task_id
 *     2. worktree_create(name, task_id) -> bind to task
 *     3. worktree_run(name, command) -> isolated execution
 *     4. worktree_keep / worktree_remove(complete_task=true) -> closeout
 *
 * 17 个工具:
 *     4 基础 (bash, read, write, edit)
 *     5 任务 (task_create, task_list, task_get, task_update, task_bind_worktree)
 *     7 Worktree (worktree_create, list, status, run, remove, keep, events)
 *     1 事件 (worktree_events)
 */

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Messages;
using dotenv.net;

DotEnv.Load();

var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
    ?? throw new InvalidOperationException("ANTHROPIC_API_KEY not set");
var baseUrl = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL");
var modelId = Environment.GetEnvironmentVariable("MODEL_ID") ?? "claude-sonnet-4-5-20250929";

using var client = baseUrl is null
    ? new AnthropicClient() { ApiKey = apiKey }
    : new AnthropicClient() { ApiKey = apiKey, BaseUrl = baseUrl };

var workDir = Directory.GetCurrentDirectory();

// 检测 Git 仓库根目录
string DetectRepoRoot()
{
    try
    {
        using var p = new Process();
        p.StartInfo = new ProcessStartInfo
        {
            FileName = "git", Arguments = "rev-parse --show-toplevel",
            WorkingDirectory = workDir, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        p.Start();
        var root = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return p.ExitCode == 0 && Directory.Exists(root) ? root : workDir;
    }
    catch { return workDir; }
}

var repoRoot = DetectRepoRoot();
var tasksDir = Path.Combine(repoRoot, ".tasks");
var worktreesDir = Path.Combine(repoRoot, ".worktrees");

var systemPrompt = $"""
    你是一个位于 {workDir} 的编码智能体。
    使用 task + worktree 工具进行多任务工作。
    对于并行或高风险的变更: 创建任务 → 分配 worktree → 在隔离目录中执行 → 收尾 (keep/remove)。
    使用 worktree_events 获取生命周期可见性。
    """;

// =============================================================================
// EventBus - 追加式生命周期事件日志
// =============================================================================

var events = new EventBus(Path.Combine(worktreesDir, "events.jsonl"));

// =============================================================================
// TaskManager - 持久化任务板 (带 worktree 绑定)
// =============================================================================

var tasks = new TaskManager(tasksDir);

// =============================================================================
// WorktreeManager - Git worktree 生命周期管理
// =============================================================================

var worktrees = new WorktreeManager(repoRoot, tasks, events);

// =============================================================================
// 基础工具实现
// =============================================================================

string SafePath(string p)
{
    var full = Path.GetFullPath(Path.Combine(workDir, p));
    if (!full.StartsWith(workDir)) throw new InvalidOperationException($"Path escapes workspace: {p}");
    return full;
}

async Task<string> RunBashAsync(string command)
{
    string[] dangerous = ["rm -rf /", "sudo", "shutdown", "reboot", "> /dev/"];
    if (dangerous.Any(d => command.Contains(d))) return "Error: Dangerous command blocked";
    try
    {
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = "bash",
            Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"",
            WorkingDirectory = workDir, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        proc.Start();
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        var output = (stdout + stderr).Trim();
        return string.IsNullOrEmpty(output) ? "(no output)" : output[..Math.Min(output.Length, 50000)];
    }
    catch (Exception ex) { return $"Error: {ex.Message}"; }
}

string RunRead(string path, int? limit = null)
{
    try
    {
        var lines = File.ReadAllLines(SafePath(path));
        if (limit.HasValue && limit.Value < lines.Length)
            lines = [.. lines.Take(limit.Value), $"... ({lines.Length - limit.Value} more)"];
        var text = string.Join("\n", lines);
        return text[..Math.Min(text.Length, 50000)];
    }
    catch (Exception ex) { return $"Error: {ex.Message}"; }
}

string RunWrite(string path, string content)
{
    try
    {
        var fp = SafePath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fp)!);
        File.WriteAllText(fp, content);
        return $"Wrote {content.Length} bytes";
    }
    catch (Exception ex) { return $"Error: {ex.Message}"; }
}

string RunEdit(string path, string oldText, string newText)
{
    try
    {
        var fp = SafePath(path);
        var content = File.ReadAllText(fp);
        if (!content.Contains(oldText)) return $"Error: Text not found in {path}";
        var idx = content.IndexOf(oldText, StringComparison.Ordinal);
        File.WriteAllText(fp, string.Concat(content.AsSpan(0, idx), newText, content.AsSpan(idx + oldText.Length)));
        return $"Edited {path}";
    }
    catch (Exception ex) { return $"Error: {ex.Message}"; }
}

// =============================================================================
// 工具定义 (17 个)
// =============================================================================

Tool MakeTool(string name, string desc, Dictionary<string, JsonElement> props, List<string> required) =>
    new() { Name = name, Description = desc, InputSchema = new InputSchema { Properties = props, Required = required } };

JsonElement S(object o) => JsonSerializer.SerializeToElement(o);

var tools = new List<Tool>
{
    MakeTool("bash", "运行 shell 命令。", new() { ["command"] = S(new { type = "string" }) }, ["command"]),
    MakeTool("read_file", "读取文件。", new() { ["path"] = S(new { type = "string" }), ["limit"] = S(new { type = "integer" }) }, ["path"]),
    MakeTool("write_file", "写入文件。", new() { ["path"] = S(new { type = "string" }), ["content"] = S(new { type = "string" }) }, ["path", "content"]),
    MakeTool("edit_file", "替换文件中的文本。", new() { ["path"] = S(new { type = "string" }), ["old_text"] = S(new { type = "string" }), ["new_text"] = S(new { type = "string" }) }, ["path", "old_text", "new_text"]),

    MakeTool("task_create", "创建任务。", new() { ["subject"] = S(new { type = "string" }), ["description"] = S(new { type = "string" }) }, ["subject"]),
    MakeTool("task_list", "列出所有任务。", new(), []),
    MakeTool("task_get", "获取任务详情。", new() { ["task_id"] = S(new { type = "integer" }) }, ["task_id"]),
    MakeTool("task_update", "更新任务状态/所有者。", new() { ["task_id"] = S(new { type = "integer" }), ["status"] = S(new { type = "string" }), ["owner"] = S(new { type = "string" }) }, ["task_id"]),
    MakeTool("task_bind_worktree", "绑定任务到 worktree。", new() { ["task_id"] = S(new { type = "integer" }), ["worktree"] = S(new { type = "string" }), ["owner"] = S(new { type = "string" }) }, ["task_id", "worktree"]),

    MakeTool("worktree_create", "创建 git worktree，可选绑定任务。", new() { ["name"] = S(new { type = "string" }), ["task_id"] = S(new { type = "integer" }), ["base_ref"] = S(new { type = "string" }) }, ["name"]),
    MakeTool("worktree_list", "列出索引中的 worktree。", new(), []),
    MakeTool("worktree_status", "查看 worktree 的 git 状态。", new() { ["name"] = S(new { type = "string" }) }, ["name"]),
    MakeTool("worktree_run", "在 worktree 目录中运行命令。", new() { ["name"] = S(new { type = "string" }), ["command"] = S(new { type = "string" }) }, ["name", "command"]),
    MakeTool("worktree_remove", "移除 worktree，可选标记任务完成。", new() { ["name"] = S(new { type = "string" }), ["force"] = S(new { type = "boolean" }), ["complete_task"] = S(new { type = "boolean" }) }, ["name"]),
    MakeTool("worktree_keep", "标记 worktree 为 kept 状态。", new() { ["name"] = S(new { type = "string" }) }, ["name"]),
    MakeTool("worktree_events", "查看最近的生命周期事件。", new() { ["limit"] = S(new { type = "integer" }) }, []),
};

// =============================================================================
// 工具分发
// =============================================================================

async Task<string> ExecuteToolAsync(string name, IReadOnlyDictionary<string, JsonElement> args)
{
    try
    {
        return name switch
        {
            "bash" => await RunBashAsync(args["command"].GetString()!),
            "read_file" => RunRead(args["path"].GetString()!, args.TryGetValue("limit", out var l) ? l.GetInt32() : null),
            "write_file" => RunWrite(args["path"].GetString()!, args["content"].GetString()!),
            "edit_file" => RunEdit(args["path"].GetString()!, args["old_text"].GetString()!, args["new_text"].GetString()!),
            "task_create" => tasks.Create(args["subject"].GetString()!, args.TryGetValue("description", out var d) ? d.GetString() ?? "" : ""),
            "task_list" => tasks.ListAll(),
            "task_get" => tasks.Get(args["task_id"].GetInt32()),
            "task_update" => tasks.Update(args["task_id"].GetInt32(),
                args.TryGetValue("status", out var s) ? s.GetString() : null,
                args.TryGetValue("owner", out var o) ? o.GetString() : null),
            "task_bind_worktree" => tasks.BindWorktree(args["task_id"].GetInt32(), args["worktree"].GetString()!,
                args.TryGetValue("owner", out var ow) ? ow.GetString() ?? "" : ""),
            "worktree_create" => worktrees.Create(args["name"].GetString()!,
                args.TryGetValue("task_id", out var tid) ? tid.GetInt32() : null,
                args.TryGetValue("base_ref", out var br) ? br.GetString() ?? "HEAD" : "HEAD"),
            "worktree_list" => worktrees.ListAll(),
            "worktree_status" => worktrees.Status(args["name"].GetString()!),
            "worktree_run" => worktrees.Run(args["name"].GetString()!, args["command"].GetString()!),
            "worktree_remove" => worktrees.Remove(args["name"].GetString()!,
                args.TryGetValue("force", out var f) && f.GetBoolean(),
                args.TryGetValue("complete_task", out var ct) && ct.GetBoolean()),
            "worktree_keep" => worktrees.Keep(args["name"].GetString()!),
            "worktree_events" => events.ListRecent(args.TryGetValue("limit", out var lm) ? lm.GetInt32() : 20),
            _ => $"Unknown tool: {name}"
        };
    }
    catch (Exception ex) { return $"Error: {ex.Message}"; }
}

// =============================================================================
// Agent 循环
// =============================================================================

async Task AgentLoopAsync(List<MessageParam> messages)
{
    while (true)
    {
        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = modelId, Messages = [.. messages],
            System = systemPrompt, Tools = [.. tools], MaxTokens = 8000
        });

        foreach (var block in response.Content)
            if (block.TryPickText(out var text)) Console.WriteLine(text.Text);

        if (response.StopReason != StopReason.ToolUse)
        {
            var ac = response.Content.Select<ContentBlock, ContentBlockParam>(c =>
            {
                if (c.TryPickText(out var t)) return new TextBlockParam { Text = t.Text };
                if (c.TryPickToolUse(out var tu)) return new ToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input };
                throw new InvalidOperationException();
            }).ToList();
            messages.Add(new MessageParam { Role = Role.Assistant, Content = ac });
            return;
        }

        var toolCalls = response.Content.Where(c => c.TryPickToolUse(out _))
            .Select(c => { c.TryPickToolUse(out var tu); return tu!; }).ToList();

        var results = new List<ToolResultBlockParam>();
        foreach (var tc in toolCalls)
        {
            var output = await ExecuteToolAsync(tc.Name, tc.Input);
            Console.WriteLine($"\n> {tc.Name}: {(output.Length > 200 ? output[..200] + "..." : output)}");
            results.Add(new ToolResultBlockParam { ToolUseID = tc.ID, Content = output });
        }

        var ab = response.Content.Select<ContentBlock, ContentBlockParam>(c =>
        {
            if (c.TryPickText(out var t)) return new TextBlockParam { Text = t.Text };
            if (c.TryPickToolUse(out var tu)) return new ToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input };
            throw new InvalidOperationException();
        }).ToList();
        messages.Add(new MessageParam { Role = Role.Assistant, Content = ab });
        messages.Add(new MessageParam { Role = Role.User, Content = results.Select(r => (ContentBlockParam)r).ToList() });
    }
}

// =============================================================================
// REPL
// =============================================================================

Console.WriteLine($"Mini Claude Code v11 (Worktree + Task Isolation) - {workDir}");
Console.WriteLine($"Repo root: {repoRoot}  Git available: {worktrees.GitAvailable}");
Console.WriteLine("输入 'exit' 退出。\n");

var history = new List<MessageParam>();

while (true)
{
    Console.Write("You: ");
    var userInput = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(userInput) || userInput.ToLower() is "exit" or "quit" or "q") break;

    history.Add(new MessageParam { Role = Role.User, Content = userInput });
    try { await AgentLoopAsync(history); }
    catch (Exception ex) { Console.WriteLine($"Error: {ex.Message}"); }
    Console.WriteLine();
}

// =============================================================================
// EventBus
// =============================================================================

class EventBus
{
    private readonly string _path;

    public EventBus(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) File.WriteAllText(path, "");
    }

    public void Emit(string eventName, Dictionary<string, object>? task = null,
        Dictionary<string, object>? worktree = null, string? error = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["event"] = eventName,
            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            ["task"] = task ?? new(),
            ["worktree"] = worktree ?? new()
        };
        if (error != null) payload["error"] = error;
        File.AppendAllText(_path, JsonSerializer.Serialize(payload) + "\n");
    }

    public string ListRecent(int limit = 20)
    {
        var n = Math.Clamp(limit, 1, 200);
        var lines = File.ReadAllLines(_path).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        var recent = lines.Skip(Math.Max(0, lines.Length - n)).Take(n)
            .Select(l => { try { return JsonSerializer.Deserialize<object>(l); } catch { return new { @event = "parse_error", raw = l }; } }).ToList();
        return JsonSerializer.Serialize(recent, new JsonSerializerOptions { WriteIndented = true });
    }
}

// =============================================================================
// TaskManager
// =============================================================================

class TaskManager
{
    private readonly string _dir;
    private int _nextId;

    public TaskManager(string dir)
    {
        _dir = dir; Directory.CreateDirectory(dir);
        _nextId = MaxId() + 1;
    }

    private int MaxId()
    {
        var ids = Directory.GetFiles(_dir, "task_*.json")
            .Select(f => { var name = Path.GetFileNameWithoutExtension(f).Replace("task_", ""); return int.TryParse(name, out var id) ? id : 0; }).ToList();
        return ids.Count > 0 ? ids.Max() : 0;
    }

    private string TaskPath(int id) => Path.Combine(_dir, $"task_{id}.json");

    private Dictionary<string, object> Load(int id)
    {
        var p = TaskPath(id);
        if (!File.Exists(p)) throw new InvalidOperationException($"Task {id} not found");
        return JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(p))!;
    }

    private void Save(Dictionary<string, object> task)
        => File.WriteAllText(TaskPath(int.Parse(task["id"].ToString()!)), JsonSerializer.Serialize(task, new JsonSerializerOptions { WriteIndented = true }));

    public bool Exists(int id) => File.Exists(TaskPath(id));

    public string Create(string subject, string description = "")
    {
        var task = new Dictionary<string, object>
        {
            ["id"] = _nextId, ["subject"] = subject, ["description"] = description,
            ["status"] = "pending", ["owner"] = "", ["worktree"] = "",
            ["blockedBy"] = JsonSerializer.SerializeToElement(new List<int>()),
            ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            ["updated_at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0
        };
        Save(task); _nextId++;
        return JsonSerializer.Serialize(task, new JsonSerializerOptions { WriteIndented = true });
    }

    public string Get(int id) => JsonSerializer.Serialize(Load(id), new JsonSerializerOptions { WriteIndented = true });

    public string Update(int id, string? status = null, string? owner = null)
    {
        var task = Load(id);
        if (status != null)
        {
            if (status is not ("pending" or "in_progress" or "completed"))
                throw new InvalidOperationException($"Invalid status: {status}");
            task["status"] = status;
        }
        if (owner != null) task["owner"] = owner;
        task["updated_at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        Save(task);
        return JsonSerializer.Serialize(task, new JsonSerializerOptions { WriteIndented = true });
    }

    public string BindWorktree(int id, string worktree, string owner = "")
    {
        var task = Load(id);
        task["worktree"] = worktree;
        if (!string.IsNullOrEmpty(owner)) task["owner"] = owner;
        if (task["status"]?.ToString() == "pending") task["status"] = "in_progress";
        task["updated_at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        Save(task);
        return JsonSerializer.Serialize(task, new JsonSerializerOptions { WriteIndented = true });
    }

    public void UnbindWorktree(int id)
    {
        var task = Load(id);
        task["worktree"] = "";
        task["updated_at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        Save(task);
    }

    public string ListAll()
    {
        var files = Directory.GetFiles(_dir, "task_*.json").OrderBy(f => f).ToList();
        if (files.Count == 0) return "No tasks.";
        var lines = new List<string>();
        foreach (var f in files)
        {
            var t = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(f))!;
            var st = t.TryGetValue("status", out var sv) ? sv.ToString() : "?";
            var marker = st switch { "pending" => "[ ]", "in_progress" => "[>]", "completed" => "[x]", _ => "[?]" };
            var ow = t.TryGetValue("owner", out var ov) && ov.ToString() is { Length: > 0 } on ? $" owner={on}" : "";
            var wt = t.TryGetValue("worktree", out var wv) && wv.ToString() is { Length: > 0 } wn ? $" wt={wn}" : "";
            lines.Add($"{marker} #{t["id"]}: {t["subject"]}{ow}{wt}");
        }
        return string.Join("\n", lines);
    }
}

// =============================================================================
// WorktreeManager
// =============================================================================

class WorktreeManager
{
    private readonly string _repoRoot;
    private readonly string _dir;
    private readonly string _indexPath;
    private readonly TaskManager _tasks;
    private readonly EventBus _events;
    public bool GitAvailable { get; }

    public WorktreeManager(string repoRoot, TaskManager tasks, EventBus events)
    {
        _repoRoot = repoRoot; _tasks = tasks; _events = events;
        _dir = Path.Combine(repoRoot, ".worktrees");
        Directory.CreateDirectory(_dir);
        _indexPath = Path.Combine(_dir, "index.json");
        if (!File.Exists(_indexPath))
            File.WriteAllText(_indexPath, JsonSerializer.Serialize(new { worktrees = new List<object>() }, new JsonSerializerOptions { WriteIndented = true }));
        GitAvailable = IsGitRepo();
    }

    private bool IsGitRepo()
    {
        try
        {
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo
            {
                FileName = "git", Arguments = "rev-parse --is-inside-work-tree",
                WorkingDirectory = _repoRoot, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            p.Start(); p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private string RunGit(string args)
    {
        if (!GitAvailable) throw new InvalidOperationException("Not in a git repository.");
        using var p = new Process();
        p.StartInfo = new ProcessStartInfo
        {
            FileName = "git", Arguments = args,
            WorkingDirectory = _repoRoot, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        p.Start();
        var o = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrEmpty(o) ? $"git {args} failed" : o);
        return string.IsNullOrEmpty(o) ? "(no output)" : o;
    }

    private Dictionary<string, object> LoadIndex()
        => JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(_indexPath))!;

    private void SaveIndex(Dictionary<string, object> data)
        => File.WriteAllText(_indexPath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));

    private List<Dictionary<string, object>> GetWorktrees(Dictionary<string, object> idx)
        => idx["worktrees"] is JsonElement je ? je.Deserialize<List<Dictionary<string, object>>>() ?? [] : [];

    private Dictionary<string, object>? Find(string name)
        => GetWorktrees(LoadIndex()).FirstOrDefault(w => w.TryGetValue("name", out var n) && n.ToString() == name);

    private static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || !Regex.IsMatch(name, @"^[A-Za-z0-9._-]{1,40}$"))
            throw new InvalidOperationException("Invalid worktree name. Use 1-40 chars: letters, numbers, ., _, -");
    }

    public string Create(string name, int? taskId = null, string baseRef = "HEAD")
    {
        ValidateName(name);
        if (Find(name) != null) throw new InvalidOperationException($"Worktree '{name}' already exists");
        if (taskId.HasValue && !_tasks.Exists(taskId.Value))
            throw new InvalidOperationException($"Task {taskId} not found");

        var path = Path.Combine(_dir, name);
        var branch = $"wt/{name}";

        _events.Emit("worktree.create.before",
            taskId.HasValue ? new() { ["id"] = taskId.Value } : null,
            new() { ["name"] = name, ["base_ref"] = baseRef });

        try
        {
            RunGit($"worktree add -b {branch} \"{path}\" {baseRef}");

            var entry = new Dictionary<string, object>
            {
                ["name"] = name, ["path"] = path, ["branch"] = branch,
                ["task_id"] = taskId.HasValue ? taskId.Value : "",
                ["status"] = "active",
                ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0
            };

            var idx = LoadIndex();
            var wts = GetWorktrees(idx);
            wts.Add(entry);
            idx["worktrees"] = JsonSerializer.SerializeToElement(wts);
            SaveIndex(idx);

            if (taskId.HasValue) _tasks.BindWorktree(taskId.Value, name);

            _events.Emit("worktree.create.after",
                taskId.HasValue ? new() { ["id"] = taskId.Value } : null,
                new() { ["name"] = name, ["path"] = path, ["branch"] = branch, ["status"] = "active" });

            return JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _events.Emit("worktree.create.failed",
                taskId.HasValue ? new() { ["id"] = taskId.Value } : null,
                new() { ["name"] = name, ["base_ref"] = baseRef }, ex.Message);
            throw;
        }
    }

    public string ListAll()
    {
        var wts = GetWorktrees(LoadIndex());
        if (wts.Count == 0) return "No worktrees in index.";
        var lines = new List<string>();
        foreach (var wt in wts)
        {
            var tid = wt.TryGetValue("task_id", out var t) && t.ToString() is { Length: > 0 } ts ? $" task={ts}" : "";
            lines.Add($"[{wt.GetValueOrDefault("status", "?")}] {wt["name"]} -> {wt["path"]} ({wt.GetValueOrDefault("branch", "-")}){tid}");
        }
        return string.Join("\n", lines);
    }

    public string Status(string name)
    {
        var wt = Find(name);
        if (wt == null) return $"Error: Unknown worktree '{name}'";
        var path = wt["path"]?.ToString();
        if (!Directory.Exists(path)) return $"Error: Worktree path missing: {path}";
        try
        {
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo
            {
                FileName = "git", Arguments = "status --short --branch",
                WorkingDirectory = path, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            p.Start();
            var o = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
            p.WaitForExit();
            return string.IsNullOrEmpty(o) ? "Clean worktree" : o;
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    public string Run(string name, string command)
    {
        string[] dangerous = ["rm -rf /", "sudo", "shutdown", "reboot", "> /dev/"];
        if (dangerous.Any(d => command.Contains(d))) return "Error: Dangerous command blocked";

        var wt = Find(name);
        if (wt == null) return $"Error: Unknown worktree '{name}'";
        var path = wt["path"]?.ToString();
        if (!Directory.Exists(path)) return $"Error: Worktree path missing: {path}";

        try
        {
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash",
                Arguments = OperatingSystem.IsWindows() ? $"/c {command}" : $"-c \"{command.Replace("\"", "\\\"")}\"",
                WorkingDirectory = path, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            p.Start();
            var o = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
            p.WaitForExit();
            return string.IsNullOrEmpty(o) ? "(no output)" : o[..Math.Min(o.Length, 50000)];
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    public string Remove(string name, bool force = false, bool completeTask = false)
    {
        var wt = Find(name);
        if (wt == null) return $"Error: Unknown worktree '{name}'";

        var tid = wt.TryGetValue("task_id", out var t) ? t.ToString() : null;

        _events.Emit("worktree.remove.before",
            !string.IsNullOrEmpty(tid) ? new() { ["id"] = tid } : null,
            new() { ["name"] = name, ["path"] = wt.GetValueOrDefault("path", "")?.ToString()! });

        try
        {
            var gitArgs = force ? $"worktree remove --force \"{wt["path"]}\"" : $"worktree remove \"{wt["path"]}\"";
            RunGit(gitArgs);

            if (completeTask && !string.IsNullOrEmpty(tid) && int.TryParse(tid, out var taskId))
            {
                _tasks.Update(taskId, status: "completed");
                _tasks.UnbindWorktree(taskId);
                _events.Emit("task.completed", new() { ["id"] = taskId, ["status"] = "completed" }, new() { ["name"] = name });
            }

            var idx = LoadIndex();
            var wts = GetWorktrees(idx);
            var item = wts.FirstOrDefault(w => w.TryGetValue("name", out var n) && n.ToString() == name);
            if (item != null)
            {
                item["status"] = "removed";
                item["removed_at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            }
            idx["worktrees"] = JsonSerializer.SerializeToElement(wts);
            SaveIndex(idx);

            _events.Emit("worktree.remove.after",
                !string.IsNullOrEmpty(tid) ? new() { ["id"] = tid } : null,
                new() { ["name"] = name, ["status"] = "removed" });

            return $"Removed worktree '{name}'";
        }
        catch (Exception ex)
        {
            _events.Emit("worktree.remove.failed", null, new() { ["name"] = name }, ex.Message);
            throw;
        }
    }

    public string Keep(string name)
    {
        var wt = Find(name);
        if (wt == null) return $"Error: Unknown worktree '{name}'";

        var idx = LoadIndex();
        var wts = GetWorktrees(idx);
        Dictionary<string, object>? kept = null;
        foreach (var item in wts)
        {
            if (item.TryGetValue("name", out var n) && n.ToString() == name)
            {
                item["status"] = "kept";
                item["kept_at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                kept = item;
            }
        }
        idx["worktrees"] = JsonSerializer.SerializeToElement(wts);
        SaveIndex(idx);

        var tid = wt.TryGetValue("task_id", out var t) ? t.ToString() : null;
        _events.Emit("worktree.keep",
            !string.IsNullOrEmpty(tid) ? new() { ["id"] = tid } : null,
            new() { ["name"] = name, ["status"] = "kept" });

        return kept != null ? JsonSerializer.Serialize(kept, new JsonSerializerOptions { WriteIndented = true }) : $"Error: '{name}'";
    }
}
