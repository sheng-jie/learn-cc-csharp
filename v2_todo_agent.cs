#!/usr/bin/dotnet run

#:sdk Microsoft.NET.Sdk

#:package Anthropic@12.2.0
#:package dotenv.net@4.0.0

#:property LangVersion=latest
#:property ImplicitUsings=enable
#:property PublishAot=false

/*
 * v2_todo_agent.cs - Mini Claude Code: Structured Planning (~300 lines)
 *
 * 核心哲学: "让计划显式化"
 * ========================
 * v1 对简单任务很好用。但让它"重构 auth、添加测试、更新文档"，
 * 看看会发生什么。没有显式规划，模型会:
 *   - 随机跳转任务
 *   - 忘记已完成的步骤
 *   - 中途失焦
 *
 * 问题 - "上下文衰减":
 * ------------------
 * 在 v1 中，计划只存在于模型的"脑海"中:
 *
 *     v1: "我先做 A，然后 B，然后 C" (不可见)
 *         10 次工具调用后: "等等，我在干什么来着？"
 *
 * 解决方案 - TodoWrite 工具:
 * -------------------------
 * v2 添加一个新工具，从根本上改变 Agent 的工作方式:
 *
 *     v2:
 *       [ ] 重构 auth 模块
 *       [>] 添加单元测试         <- 当前正在做这个
 *       [ ] 更新文档
 *
 * 现在你和模型都能看到计划。模型可以:
 *   - 工作时更新状态
 *   - 看到什么完成了、下一步是什么
 *   - 一次专注一件事
 *
 * 关键约束（不是随意的 - 这些是护栏）:
 * -----------------------------------
 *     | 规则           | 原因                          |
 *     |---------------|-------------------------------|
 *     | 最多 20 项     | 防止无限任务列表                |
 *     | 一个 in_progress | 强制一次只做一件事            |
 *     | 必填字段       | 确保结构化输出                 |
 *
 * 深层洞察:
 * --------
 * > "约束既限制又赋能。"
 *
 * Todo 约束（最大项数、一个 in_progress）赋能（可见计划、追踪进度）。
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
// TodoManager - v2 的核心新增
// =============================================================================

var todo = new TodoManager();

// =============================================================================
// 系统提示 - 为 v2 更新
// =============================================================================

var systemPrompt = $"""
    你是一个位于 {workDir} 的编程代理。

    循环: 规划 -> 使用工具行动 -> 更新 todos -> 报告。

    规则:
    - 使用 TodoWrite 追踪多步骤任务
    - 开始前标记任务为 in_progress，完成后标记为 completed
    - 行动优先，不要只是解释。
    - 完成后，总结做了什么改动。
    """;

// =============================================================================
// 系统提醒 - 鼓励使用 todo 的软提示
// =============================================================================

const string INITIAL_REMINDER = "<reminder>对于多步骤任务请使用 TodoWrite。</reminder>";
const string NAG_REMINDER = "<reminder>10+ 轮未更新 todo。请更新 todos。</reminder>";

// =============================================================================
// 工具定义 (v1 工具 + TodoWrite)
// =============================================================================

var tools = new List<Tool>
{
    // v1 工具（不变）
    new()
    {
        Name = "bash",
        Description = "运行 shell 命令。",
        InputSchema = new InputSchema
        {
            Properties = new Dictionary<string, JsonElement>
            {
                ["command"] = JsonSerializer.SerializeToElement(new { type = "string" })
            },
            Required = ["command"]
        }
    },
    new()
    {
        Name = "read_file",
        Description = "读取文件内容。",
        InputSchema = new InputSchema
        {
            Properties = new Dictionary<string, JsonElement>
            {
                ["path"] = JsonSerializer.SerializeToElement(new { type = "string" }),
                ["limit"] = JsonSerializer.SerializeToElement(new { type = "integer" })
            },
            Required = ["path"]
        }
    },
    new()
    {
        Name = "write_file",
        Description = "写入内容到文件。",
        InputSchema = new InputSchema
        {
            Properties = new Dictionary<string, JsonElement>
            {
                ["path"] = JsonSerializer.SerializeToElement(new { type = "string" }),
                ["content"] = JsonSerializer.SerializeToElement(new { type = "string" })
            },
            Required = ["path", "content"]
        }
    },
    new()
    {
        Name = "edit_file",
        Description = "替换文件中的精确文本。",
        InputSchema = new InputSchema
        {
            Properties = new Dictionary<string, JsonElement>
            {
                ["path"] = JsonSerializer.SerializeToElement(new { type = "string" }),
                ["old_text"] = JsonSerializer.SerializeToElement(new { type = "string" }),
                ["new_text"] = JsonSerializer.SerializeToElement(new { type = "string" })
            },
            Required = ["path", "old_text", "new_text"]
        }
    },
    // v2 新增: TodoWrite
    new()
    {
        Name = "TodoWrite",
        Description = "更新任务列表。用于规划和追踪进度。",
        InputSchema = new InputSchema
        {
            Properties = new Dictionary<string, JsonElement>
            {
                ["items"] = JsonSerializer.SerializeToElement(new
                {
                    type = "array",
                    description = "完整的任务列表（替换现有）",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            content = new { type = "string", description = "任务描述" },
                            status = new { type = "string", @enum = new[] { "pending", "in_progress", "completed" } },
                            activeForm = new { type = "string", description = "现在进行时动作，如'正在读取文件'" }
                        },
                        required = new[] { "content", "status", "activeForm" }
                    }
                })
            },
            Required = ["items"]
        }
    }
};

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
    string[] dangerous = ["rm -rf /", "sudo", "shutdown", "reboot"];
    if (dangerous.Any(d => command.Contains(d)))
        return "Error: 危险命令已阻止";

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
    catch (Exception ex)
    {
        return $"Error: {ex.Message}";
    }
}

string RunRead(string path, int? limit = null)
{
    try
    {
        var lines = File.ReadAllLines(SafePath(path));
        if (limit.HasValue && limit.Value < lines.Length)
            lines = [.. lines.Take(limit.Value), $"... ({lines.Length - limit.Value} more)"];
        var result = string.Join("\n", lines);
        return result[..Math.Min(result.Length, 50000)];
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
        var content = File.ReadAllText(fullPath);
        if (!content.Contains(oldText))
            return $"Error: 在 {path} 中未找到文本";
        var index = content.IndexOf(oldText);
        File.WriteAllText(fullPath, content[..index] + newText + content[(index + oldText.Length)..]);
        return $"Edited {path}";
    }
    catch (Exception ex) { return $"Error: {ex.Message}"; }
}

string RunTodo(JsonElement items)
{
    try { return todo.Update(items); }
    catch (Exception ex) { return $"Error: {ex.Message}"; }
}

async Task<string> ExecuteToolAsync(string name, IReadOnlyDictionary<string, JsonElement> args)
{
    return name switch
    {
        "bash" => await RunBashAsync(args["command"].GetString()!),
        "read_file" => RunRead(
            args["path"].GetString()!,
            args.TryGetValue("limit", out var limit) ? limit.GetInt32() : null),
        "write_file" => RunWrite(
            args["path"].GetString()!,
            args["content"].GetString()!),
        "edit_file" => RunEdit(
            args["path"].GetString()!,
            args["old_text"].GetString()!,
            args["new_text"].GetString()!),
        "TodoWrite" => RunTodo(args["items"]),
        _ => $"Unknown tool: {name}"
    };
}

// =============================================================================
// Agent 循环（带 todo 追踪）
// =============================================================================

int roundsWithoutTodo = 0;

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

        var toolCalls = new List<ToolUseBlock>();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var text))
                Console.WriteLine(text.Text);
            if (block.TryPickToolUse(out var toolUse))
                toolCalls.Add(toolUse);
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
        var usedTodo = false;

        foreach (var tc in toolCalls)
        {
            Console.WriteLine($"\n> {tc.Name}");
            var output = await ExecuteToolAsync(tc.Name, tc.Input);
            var preview = output.Length > 300 ? output[..300] + "..." : output;
            Console.WriteLine($"  {preview}");

            results.Add(new ToolResultBlockParam { ToolUseID = tc.ID, Content = output });

            if (tc.Name == "TodoWrite")
                usedTodo = true;
        }

        // 更新计数器
        if (usedTodo)
            roundsWithoutTodo = 0;
        else
            roundsWithoutTodo++;

        var assistantBlocks = response.Content.Select<ContentBlock, ContentBlockParam>(c =>
        {
            if (c.TryPickText(out var t)) return new TextBlockParam { Text = t.Text };
            if (c.TryPickToolUse(out var tu)) return new ToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input };
            throw new InvalidOperationException("Unknown content block type");
        }).ToList();
        messages.Add(new MessageParam { Role = Role.Assistant, Content = assistantBlocks });

        // 如果模型很久没用 todos，注入提醒
        var userContent = results.Select(r => (ContentBlockParam)r).ToList();
        if (roundsWithoutTodo > 10)
            userContent.Insert(0, new TextBlockParam { Text = NAG_REMINDER });

        messages.Add(new MessageParam { Role = Role.User, Content = userContent });
    }
}

// =============================================================================
// 主 REPL
// =============================================================================

Console.WriteLine($"Mini Claude Code v2 (with Todos) - {workDir}");
Console.WriteLine("输入 'exit' 退出。\n");

var history = new List<MessageParam>();
var firstMessage = true;

while (true)
{
    Console.Write("You: ");
    var userInput = Console.ReadLine()?.Trim();

    if (string.IsNullOrEmpty(userInput) || userInput.ToLower() is "exit" or "quit" or "q")
        break;

    // 构建用户消息内容
    var content = new List<ContentBlockParam>();

    if (firstMessage)
    {
        content.Add(new TextBlockParam { Text = INITIAL_REMINDER });
        firstMessage = false;
    }

    content.Add(new TextBlockParam { Text = userInput });
    history.Add(new MessageParam { Role = Role.User, Content = content });

    try
    {
        await AgentLoopAsync(history);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
    }

    Console.WriteLine();
}

// =============================================================================
// 类型定义 (必须在顶层语句之后)
// =============================================================================

record TodoItem(string Content, string Status, string ActiveForm);

class TodoManager
{
    /*
     * 带有强制约束的结构化任务列表管理器。
     *
     * 关键设计决策:
     * 1. 最多 20 项: 防止模型创建无尽列表
     * 2. 一个 in_progress: 强制专注 - 一次只能做一件事
     * 3. 必填字段: 每项需要 content, status, activeForm
     *
     * activeForm 字段解释:
     * - 正在发生的事情的现在进行时形式
     * - 状态为 "in_progress" 时显示
     * - 例如: content="添加测试", activeForm="正在添加单元测试..."
     */
    
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

            if (string.IsNullOrEmpty(content))
                throw new InvalidOperationException($"Item: content required");
            if (status is not ("pending" or "in_progress" or "completed"))
                throw new InvalidOperationException($"Item: invalid status '{status}'");
            if (string.IsNullOrEmpty(activeForm))
                throw new InvalidOperationException($"Item: activeForm required");

            if (status == "in_progress")
                inProgressCount++;

            validated.Add(new TodoItem(content, status, activeForm));
        }

        if (validated.Count > 20)
            throw new InvalidOperationException("Max 20 todos allowed");
        if (inProgressCount > 1)
            throw new InvalidOperationException("Only one task can be in_progress at a time");

        _items = validated;
        return Render();
    }

    public string Render()
    {
        if (_items.Count == 0)
            return "No todos.";

        var sb = new StringBuilder();
        foreach (var item in _items)
        {
            var mark = item.Status switch
            {
                "completed" => "[x]",
                "in_progress" => "[>]",
                _ => "[ ]"
            };
            
            if (item.Status == "in_progress")
                sb.AppendLine($"{mark} {item.Content} <- {item.ActiveForm}");
            else
                sb.AppendLine($"{mark} {item.Content}");
        }

        var completed = _items.Count(t => t.Status == "completed");
        sb.AppendLine($"\n({completed}/{_items.Count} completed)");
        return sb.ToString();
    }
}
