#!/usr/bin/dotnet run

#:sdk Microsoft.NET.Sdk

#:package Anthropic@12.2.0
#:package dotenv.net@4.0.0

#:property LangVersion=latest
#:property ImplicitUsings=enable
#:property PublishAot=false

/*
 * v6_task_system_agent.cs - Mini Claude Code: Task System (~400 lines)
 *
 * 对应上游: s07_task_system.py
 *
 * 核心哲学: "状态在上下文之外生存 —— 因为它在对话之外"
 * ====================================================
 * v5 解决了上下文窗口爆炸问题。但压缩会丢失信息。
 * Todo 列表存在于内存中 —— 一旦对话压缩，计划就消失了。
 *
 * Task vs Todo:
 * ------------
 *     | 维度     | Todo (v2)       | Task (v6)           |
 *     |---------|----------------|---------------------|
 *     | 存储    | 内存中          | 文件系统 (.tasks/)   |
 *     | 生命期  | 对话内          | 跨对话持久           |
 *     | 依赖    | 无              | blockedBy/blocks    |
 *     | 共享    | 单 agent        | 多 agent 可共享      |
 *     | 压缩安全 | 否              | 是                  |
 *
 * 依赖解析:
 * --------
 *     +----------+     +----------+     +----------+
 *     | task 1   | --> | task 2   | --> | task 3   |
 *     | complete |     | blocked  |     | blocked  |
 *     +----------+     +----------+     +----------+
 *          |                ^
 *          +--- 完成 task 1 会从 task 2 的 blockedBy 中移除它
 *
 * 数据结构:
 * --------
 *     .tasks/
 *       task_1.json  {"id":1, "subject":"...", "status":"completed", ...}
 *       task_2.json  {"id":2, "blockedBy":[1], "status":"pending", ...}
 *       task_3.json  {"id":3, "blockedBy":[2], "blocks":[], ...}
 */

using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
var tasksDir = Path.Combine(workDir, ".tasks");

// =============================================================================
// TaskManager - v6 的核心新增
// =============================================================================

var tasks = new TaskManager(tasksDir);

// =============================================================================
// 系统提示
// =============================================================================

var systemPrompt = $"""
    你是一个位于 {workDir} 的编程代理。

    使用 task 工具来规划和追踪工作。任务持久化在 .tasks/ 目录中，
    即使对话压缩也不会丢失。

    规则:
    - 对多步骤计划使用 task_create 创建任务
    - 用 task_update 更新状态和依赖关系
    - 用 task_list 查看全局进度
    - 行动优先，不要只是解释。
    - 完成后，总结做了什么改动。
    """;

