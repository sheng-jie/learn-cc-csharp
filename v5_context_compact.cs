#!/usr/bin/dotnet run

#:sdk Microsoft.NET.Sdk

#:package Anthropic@12.2.0
#:package dotenv.net@4.0.0

#:property LangVersion=latest
#:property ImplicitUsings=enable
#:property PublishAot=false

/*
 * v5_context_compact.cs - Mini Claude Code: Context Compact (~350 lines)
 *
 * 对应上游: s06_context_compact.py
 *
 * 核心哲学: "Agent 可以策略性地遗忘，然后永远工作下去"
 * ================================================
 * v4 给了我们技能加载。但有个更根本的问题：
 *
 *     上下文窗口是有限的。
 *
 * 长时间会话中，工具调用结果不断堆积：
 *   - 读取 20 个文件 → 数万 tokens
 *   - 执行 30 个命令 → 输出溢满上下文
 *   - 最终触及上下文窗口上限 → 对话崩溃
 *
 * 三层压缩管道:
 * -----------
 *     每轮:
 *     +------------------+
 *     | 工具调用结果       |
 *     +------------------+
 *             |
 *             v
 *     [第 1 层: micro_compact]        (静默，每轮执行)
 *       将超过最近 3 个的 tool_result
 *       替换为 "[Previous: used {tool_name}]"
 *             |
 *             v
 *     [检查: tokens > 50000?]
 *        |               |
 *        否              是
 *        |               |
 *        v               v
 *     继续        [第 2 层: auto_compact]
 *                   保存完整记录到 .transcripts/
 *                   让 LLM 总结对话。
 *                   用 [summary] 替换所有消息。
 *                         |
 *                         v
 *                 [第 3 层: compact 工具]
 *                   模型调用 compact -> 立即总结。
 *                   和 auto 相同，但由 Agent 主动触发。
 *
 * 缓存保留:
 * --------
 * 微压缩只替换内容，不删除消息结构。
 * 系统提示前缀不变 → prompt cache 命中。
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

const int THRESHOLD = 50000;
const int KEEP_RECENT = 3;
var transcriptDir = Path.Combine(workDir, ".transcripts");

// =============================================================================
// 系统提示
// =============================================================================

var systemPrompt = $"""
    你是一个位于 {workDir} 的编程代理。

    循环: 使用工具行动 -> 报告。

    你拥有 compact 工具，可以在你觉得上下文过长时手动触发压缩。
    系统也会在 token 超过阈值时自动压缩。

    规则:
    - 行动优先，不要只是解释。
    - 完成后，总结做了什么改动。
    """;

// =============================================================================
// Context Compact 管道
// =============================================================================

/// <summary>粗略估算 token 数: ~4 字符 ≈ 1 token</summary>
int EstimateTokens(List<MessageParam> messages)
    => JsonSerializer.Serialize(messages).Length / 4;

// 并行追踪: 记录哪些消息包含 tool_result 及其 ID 和工具名
var toolResultTracker = new List<(int msgIdx, List<(string toolUseId, string toolName)> tools)>();

/// <summary>
/// 第 1 层: 微压缩 - 用占位符替换旧的工具结果 (保留最近 KEEP_RECENT 个)
/// 由于 ContentBlockParam 是判别联合类型 (不支持 is 模式匹配),
/// 且 ToolResultBlockParam.Content 是 init-only 属性,
/// 我们使用并行追踪列表来定位旧消息, 并以全新的 ToolResultBlockParam 替换。
/// </summary>
void MicroCompact(List<MessageParam> messages)
{
    if (toolResultTracker.Count <= KEEP_RECENT) return;

    var toClear = toolResultTracker.Take(toolResultTracker.Count - KEEP_RECENT).ToList();
    foreach (var (msgIdx, tools) in toClear)
    {
        if (msgIdx >= messages.Count) continue;

        // 用包含占位符内容的新 ToolResultBlockParam 替换整条消息
        var newBlocks = tools.Select(t => (ContentBlockParam)new ToolResultBlockParam
        {
            ToolUseID = t.toolUseId,
            Content = $"[Previous: used {t.toolName}]"
        }).ToList();

        messages[msgIdx] = new MessageParam { Role = Role.User, Content = newBlocks };
    }
}

/// <summary>
/// 第 2 层: 自动压缩 - 保存记录，让 LLM 总结，替换所有消息
/// </summary>
async Task AutoCompactAsync(List<MessageParam> messages)
{
    // 保存完整记录到磁盘
    Directory.CreateDirectory(transcriptDir);
    var transcriptPath = Path.Combine(transcriptDir, $"transcript_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.jsonl");
    await File.WriteAllLinesAsync(transcriptPath,
        messages.Select(m => JsonSerializer.Serialize(m)));
    Console.WriteLine($"[transcript saved: {transcriptPath}]");

    // 让 LLM 总结
    var conversationText = JsonSerializer.Serialize(messages);
    if (conversationText.Length > 80000) conversationText = conversationText[..80000];

    var summaryResponse = await client.Messages.Create(new MessageCreateParams
    {
        Model = modelId,
        Messages =
        [
            new MessageParam
            {
                Role = Role.User,
                Content = "Summarize this conversation for continuity. Include: "
                          + "1) What was accomplished, 2) Current state, 3) Key decisions made. "
                          + "Be concise but preserve critical details.\n\n" + conversationText
            }
        ],
        MaxTokens = 2000
    });

    var summary = string.Join("", summaryResponse.Content
        .Where(c => c.TryPickText(out _))
        .Select(c => { c.TryPickText(out var t); return t!.Text; }));

    // 替换所有消息为压缩摘要
    messages.Clear();
    toolResultTracker.Clear();
    messages.Add(new MessageParam
    {
        Role = Role.User,
        Content = $"[Conversation compressed. Transcript: {transcriptPath}]\n\n{summary}"
    });
    messages.Add(new MessageParam
    {
        Role = Role.Assistant,
        Content = "Understood. I have the context from the summary. Continuing."
    });
}

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
        return string.Join("\n", lines)[..Math.Min(string.Join("\n", lines).Length, 50000)];
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
// 工具定义
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
    new() { Name = "compact", Description = "手动触发对话压缩。在上下文太长时使用。",
        InputSchema = new InputSchema {
            Properties = new Dictionary<string, JsonElement> {
                ["focus"] = JsonSerializer.SerializeToElement(new { type = "string", description = "压缩时需要保留的重点" }) },
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
        "compact" => "Manual compression requested.",
        _ => $"Unknown tool: {name}"
    };
}

// =============================================================================
// 主 Agent 循环 - 集成三层压缩
// =============================================================================

async Task AgentLoopAsync(List<MessageParam> messages)
{
    while (true)
    {
        // 第 1 层: 每轮微压缩
        MicroCompact(messages);

        // 第 2 层: token 超阈值时自动压缩
        if (EstimateTokens(messages) > THRESHOLD)
        {
            Console.WriteLine("[auto_compact triggered]");
            await AutoCompactAsync(messages);
        }

        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = modelId,
            Messages = [.. messages],
            System = systemPrompt,
            Tools = [.. tools],
            MaxTokens = 8000
        });

        // 提取文本输出
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var text))
                Console.WriteLine(text.Text);
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
        var manualCompact = false;

        foreach (var tc in toolCalls)
        {
            if (tc.Name == "compact")
            {
                manualCompact = true;
                Console.WriteLine($"\n> compact: Compressing...");
                results.Add(new ToolResultBlockParam { ToolUseID = tc.ID, Content = "Compressing..." });
            }
            else
            {
                var output = await ExecuteToolAsync(tc.Name, tc.Input);
                var preview = output.Length > 200 ? output[..200] + "..." : output;
                Console.WriteLine($"\n> {tc.Name}: {preview}");
                results.Add(new ToolResultBlockParam { ToolUseID = tc.ID, Content = output });
            }
        }

        var assistantBlocks = response.Content.Select<ContentBlock, ContentBlockParam>(c =>
        {
            if (c.TryPickText(out var t)) return new TextBlockParam { Text = t.Text };
            if (c.TryPickToolUse(out var tu)) return new ToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input };
            throw new InvalidOperationException("Unknown content block type");
        }).ToList();
        messages.Add(new MessageParam { Role = Role.Assistant, Content = assistantBlocks });
        messages.Add(new MessageParam { Role = Role.User, Content = results.Select(r => (ContentBlockParam)r).ToList() });

        // 记录此 tool_result 消息的位置和工具信息, 供 MicroCompact 使用
        var toolInfos = toolCalls.Zip(results, (tc, r) => (toolUseId: r.ToolUseID, toolName: tc.Name)).ToList();
        toolResultTracker.Add((messages.Count - 1, toolInfos));

        // 第 3 层: 手动 compact 触发
        if (manualCompact)
        {
            Console.WriteLine("[manual compact]");
            await AutoCompactAsync(messages);
        }
    }
}

// =============================================================================
// 主 REPL
// =============================================================================

Console.WriteLine($"Mini Claude Code v5 (Context Compact) - {workDir}");
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
