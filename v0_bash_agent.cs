#!/usr/bin/dotnet run

#:sdk Microsoft.NET.Sdk

#:package Anthropic@12.2.0
#:package dotenv.net@4.0.0

#:property LangVersion=latest
#:property ImplicitUsings=enable
#:property PublishAot=false

/*
 * v0_bash_agent.cs - Mini Claude Code: Bash is All You Need (~60 lines core)
 *
 * 核心哲学: "Bash 就是一切"
 * ========================
 * 这是 Agent 的终极简化。构建了 v1-v3 之后，我们追问：Agent 的本质是什么？
 *
 * 答案: 一个工具 (bash) + 一个循环 = 完整的 Agent 能力
 *
 * 为什么 Bash 就够了:
 * -----------------
 * Unix 哲学说一切皆文件，一切皆可管道。Bash 是通往这个世界的大门：
 *
 *     | 你需要      | Bash 命令                              |
 *     |------------|----------------------------------------|
 *     | 读取文件    | cat, head, tail, grep                  |
 *     | 写入文件    | echo '...' > file, cat << 'EOF' > file |
 *     | 搜索       | find, grep, rg, ls                      |
 *     | 执行       | python, npm, dotnet, 任何命令           |
 *     | 子代理     | dotnet run v0_bash_agent.cs "task"      |
 *
 * 最后一行是关键洞察：通过 bash 调用自身实现子代理！
 * 无需 Task 工具，无需 Agent 注册表 - 只需进程生成的递归。
 *
 * 子代理工作原理:
 * -------------
 *     主 Agent
 *       |-- bash: dotnet run v0_bash_agent.cs "分析架构"
 *            |-- 子 Agent (隔离进程，干净历史)
 *                 |-- bash: find . -name "*.cs"
 *                 |-- bash: cat src/Program.cs
 *                 |-- 通过 stdout 返回摘要
 *
 * 进程隔离 = 上下文隔离
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

var client = baseUrl is null 
    ? new AnthropicClient() { ApiKey = apiKey }
    : new AnthropicClient() { ApiKey = apiKey, BaseUrl = baseUrl };

var workDir = Directory.GetCurrentDirectory();

// 唯一的工具：bash - 通往一切的大门
var bashTool = new Tool
{
    Name = "bash",
    Description = """
        执行 shell 命令。常用模式:
        - 读取: cat/head/tail, grep/find/rg/ls, wc -l
        - 写入: echo 'content' > file, sed -i 's/old/new/g' file
        - 子代理: dotnet run v0_bash_agent.cs '任务描述' (生成隔离 agent，返回摘要)
        """,
    InputSchema = new InputSchema
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["command"] = JsonSerializer.SerializeToElement(new { type = "string", description = "要执行的命令" })
        },
        Required = ["command"]
    }
};

var systemPrompt = $"""
    你是一个位于 {workDir} 的 CLI agent。使用 bash 命令解决问题。

    规则:
    - 行动优先，简短解释。
    - 读取文件: cat, grep, find, rg, ls, head, tail
    - 写入文件: echo '...' > file, sed -i, 或 cat << 'EOF' > file
    - 子代理: 对于复杂子任务，生成子代理保持上下文干净:
      dotnet run v0_bash_agent.cs "探索 src/ 并总结架构"

    何时使用子代理:
    - 任务需要读取很多文件（隔离探索过程）
    - 任务独立且自包含
    - 你想避免用中间细节污染当前对话

    子代理在隔离环境中运行，只返回最终摘要。
    """;

// 核心 Agent 循环
async Task<string> ChatAsync(string prompt, List<MessageParam>? history = null)
{
    history ??= [];
    history.Add(new MessageParam { Role = Role.User, Content = prompt });

    while (true)
    {
        // 1. 调用模型
        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = modelId,
            Messages = history,
            System = systemPrompt,
            Tools = [bashTool],
            MaxTokens = 8000
        });

        // 2. 添加助手消息到历史 - 转换 ContentBlock 为 ContentBlockParam
        var assistantContent = response.Content.Select<ContentBlock, ContentBlockParam>(c =>
        {
            if (c.TryPickText(out var text))
                return new TextBlockParam { Text = text.Text };
            if (c.TryPickToolUse(out var toolUse))
                return new ToolUseBlockParam { ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input };
            throw new InvalidOperationException("Unknown content block type");
        }).ToList();
        history.Add(new MessageParam { Role = Role.Assistant, Content = assistantContent });

        // 3. 如果没有工具调用，完成
        if (response.StopReason != StopReason.ToolUse)
        {
            return string.Join("", response.Content
                .Where(c => c.TryPickText(out _))
                .Select(c => { c.TryPickText(out var t); return t!.Text; }));
        }

        // 4. 执行每个工具调用
        var toolResults = new List<ContentBlockParam>();
        foreach (var block in response.Content)
        {
            if (!block.TryPickToolUse(out var toolUse)) continue;
            
            var command = toolUse.Input["command"].GetString()!;
            
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"$ {command}");
            Console.ResetColor();

            var output = await RunBashAsync(command);
            Console.WriteLine(string.IsNullOrEmpty(output) ? "(empty)" : output);

            toolResults.Add(new ToolResultBlockParam 
            { 
                ToolUseID = toolUse.ID, 
                Content = output[..Math.Min(output.Length, 50000)] 
            });
        }

        // 5. 添加工具结果并继续循环
        history.Add(new MessageParam { Role = Role.User, Content = toolResults });
    }
}

// Bash 执行函数
async Task<string> RunBashAsync(string command)
{
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
        
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300));
        await process.WaitForExitAsync(cts.Token);

        return (stdout + stderr).Trim();
    }
    catch (OperationCanceledException)
    {
        return "(timeout after 300s)";
    }
    catch (Exception ex)
    {
        return $"Error: {ex.Message}";
    }
}

// 主入口
if (args.Length > 0)
{
    // 子代理模式：执行任务并打印结果
    Console.WriteLine(await ChatAsync(args[0]));
}
else
{
    // 交互式 REPL 模式
    var history = new List<MessageParam>();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"Mini Claude Code v0 (C#) - {workDir}");
    Console.WriteLine("输入 'exit' 退出\n");
    Console.ResetColor();

    while (true)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write(">> ");
        Console.ResetColor();
        
        var query = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(query) || query is "q" or "exit" or "quit")
            break;

        Console.WriteLine(await ChatAsync(query, history));
        Console.WriteLine();
    }
}