// =============================================================================
// 工具实现
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
    if (dangerous.Any(d => command.Contains(d)))
        return "Error: Dangerous command blocked";

    try
    {
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = "bash",
            Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"",
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
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
// 工具定义 (8 个工具)
// =============================================================================

var tools = new List<Tool>
{
    new() { Name = "bash", Description = "运行 shell 命令。",
        InputSchema = new InputSchema {
            Properties = new Dictionary<string, JsonElement> {
                ["command"] = JsonSerializer.SerializeToElement(new { type = "string" }) },
            Required = ["command"] } },
    new() { Name = "read_file", Description = "读取文件内容。",
        InputSchema = new InputSchema {
            Properties = new Dictionary<string, JsonElement> {
                ["path"] = JsonSerializer.SerializeToElement(new { type = "string" }),
                ["limit"] = JsonSerializer.SerializeToElement(new { type = "integer" }) },
            Required = ["path"] } },
    new() { Name = "write_file", Description = "写入内容到文件。",
        InputSchema = new InputSchema {
            Properties = new Dictionary<string, JsonElement> {
                ["path"] = JsonSerializer.SerializeToElement(new { type = "string" }),
                ["content"] = JsonSerializer.SerializeToElement(new { type = "string" }) },
            Required = ["path", "content"] } },
    new() { Name = "edit_file", Description = "替换文件中的文本。",
        InputSchema = new InputSchema {
            Properties = new Dictionary<string, JsonElement> {
                ["path"] = JsonSerializer.SerializeToElement(new { type = "string" }),
                ["old_text"] = JsonSerializer.SerializeToElement(new { type = "string" }),
                ["new_text"] = JsonSerializer.SerializeToElement(new { type = "string" }) },
            Required = ["path", "old_text", "new_text"] } },
    new() { Name = "task_create", Description = "创建一个新任务。",
        InputSchema = new InputSchema {
            Properties = new Dictionary<string, JsonElement> {
                ["subject"] = JsonSerializer.SerializeToElement(new { type = "string" }),
                ["description"] = JsonSerializer.SerializeToElement(new { type = "string" }) },
            Required = ["subject"] } },
    new() { Name = "task_update", Description = "更新任务的状态或依赖关系。",
        InputSchema = new InputSchema {
            Properties = new Dictionary<string, JsonElement> {
                ["task_id"] = JsonSerializer.SerializeToElement(new { type = "integer" }),
                ["status"] = JsonSerializer.SerializeToElement(new { type = "string", @enum = new[] { "pending", "in_progress", "completed" } }),
                ["addBlockedBy"] = JsonSerializer.SerializeToElement(new { type = "array", items = new { type = "integer" } }),
                ["addBlocks"] = JsonSerializer.SerializeToElement(new { type = "array", items = new { type = "integer" } }) },
            Required = ["task_id"] } },
    new() { Name = "task_list", Description = "列出所有任务及状态摘要。",
        InputSchema = new InputSchema {
            Properties = new Dictionary<string, JsonElement>(),
            Required = [] } },
    new() { Name = "task_get", Description = "按 ID 获取任务详情。",
        InputSchema = new InputSchema {
            Properties = new Dictionary<string, JsonElement> {
                ["task_id"] = JsonSerializer.SerializeToElement(new { type = "integer" }) },
            Required = ["task_id"] } }
};

// =============================================================================
// 工具分发
// =============================================================================

async Task<string> ExecuteToolAsync(string name, IReadOnlyDictionary<string, JsonElement> args)
{
    return name switch
    {
        "bash" => await RunBashAsync(args["command"].GetString()!),
        "read_file" => RunRead(args["path"].GetString()!,
            args.TryGetValue("limit", out var l) ? l.GetInt32() : null),
        "write_file" => RunWrite(args["path"].GetString()!, args["content"].GetString()!),
        "edit_file" => RunEdit(args["path"].GetString()!,
            args["old_text"].GetString()!, args["new_text"].GetString()!),
        "task_create" => tasks.Create(args["subject"].GetString()!,
            args.TryGetValue("description", out var d) ? d.GetString() ?? "" : ""),
        "task_update" => tasks.Update(args["task_id"].GetInt32(),
            args.TryGetValue("status", out var s) ? s.GetString() : null,
            args.TryGetValue("addBlockedBy", out var bb) ? bb.EnumerateArray().Select(x => x.GetInt32()).ToList() : null,
            args.TryGetValue("addBlocks", out var bl) ? bl.EnumerateArray().Select(x => x.GetInt32()).ToList() : null),
        "task_list" => tasks.ListAll(),
        "task_get" => tasks.Get(args["task_id"].GetInt32()),
        _ => $"Unknown tool: {name}"
    };
}

// =============================================================================
// 主 Agent 循环
// =============================================================================

async Task AgentLoopAsync(List<MessageParam> messages)
{
    while (true)
    {
        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = modelId,
            Messages = [.. messages],
            System = systemPrompt,
            Tools = [.. tools],
            MaxTokens = 8000
        });

        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var text)) Console.WriteLine(text.Text);
        }

        if (response.StopReason != StopReason.ToolUse)
        {
            var assistantContent = response.Content.Select<ContentBlock, ContentBlockParam>(c =>
            {
                if (c.TryPickText(out var t)) return new TextBlockParam { Text = t.Text };
                if (c.TryPickToolUse(out var tu)) return new ToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input };
                throw new InvalidOperationException("Unknown content block type");
            }).ToList();
            messages.Add(new MessageParam { Role = Role.Assistant, Content = assistantContent });
            return;
        }

        var toolCalls = response.Content
            .Where(c => c.TryPickToolUse(out _))
            .Select(c => { c.TryPickToolUse(out var tu); return tu!; })
            .ToList();

        var results = new List<ToolResultBlockParam>();
        foreach (var tc in toolCalls)
        {
            var output = await ExecuteToolAsync(tc.Name, tc.Input);
            var preview = output.Length > 200 ? output[..200] + "..." : output;
            Console.WriteLine($"\n> {tc.Name}: {preview}");
            results.Add(new ToolResultBlockParam { ToolUseID = tc.ID, Content = output });
        }

        var assistantBlocks = response.Content.Select<ContentBlock, ContentBlockParam>(c =>
        {
            if (c.TryPickText(out var t)) return new TextBlockParam { Text = t.Text };
            if (c.TryPickToolUse(out var tu)) return new ToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input };
            throw new InvalidOperationException("Unknown content block type");
        }).ToList();
        messages.Add(new MessageParam { Role = Role.Assistant, Content = assistantBlocks });
        messages.Add(new MessageParam { Role = Role.User, Content = results.Select(r => (ContentBlockParam)r).ToList() });
    }
}

// =============================================================================
// 主 REPL
// =============================================================================

Console.WriteLine($"Mini Claude Code v6 (Task System) - {workDir}");
Console.WriteLine("输入 'exit' 退出。\n");

var history = new List<MessageParam>();

