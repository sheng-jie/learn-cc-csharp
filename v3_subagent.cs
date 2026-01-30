#!/usr/bin/dotnet run

#:sdk Microsoft.NET.Sdk

#:package Anthropic@12.2.0
#:package dotenv.net@4.0.0

#:property LangVersion=latest
#:property ImplicitUsings=enable
#:property PublishAot=false

/*
 * v3_subagent.cs - Mini Claude Code: Subagent Mechanism (~400 lines)
 *
 * 核心哲学: "分而治之，上下文隔离"
 * ===============================
 * v2 添加了规划。但对于大型任务如"探索代码库然后重构 auth"，
 * 单个 Agent 会遇到问题：
 *
 * 问题 - 上下文污染:
 * ----------------
 *     单 Agent 历史:
 *       [探索中...] cat file1.cs -> 500 行
 *       [探索中...] cat file2.cs -> 300 行
 *       ... 15 个文件 ...
 *       [现在重构...] "等等，file1 里有什么来着？"
 *
 * 模型的上下文被探索细节填满，实际任务没有空间了。这就是"上下文污染"。
 *
 * 解决方案 - 隔离上下文的子代理:
 * ----------------------------
 *     主 Agent 历史:
 *       [Task: 探索代码库]
 *         -> 子代理探索 20 个文件（在自己的上下文中）
 *         -> 只返回: "Auth 在 src/auth/，DB 在 src/models/"
 *       [现在用干净上下文重构]
 *
 * 每个子代理有:
 *   1. 自己的干净消息历史
 *   2. 过滤后的工具（explore 不能写）
 *   3. 专门的系统提示
 *   4. 只向父代理返回最终摘要
 *
 * 关键洞察:
 * --------
 *     进程隔离 = 上下文隔离
 *
 * Agent 类型注册表:
 * ----------------
 *     | 类型    | 工具             | 用途                     |
 *     |--------|------------------|-------------------------|
 *     | explore | bash, read_file  | 只读探索                 |
 *     | code    | 全部工具         | 完整实现访问              |
 *     | plan    | bash, read_file  | 设计但不修改             |
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

// =============================================================================
// Agent 类型注册表 - 子代理机制的核心
// =============================================================================

var agentTypes = new Dictionary<string, AgentConfig>
{
    ["explore"] = new(
        "只读代理，用于探索代码、查找文件、搜索",
        ["bash", "read_file"],
        "你是一个探索代理。搜索和分析，但不要修改文件。返回简洁的摘要。"
    ),
    ["code"] = new(
        "完整代理，用于实现功能和修复 bug",
        ["*"], // 所有工具
        "你是一个编程代理。高效地实现请求的更改。"
    ),
    ["plan"] = new(
        "规划代理，用于设计实现策略",
        ["bash", "read_file"],
        "你是一个规划代理。分析代码库并输出编号的实现计划。不要做更改。"
    )
};

string GetAgentDescriptions() => string.Join("\n",
    agentTypes.Select(kv => $"- {kv.Key}: {kv.Value.Description}"));

// =============================================================================
// TodoManager instance
// =============================================================================

var todo = new TodoManager();

// =============================================================================
// 系统提示
// =============================================================================

var systemPrompt = $"""
    你是一个位于 {workDir} 的编程代理。

    循环: 规划 -> 使用工具行动 -> 报告。

    你可以为复杂子任务生成子代理:
    {GetAgentDescriptions()}

    规则:
    - 对需要专注探索或实现的子任务使用 Task 工具
    - 使用 TodoWrite 追踪多步骤工作
    - 行动优先，不要只是解释。
    - 完成后，总结做了什么改动。
    """;

// =============================================================================
// 基础工具定义
// =============================================================================

var baseTools = new List<Tool>
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
    new() { Name = "TodoWrite", Description = "更新任务列表。",
        InputSchema = new InputSchema {
            Properties = new Dictionary<string, JsonElement> {
                ["items"] = JsonSerializer.SerializeToElement(new {
                    type = "array",
                    items = new {
                        type = "object",
                        properties = new {
                            content = new { type = "string" },
                            status = new { type = "string", @enum = new[] { "pending", "in_progress", "completed" } },
                            activeForm = new { type = "string" }
                        },
                        required = new[] { "content", "status", "activeForm" }
                    }
                }) },
            Required = ["items"] } }
};

// =============================================================================
// Task 工具 - v3 的核心新增
// =============================================================================

var taskTool = new Tool
{
    Name = "Task",
    Description = $"""
        为专注的子任务生成子代理。

        子代理在隔离上下文中运行 - 它们看不到父代理的历史。
        用这个来保持主对话干净。

        Agent 类型:
        {GetAgentDescriptions()}

        示例用法:
        - Task(explore): "查找所有使用 auth 模块的文件"
        - Task(plan): "设计数据库迁移策略"
        - Task(code): "实现用户注册表单"
        """,
    InputSchema = new InputSchema
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["description"] = JsonSerializer.SerializeToElement(new { type = "string", description = "简短任务名（3-5 词）用于进度显示" }),
            ["prompt"] = JsonSerializer.SerializeToElement(new { type = "string", description = "给子代理的详细指令" }),
            ["agent_type"] = JsonSerializer.SerializeToElement(new { type = "string", @enum = new[] { "explore", "code", "plan" } })
        },
        Required = ["description", "prompt", "agent_type"]
    }
};

var allTools = baseTools.Concat([taskTool]).ToList();

List<Tool> GetToolsForAgent(string agentType)
{
    var allowed = agentTypes.GetValueOrDefault(agentType)?.Tools ?? ["*"];
    if (allowed.Contains("*"))
        return baseTools; // 所有基础工具，但不包括 Task（防止无限递归）
    return baseTools.Where(t => allowed.Contains(t.Name)).ToList();
}

// =============================================================================
// 工具实现
// =============================================================================

string SafePath(string p)
{
    var fullPath = Path.GetFullPath(Path.Combine(workDir, p));
    if (!fullPath.StartsWith(workDir))
        throw new InvalidOperationException($"路径逃逸工作区: {p}");
    return fullPath;
}

async Task<string> RunBashAsync(string command)
{
    if (new[] { "rm -rf /", "sudo", "shutdown" }.Any(d => command.Contains(d)))
        return "Error: 危险命令";
    try
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "bash",
                Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"",
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(cts.Token);
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
        if (limit.HasValue) lines = lines.Take(limit.Value).ToArray();
        return string.Join("\n", lines)[..Math.Min(string.Join("\n", lines).Length, 50000)];
    }
    catch (Exception ex) { return $"Error: {ex.Message}"; }
}

string RunWrite(string path, string content)
{
    try
    {
        var fullPath = SafePath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(fullPath, content);
        return $"Wrote {content.Length} bytes to {path}";
    }
    catch (Exception ex) { return $"Error: {ex.Message}"; }
}

string RunEdit(string path, string oldText, string newText)
{
    try
    {
        var fullPath = SafePath(path);
        var text = File.ReadAllText(fullPath);
        if (!text.Contains(oldText)) return $"Error: 在 {path} 中未找到文本";
        var index = text.IndexOf(oldText);
        File.WriteAllText(fullPath, text[..index] + newText + text[(index + oldText.Length)..]);
        return $"Edited {path}";
    }
    catch (Exception ex) { return $"Error: {ex.Message}"; }
}

string RunTodo(JsonElement items)
{
    try { return todo.Update(items); }
    catch (Exception ex) { return $"Error: {ex.Message}"; }
}

// =============================================================================
// 子代理执行 - v3 的核心
// =============================================================================

async Task<string> RunTaskAsync(string description, string prompt, string agentType)
{
    /*
     * 带隔离上下文执行子代理任务。
     *
     * 这是子代理机制的核心:
     * 1. 创建隔离的消息历史（关键: 没有父代理上下文！）
     * 2. 使用特定于代理的系统提示
     * 3. 根据代理类型过滤可用工具
     * 4. 运行与主代理相同的查询循环
     * 5. 只返回最终文本（不是中间细节）
     */
    if (!agentTypes.TryGetValue(agentType, out var config))
        return $"Error: 未知 agent 类型 '{agentType}'";

    var subSystem = $"""
        你是一个位于 {workDir} 的 {agentType} 子代理。

        {config.Prompt}

        完成任务并返回清晰简洁的摘要。
        """;

    var subTools = GetToolsForAgent(agentType);
    var subMessages = new List<MessageParam> { new MessageParam { Role = Role.User, Content = prompt } };

    Console.WriteLine($"  [{agentType}] {description}");
    var start = DateTime.Now;
    var toolCount = 0;

    while (true)
    {
        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = modelId,
            Messages = [.. subMessages],
            System = subSystem,
            Tools = [.. subTools],
            MaxTokens = 8000
        });

        if (response.StopReason != StopReason.ToolUse)
        {
            var elapsed = (DateTime.Now - start).TotalSeconds;
            Console.WriteLine($"\r  [{agentType}] {description} - done ({toolCount} tools, {elapsed:F1}s)");
            return string.Join("", response.Content
                .Where(c => c.TryPickText(out _))
                .Select(c => { c.TryPickText(out var t); return t!.Text; }));
        }

        var toolCalls = response.Content
            .Where(c => c.TryPickToolUse(out _))
            .Select(c => { c.TryPickToolUse(out var tu); return tu!; })
            .ToList();
        var results = new List<ToolResultBlockParam>();

        foreach (var tc in toolCalls)
        {
            toolCount++;
            var output = await ExecuteToolAsync(tc.Name, tc.Input);
            results.Add(new ToolResultBlockParam { ToolUseID = tc.ID, Content = output });

            var elapsed = (DateTime.Now - start).TotalSeconds;
            Console.Write($"\r  [{agentType}] {description} ... {toolCount} tools, {elapsed:F1}s");
        }

        var assistantBlocks = response.Content.Select<ContentBlock, ContentBlockParam>(c =>
        {
            if (c.TryPickText(out var t)) return new TextBlockParam { Text = t.Text };
            if (c.TryPickToolUse(out var tu)) return new ToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input };
            throw new InvalidOperationException("Unknown content block type");
        }).ToList();
        subMessages.Add(new MessageParam { Role = Role.Assistant, Content = assistantBlocks });
        subMessages.Add(new MessageParam { Role = Role.User, Content = results.Select(r => (ContentBlockParam)r).ToList() });
    }
}

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
        "TodoWrite" => RunTodo(args["items"]),
        "Task" => await RunTaskAsync(
            args["description"].GetString()!,
            args["prompt"].GetString()!,
            args["agent_type"].GetString()!),
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
            Tools = [.. allTools],
            MaxTokens = 8000
        });

        var toolCalls = new List<ToolUseBlock>();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var text)) Console.WriteLine(text.Text);
            if (block.TryPickToolUse(out var toolUse)) toolCalls.Add(toolUse);
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

        var results = new List<ToolResultBlockParam>();
        foreach (var tc in toolCalls)
        {
            if (tc.Name == "Task")
                Console.WriteLine($"\n> Task: {tc.Input["description"].GetString()}");
            else
                Console.WriteLine($"\n> {tc.Name}");

            var output = await ExecuteToolAsync(tc.Name, tc.Input);

            if (tc.Name != "Task")
            {
                var preview = output.Length > 200 ? output[..200] + "..." : output;
                Console.WriteLine($"  {preview}");
            }

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

Console.WriteLine($"Mini Claude Code v3 (with Subagents) - {workDir}");
Console.WriteLine($"Agent 类型: {string.Join(", ", agentTypes.Keys)}");
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
// 类型定义 (必须在顶层语句之后)
// =============================================================================

record AgentConfig(string Description, string[] Tools, string Prompt);

record TodoItem(string Content, string Status, string ActiveForm);

class TodoManager
{
    private List<TodoItem> _items = [];

    public string Update(JsonElement itemsJson)
    {
        var validated = new List<TodoItem>();
        var inProgressCount = 0;

        foreach (var item in itemsJson.EnumerateArray())
        {
            var content = item.TryGetProperty("content", out var c) ? c.GetString()?.Trim() ?? "" : "";
            var status = item.TryGetProperty("status", out var s) ? s.GetString()?.ToLower() ?? "pending" : "pending";
            var activeForm = item.TryGetProperty("activeForm", out var a) ? a.GetString()?.Trim() ?? "" : "";

            if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(activeForm))
                throw new InvalidOperationException("content and activeForm required");
            if (status is not ("pending" or "in_progress" or "completed"))
                throw new InvalidOperationException($"invalid status: {status}");
            if (status == "in_progress") inProgressCount++;

            validated.Add(new TodoItem(content, status, activeForm));
        }

        if (inProgressCount > 1)
            throw new InvalidOperationException("Only one task can be in_progress");

        _items = validated.Take(20).ToList();
        return Render();
    }

    public string Render()
    {
        if (_items.Count == 0) return "No todos.";
        var sb = new StringBuilder();
        foreach (var t in _items)
        {
            var mark = t.Status == "completed" ? "[x]" : t.Status == "in_progress" ? "[>]" : "[ ]";
            sb.AppendLine($"{mark} {t.Content}");
        }
        var done = _items.Count(t => t.Status == "completed");
        return sb.ToString() + $"({done}/{_items.Count} done)";
    }
}
