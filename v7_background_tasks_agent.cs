#!/usr/bin/dotnet run

#:sdk Microsoft.NET.Sdk

#:package Anthropic@12.2.0
#:package dotenv.net@4.0.0

#:property LangVersion=latest
#:property ImplicitUsings=enable
#:property PublishAot=false

/*
 * v7_background_tasks_agent.cs - Mini Claude Code: Background Tasks (~350 lines)
 *
 * 对应上游: s08_background_tasks.py
 *
 * 核心哲学: "发射即忘 —— Agent 不因长时间运行而阻塞"
 * =================================================
 * v6 有了持久化任务系统。但所有命令都是同步阻塞的：
 *
 *     Agent: 运行 `dotnet build` (等待 30 秒...)
 *     Agent: 运行 `npm install` (又等 20 秒...)
 *     Agent: 这两个本可以并行！
 *
 * 后台任务机制:
 * -----------
 *     主线程                    后台线程
 *     +-----------------+       +-----------------+
 *     | agent loop      |       | 任务执行         |
 *     | ...             |       | ...             |
 *     | [LLM call] <---+------- | enqueue(result) |
 *     |  ^drain queue   |       +-----------------+
 *     +-----------------+
 *
 *     时间线:
 *     Agent ----[spawn A]----[spawn B]----[其他工作]----
 *                  |              |
 *                  v              v
 *               [A 运行]       [B 运行]        (并行)
 *                  |              |
 *                  +-- 通知队列 --> [结果注入]
 *
 * 通知排空:
 * --------
 * 每次 LLM 调用前，排空后台任务的完成通知队列，
 * 将结果作为 <background-results> 消息注入对话。
 */

using System.Collections.Concurrent;
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

// =============================================================================
// BackgroundManager - v7 的核心新增
// =============================================================================

var bg = new BackgroundManager(workDir);

// =============================================================================
// 系统提示
// =============================================================================

var systemPrompt = $"""
    你是一个位于 {workDir} 的编程代理。

    对长时间运行的命令使用 background_run (如 build, test, install)。
    对快速命令使用 bash。

    规则:
    - 对可能超过几秒的命令使用 background_run
    - 用 check_background 检查后台任务状态
    - 后台结果会在 <background-results> 中自动注入
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
            RedirectStandardOutput = true, RedirectStandardError = true,
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
// 工具定义 (6 个工具)
// =============================================================================

var tools = new List<Tool>
{
    new() { Name = "bash", Description = "运行 shell 命令 (阻塞式)。",
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
    new() { Name = "background_run", Description = "在后台线程运行命令。立即返回 task_id。",
        InputSchema = new InputSchema {
            Properties = new Dictionary<string, JsonElement> {
                ["command"] = JsonSerializer.SerializeToElement(new { type = "string" }) },
            Required = ["command"] } },
    new() { Name = "check_background", Description = "检查后台任务状态。省略 task_id 列出全部。",
        InputSchema = new InputSchema {
            Properties = new Dictionary<string, JsonElement> {
                ["task_id"] = JsonSerializer.SerializeToElement(new { type = "string" }) },
            Required = [] } }
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
        "background_run" => bg.Run(args["command"].GetString()!),
        "check_background" => bg.Check(args.TryGetValue("task_id", out var tid) ? tid.GetString() : null),
        _ => $"Unknown tool: {name}"
    };
}

// =============================================================================
// 主 Agent 循环 - 集成后台通知排空
// =============================================================================

async Task AgentLoopAsync(List<MessageParam> messages)
{
    while (true)
    {
        // 排空后台通知队列，注入结果
        var notifs = bg.DrainNotifications();
        if (notifs.Count > 0 && messages.Count > 0)
        {
            var notifText = string.Join("\n",
                notifs.Select(n => $"[bg:{n.TaskId}] {n.Status}: {n.Result}"));
            messages.Add(new MessageParam { Role = Role.User,
                Content = $"<background-results>\n{notifText}\n</background-results>" });
            messages.Add(new MessageParam { Role = Role.Assistant,
                Content = "Noted background results." });
        }

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

Console.WriteLine($"Mini Claude Code v7 (Background Tasks) - {workDir}");
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
// BackgroundManager 类型定义
// =============================================================================

record BgNotification(string TaskId, string Status, string Command, string Result);

class BackgroundManager
{
    private readonly ConcurrentDictionary<string, BgTaskInfo> _tasks = new();
    private readonly ConcurrentQueue<BgNotification> _notificationQueue = new();
    private readonly string _workDir;

    public BackgroundManager(string workDir) => _workDir = workDir;

    public string Run(string command)
    {
        var taskId = Guid.NewGuid().ToString()[..8];
        _tasks[taskId] = new BgTaskInfo("running", null, command);

        var thread = new Thread(() => Execute(taskId, command))
        { IsBackground = true };
        thread.Start();

        return $"Background task {taskId} started: {command[..Math.Min(command.Length, 80)]}";
    }

    private void Execute(string taskId, string command)
    {
        string output;
        string status;

        try
        {
            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash",
                Arguments = OperatingSystem.IsWindows() ? $"/c {command}" : $"-c \"{command.Replace("\"", "\\\"")}\"",
                WorkingDirectory = _workDir,
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            proc.Start();

            if (!proc.WaitForExit(300_000)) // 5 分钟超时
            {
                proc.Kill();
                output = "Error: Timeout (300s)";
                status = "timeout";
            }
            else
            {
                output = (proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd()).Trim();
                output = string.IsNullOrEmpty(output) ? "(no output)" : output[..Math.Min(output.Length, 50000)];
                status = "completed";
            }
        }
        catch (Exception ex)
        {
            output = $"Error: {ex.Message}";
            status = "error";
        }

        _tasks[taskId] = new BgTaskInfo(status, output, command);
        _notificationQueue.Enqueue(new BgNotification(
            taskId, status,
            command[..Math.Min(command.Length, 80)],
            (output ?? "(no output)")[..Math.Min((output ?? "(no output)").Length, 500)]));
    }

    public string Check(string? taskId = null)
    {
        if (taskId != null)
        {
            if (!_tasks.TryGetValue(taskId, out var t))
                return $"Error: Unknown task {taskId}";
            return $"[{t.Status}] {t.Command[..Math.Min(t.Command.Length, 60)]}\n{t.Result ?? "(running)"}";
        }

        if (_tasks.IsEmpty) return "No background tasks.";
        return string.Join("\n", _tasks.Select(kv =>
            $"{kv.Key}: [{kv.Value.Status}] {kv.Value.Command[..Math.Min(kv.Value.Command.Length, 60)]}"));
    }

    public List<BgNotification> DrainNotifications()
    {
        var result = new List<BgNotification>();
        while (_notificationQueue.TryDequeue(out var n))
            result.Add(n);
        return result;
    }
}

record BgTaskInfo(string Status, string? Result, string Command);