while (true)
{
    Console.Write("You: ");
    var userInput = Console.ReadLine()?.Trim();

    if (string.IsNullOrEmpty(userInput) || userInput.ToLower() is "exit" or "quit" or "q")
        break;

    history.Add(new MessageParam { Role = Role.User, Content = userInput });

    try { await AgentLoopAsync(history); }
    catch (Exception ex) { Console.WriteLine($"Error: {ex.Message}"); }

    Console.WriteLine();
}

// =============================================================================
// TaskManager 类型定义
// =============================================================================

class TaskManager
{
    private readonly string _dir;
    private int _nextId;

    public TaskManager(string tasksDir)
    {
        _dir = tasksDir;
        Directory.CreateDirectory(_dir);
        _nextId = MaxId() + 1;
    }

    private int MaxId()
    {
        var ids = Directory.GetFiles(_dir, "task_*.json")
            .Select(f => int.TryParse(Path.GetFileNameWithoutExtension(f).Split('_')[1], out var id) ? id : 0)
            .ToList();
        return ids.Count > 0 ? ids.Max() : 0;
    }

    private Dictionary<string, object> Load(int taskId)
    {
        var path = Path.Combine(_dir, $"task_{taskId}.json");
        if (!File.Exists(path)) throw new InvalidOperationException($"Task {taskId} not found");
        return JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(path))!;
    }

    private void Save(Dictionary<string, object> task)
    {
        var path = Path.Combine(_dir, $"task_{task["id"]}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(task, new JsonSerializerOptions { WriteIndented = true }));
    }

    public string Create(string subject, string description = "")
    {
        var task = new Dictionary<string, object>
        {
            ["id"] = _nextId,
            ["subject"] = subject,
            ["description"] = description,
            ["status"] = "pending",
            ["blockedBy"] = new List<int>(),
            ["blocks"] = new List<int>(),
            ["owner"] = ""
        };
        Save(task);
        _nextId++;
        return JsonSerializer.Serialize(task, new JsonSerializerOptions { WriteIndented = true });
    }

    public string Get(int taskId)
    {
        return JsonSerializer.Serialize(Load(taskId), new JsonSerializerOptions { WriteIndented = true });
    }

    public string Update(int taskId, string? status = null, List<int>? addBlockedBy = null, List<int>? addBlocks = null)
    {
        var task = Load(taskId);

        if (status != null)
        {
            if (status is not ("pending" or "in_progress" or "completed"))
                throw new InvalidOperationException($"Invalid status: {status}");
            task["status"] = status;
            if (status == "completed")
                ClearDependency(taskId);
        }

        if (addBlockedBy != null)
        {
            var current = GetIntList(task, "blockedBy");
            current.AddRange(addBlockedBy);
            task["blockedBy"] = current.Distinct().ToList();
        }

        if (addBlocks != null)
        {
            var current = GetIntList(task, "blocks");
            current.AddRange(addBlocks);
            task["blocks"] = current.Distinct().ToList();

            // 双向: 更新被阻塞任务的 blockedBy 列表
            foreach (var blockedId in addBlocks)
            {
                try
                {
                    var blocked = Load(blockedId);
                    var blockedBy = GetIntList(blocked, "blockedBy");
                    if (!blockedBy.Contains(taskId))
                    {
                        blockedBy.Add(taskId);
                        blocked["blockedBy"] = blockedBy;
                        Save(blocked);
                    }
                }
                catch { }
            }
        }

        Save(task);
        return JsonSerializer.Serialize(task, new JsonSerializerOptions { WriteIndented = true });
    }

    private void ClearDependency(int completedId)
    {
        foreach (var file in Directory.GetFiles(_dir, "task_*.json"))
        {
            var task = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(file))!;
            var blockedBy = GetIntList(task, "blockedBy");
            if (blockedBy.Remove(completedId))
            {
                task["blockedBy"] = blockedBy;
                Save(task);
            }
        }
    }

    public string ListAll()
    {
        var files = Directory.GetFiles(_dir, "task_*.json").OrderBy(f => f).ToList();
        if (files.Count == 0) return "No tasks.";

        var lines = new List<string>();
        foreach (var file in files)
        {
            var task = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(file))!;
            var status = task["status"].ToString()!;
            var marker = status switch { "pending" => "[ ]", "in_progress" => "[>]", "completed" => "[x]", _ => "[?]" };
            var blockedBy = GetIntList(task, "blockedBy");
            var blocked = blockedBy.Count > 0 ? $" (blocked by: [{string.Join(", ", blockedBy)}])" : "";
            lines.Add($"{marker} #{task["id"]}: {task["subject"]}{blocked}");
        }
        return string.Join("\n", lines);
    }

    private static List<int> GetIntList(Dictionary<string, object> task, string key)
    {
        if (!task.TryGetValue(key, out var val)) return [];
        if (val is JsonElement je && je.ValueKind == JsonValueKind.Array)
            return je.EnumerateArray().Select(x => x.GetInt32()).ToList();
        if (val is List<int> list) return list;
        return [];
    }
}
