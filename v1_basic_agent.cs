#!/usr/bin/dotnet run

#:sdk Microsoft.NET.Sdk

#:package Anthropic@12.2.0
#:package dotenv.net@4.0.0

#:property LangVersion=latest
#:property ImplicitUsings=enable
#:property PublishAot=false

/*
 * v1_basic_agent.cs - Mini Claude Code: Model as Agent (~200 lines)
 *
 * 核心哲学: "模型即代理"
 * =====================
 * Claude Code、Cursor Agent、Codex CLI 的秘密？没有秘密。
 *
 * 剥离 CLI 美化、进度条、权限系统。剩下的出奇简单：
 * 一个让模型持续调用工具直到完成的循环。
 *
 * 传统助手:
 *     用户 -> 模型 -> 文本回复
 *
 * Agent 系统:
 *     用户 -> 模型 -> [工具 -> 结果]* -> 回复
 *                          ^________|
 *
 * 星号 (*) 很重要！模型重复调用工具直到它认为任务完成。
 * 这将聊天机器人转变为自主 Agent。
 *
 * 关键洞察: 模型是决策者。代码只提供工具并运行循环。
 * 模型决定:
 *   - 调用哪些工具
 *   - 以什么顺序
 *   - 何时停止
 *
 * 四个核心工具:
 * ------------
 * Claude Code 有约 20 个工具。但这 4 个覆盖 90% 的使用场景：
 *
 *     | 工具       | 用途              | 示例                       |
 *     |-----------|-------------------|---------------------------|
 *     | bash      | 运行任何命令        | npm install, git status   |
 *     | read_file | 读取文件内容        | 查看 src/index.ts          |
 *     | write_file| 创建/覆盖文件      | 创建 README.md             |
 *     | edit_file | 精确修改          | 替换一个函数                |
 *
 * 用法:
 *     dotnet run v1_basic_agent.cs
 */

using System.Diagnostics;
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
// 系统提示 - 模型唯一需要的"配置"
// =============================================================================

var systemPrompt = $"""
    你是一个位于 {workDir} 的编程代理。

    循环: 简短思考 -> 使用工具 -> 报告结果。

    规则:
    - 行动优先，不要只是解释。
    - 不要凭空想象文件路径。如果不确定，先用 bash ls/find 查看。
    - 最小化修改。不要过度工程化。
    - 完成后，总结做了什么改动。
    """;

// =============================================================================
// 工具定义 - 4 个工具覆盖 90% 的编程任务
// =============================================================================

var tools = new List<Tool>
{
    // 工具 1: Bash - 通往一切的大门
    new()
    {
        Name = "bash",
        Description = "运行 shell 命令。用于: ls, find, grep, git, npm, dotnet 等。",
        InputSchema = new InputSchema
        {
            Properties = new Dictionary<string, JsonElement>
            {
                ["command"] = JsonSerializer.SerializeToElement(new { type = "string", description = "要执行的命令" })
            },
            Required = ["command"]
        }
    },
    // 工具 2: 读取文件 - 理解现有代码
    new()
    {
        Name = "read_file",
        Description = "读取文件内容。返回 UTF-8 文本。",
        InputSchema = new InputSchema
        {
            Properties = new Dictionary<string, JsonElement>
            {
                ["path"] = JsonSerializer.SerializeToElement(new { type = "string", description = "文件的相对路径" }),
                ["limit"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "最大读取行数（默认: 全部）" })
            },
            Required = ["path"]
        }
    },
    // 工具 3: 写入文件 - 创建新文件或完全重写
    new()
    {
        Name = "write_file",
        Description = "将内容写入文件。如果需要会创建父目录。",
        InputSchema = new InputSchema
        {
            Properties = new Dictionary<string, JsonElement>
            {
                ["path"] = JsonSerializer.SerializeToElement(new { type = "string", description = "文件的相对路径" }),
                ["content"] = JsonSerializer.SerializeToElement(new { type = "string", description = "要写入的内容" })
            },
            Required = ["path", "content"]
        }
    },
    // 工具 4: 编辑文件 - 对现有代码的精确修改
    new()
    {
        Name = "edit_file",
        Description = "替换文件中的精确文本。用于局部编辑。",
        InputSchema = new InputSchema
        {
            Properties = new Dictionary<string, JsonElement>
            {
                ["path"] = JsonSerializer.SerializeToElement(new { type = "string", description = "文件的相对路径" }),
                ["old_text"] = JsonSerializer.SerializeToElement(new { type = "string", description = "要查找的精确文本（必须完全匹配）" }),
                ["new_text"] = JsonSerializer.SerializeToElement(new { type = "string", description = "替换文本" })
            },
            Required = ["path", "old_text", "new_text"]
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
    // 基本安全检查 - 阻止危险命令
    string[] dangerous = ["rm -rf /", "sudo", "shutdown", "reboot", "> /dev/"];
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
    catch (OperationCanceledException)
    {
        return "Error: 命令超时 (60s)";
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
        {
            lines = [.. lines.Take(limit.Value), $"... ({lines.Length - limit.Value} more lines)"];
        }
        var result = string.Join("\n", lines);
        return result[..Math.Min(result.Length, 50000)];
    }
    catch (Exception ex)
    {
        return $"Error: {ex.Message}";
    }
}

string RunWrite(string path, string content)
{
    try
    {
        var fullPath = SafePath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(fullPath, content);
        return $"Wrote {content.Length} bytes to {path}";
    }
    catch (Exception ex)
    {
        return $"Error: {ex.Message}";
    }
}

string RunEdit(string path, string oldText, string newText)
{
    try
    {
        var fullPath = SafePath(path);
        var content = File.ReadAllText(fullPath);
        if (!content.Contains(oldText))
            return $"Error: 在 {path} 中未找到文本";
        
        // 只替换第一次出现，保证安全
        var index = content.IndexOf(oldText);
        var newContent = content[..index] + newText + content[(index + oldText.Length)..];
        File.WriteAllText(fullPath, newContent);
        return $"Edited {path}";
    }
    catch (Exception ex)
    {
        return $"Error: {ex.Message}";
    }
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
        _ => $"Unknown tool: {name}"
    };
}

// =============================================================================
// Agent 循环 - 这是一切的核心
// =============================================================================

async Task AgentLoopAsync(List<MessageParam> messages)
{
    /*
     * 所有编程代理共享的完整模式:
     *
     *     while True:
     *         response = model(messages, tools)
     *         if no tool calls: return
     *         execute tools, append results, continue
     *
     * 模型控制循环:
     *   - 持续调用工具直到 stop_reason != "tool_use"
     *   - 结果成为上下文（作为 "user" 消息反馈）
     *   - 记忆是自动的（消息列表累积历史）
     */
    while (true)
    {
        // 步骤 1: 调用模型
        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = modelId,
            Messages = [.. messages],
            System = systemPrompt,
            Tools = [.. tools],
            MaxTokens = 8000
        });

        // 步骤 2: 收集工具调用并打印文本输出
        var toolCalls = new List<ToolUseBlock>();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var text))
                Console.WriteLine(text.Text);
            if (block.TryPickToolUse(out var toolUse))
                toolCalls.Add(toolUse);
        }

        // 步骤 3: 如果没有工具调用，任务完成
        if (response.StopReason != StopReason.ToolUse)
        {
            // 将助手响应转换为 MessageParam 添加到历史
            var assistantContent = response.Content.Select<ContentBlock, ContentBlockParam>(c =>
            {
                if (c.TryPickText(out var t)) return new TextBlockParam { Text = t.Text };
                if (c.TryPickToolUse(out var tu)) return new ToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input };
                throw new InvalidOperationException("Unknown content block type");
            }).ToList();
            messages.Add(new MessageParam { Role = Role.Assistant, Content = assistantContent });
            return;
        }

        // 步骤 4: 执行每个工具并收集结果
        var results = new List<ToolResultBlockParam>();
        foreach (var tc in toolCalls)
        {
            // 显示正在执行什么
            Console.WriteLine($"\n> {tc.Name}: {tc.Input}");

            // 执行并显示结果预览
            var output = await ExecuteToolAsync(tc.Name, tc.Input);
            var preview = output.Length > 200 ? output[..200] + "..." : output;
            Console.WriteLine($"  {preview}");

            results.Add(new ToolResultBlockParam { ToolUseID = tc.ID, Content = output });
        }

        // 步骤 5: 添加到对话并继续
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

Console.WriteLine($"Mini Claude Code v1 (C#) - {workDir}");
Console.WriteLine("输入 'exit' 退出。\n");

var history = new List<MessageParam>();

while (true)
{
    Console.Write("You: ");
    var userInput = Console.ReadLine()?.Trim();

    if (string.IsNullOrEmpty(userInput) || userInput.ToLower() is "exit" or "quit" or "q")
        break;

    // 添加用户消息到历史
    history.Add(new MessageParam { Role = Role.User, Content = userInput });

    try
    {
        // 运行 Agent 循环
        await AgentLoopAsync(history);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
    }

    Console.WriteLine(); // 回合之间空行
}
