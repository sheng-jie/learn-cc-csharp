#!/usr/bin/dotnet run

#:sdk Microsoft.NET.Sdk

#:package Anthropic@12.2.0
#:package dotenv.net@4.0.0

#:property LangVersion=latest
#:property ImplicitUsings=enable
#:property PublishAot=false

/*
 * v4_skills_agent.cs - Mini Claude Code: Skills Mechanism (~500 lines)
 *
 * 核心哲学: "知识外部化"
 * =====================
 * v3 给了我们子代理来分解任务。但有个更深的问题：
 *
 *     模型如何知道怎样处理特定领域的任务？
 *
 * - 处理 PDF？它需要知道 pdftotext vs PyMuPDF
 * - 构建 MCP 服务器？它需要协议规范和最佳实践
 * - 代码审查？它需要系统化的检查清单
 *
 * 这些知识不是工具 - 是专业技能。Skills 解决这个问题，
 * 让模型按需加载领域知识。
 *
 * 范式转变: 知识外部化
 * ------------------
 * 传统 AI: 知识锁在模型参数里
 *   - 教新技能: 收集数据 -> 训练 -> 部署
 *   - 成本: $10K-$1M+，时间线: 数周
 *   - 需要 ML 专业知识、GPU 集群
 *
 * Skills: 知识存储在可编辑文件中
 *   - 教新技能: 写一个 SKILL.md 文件
 *   - 成本: 免费，时间线: 几分钟
 *   - 任何人都能做
 *
 * 就像附加一个热插拔的 LoRA 适配器，不需要任何训练！
 *
 * 工具 vs 技能:
 * ------------
 *     | 概念     | 是什么              | 示例                      |
 *     |---------|--------------------|--------------------------| 
 *     | 工具    | 模型能做什么         | bash, read_file, write   |
 *     | 技能    | 模型知道怎么做       | PDF 处理, MCP 开发        |
 *
 * 工具是能力。技能是知识。
 *
 * 渐进式披露:
 * ----------
 *     层 1: 元数据（始终加载）        ~100 tokens/skill
 *           只有 name + description
 *
 *     层 2: SKILL.md 正文（触发时）   ~2000 tokens
 *           详细指令
 *
 *     层 3: 资源（按需）             无限
 *           scripts/, references/, assets/
 *
 * 缓存保留注入:
 * -----------
 * 关键洞察: Skill 内容放入 tool_result（用户消息），
 * 不是系统提示。这保留了 prompt cache！
 *
 *     错误: 每次编辑系统提示（缓存失效，20-50x 成本）
 *     正确: 作为工具结果追加（前缀不变，缓存命中）
 */

using System.Diagnostics;
using System.Text;
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
var skillsDir = Path.Combine(workDir, "skills");

// =============================================================================
// SkillLoader instance
// =============================================================================

var skills = new SkillLoader(skillsDir);

// =============================================================================
// Agent 类型注册表（来自 v3）
// =============================================================================

var agentTypes = new Dictionary<string, AgentConfig>
{
    ["explore"] = new("只读代理，用于探索代码", ["bash", "read_file"],
        "你是一个探索代理。搜索和分析，但不要修改文件。返回简洁的摘要。"),
    ["code"] = new("完整代理，用于实现功能", ["*"],
        "你是一个编程代理。高效地实现请求的更改。"),
    ["plan"] = new("规划代理，用于设计策略", ["bash", "read_file"],
        "你是一个规划代理。分析代码库并输出编号的实现计划。不要做更改。")
};

string GetAgentDescriptions() => string.Join("\n",
    agentTypes.Select(kv => $"- {kv.Key}: {kv.Value.Description}"));

// =============================================================================
// TodoManager instance
// =============================================================================

var todo = new TodoManager();

// =============================================================================
// 系统提示 - 为 v4 更新
// =============================================================================

var systemPrompt = $"""
    你是一个位于 {workDir} 的编程代理。

    循环: 规划 -> 使用工具行动 -> 报告。

    **可用技能**（任务匹配时用 Skill 工具调用）:
    {skills.GetDescriptions()}

    **可用子代理**（对专注子任务用 Task 工具调用）:
    {GetAgentDescriptions()}

    规则:
    - 当任务匹配技能描述时立即使用 Skill 工具
    - 对需要专注探索或实现的子任务使用 Task 工具
    - 使用 TodoWrite 追踪多步骤工作
    - 行动优先，不要只是解释。
    - 完成后，总结做了什么改动。
    """;

// =============================================================================
// 工具定义
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

// Task 工具（来自 v3）
var taskTool = new Tool
{
    Name = "Task",
    Description = $"为专注子任务生成子代理。\n\nAgent 类型:\n{GetAgentDescriptions()}",
    InputSchema = new InputSchema
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["description"] = JsonSerializer.SerializeToElement(new { type = "string", description = "简短任务描述（3-5 词）" }),
            ["prompt"] = JsonSerializer.SerializeToElement(new { type = "string", description = "给子代理的详细指令" }),
            ["agent_type"] = JsonSerializer.SerializeToElement(new { type = "string", @enum = new[] { "explore", "code", "plan" } })
        },
        Required = ["description", "prompt", "agent_type"]
    }
};

// v4 新增: Skill 工具
var skillTool = new Tool
{
    Name = "Skill",
    Description = $"""
        加载技能以获取任务的专业知识。

        可用技能:
        {skills.GetDescriptions()}

        何时使用:
        - 用户任务匹配技能描述时立即使用
        - 在尝试特定领域工作（PDF、MCP 等）之前

        技能内容将注入对话，给你详细指令和资源访问。
        """,
    InputSchema = new InputSchema
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["skill"] = JsonSerializer.SerializeToElement(new { type = "string", description = "要加载的技能名称" })
        },
        Required = ["skill"]
    }
};

var allTools = baseTools.Concat([taskTool, skillTool]).ToList();

List<Tool> GetToolsForAgent(string agentType)
{
    var allowed = agentTypes.GetValueOrDefault(agentType)?.Tools ?? ["*"];
    if (allowed.Contains("*")) return baseTools;
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

string RunSkill(string skillName)
{
    /*
     * 加载技能并注入对话。
     *
     * 这是关键机制:
     * 1. 获取技能内容（SKILL.md 正文 + 资源提示）
     * 2. 用 <skill-loaded> 标签包装返回
     * 3. 模型接收这个作为 tool_result（用户消息）
     * 4. 模型现在"知道"如何做任务
     *
     * 为什么用 tool_result 而不是系统提示？
     * - 系统提示更改会使缓存失效（20-50x 成本增加）
     * - 工具结果追加到末尾（前缀不变，缓存命中）
     */
    var content = skills.GetSkillContent(skillName);

    if (content is null)
    {
        var available = string.Join(", ", skills.ListSkills());
        return $"Error: 未知技能 '{skillName}'。可用: {(string.IsNullOrEmpty(available) ? "none" : available)}";
    }

    return $"""
        <skill-loaded name="{skillName}">
        {content}
        </skill-loaded>

        按照上面技能中的指令完成用户的任务。
        """;
}

async Task<string> RunTaskAsync(string description, string prompt, string agentType)
{
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
        "Skill" => RunSkill(args["skill"].GetString()!),
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
            else if (tc.Name == "Skill")
                Console.WriteLine($"\n> Loading skill: {tc.Input["skill"].GetString()}");
            else
                Console.WriteLine($"\n> {tc.Name}");

            var output = await ExecuteToolAsync(tc.Name, tc.Input);

            if (tc.Name == "Skill")
                Console.WriteLine($"  Skill loaded ({output.Length} chars)");
            else if (tc.Name != "Task")
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

Console.WriteLine($"Mini Claude Code v4 (with Skills) - {workDir}");
Console.WriteLine($"Skills: {string.Join(", ", skills.ListSkills())}");
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

record Skill(string Name, string Description, string Body, string Path, string Dir);

class SkillLoader
{
    /*
     * 从 SKILL.md 文件加载和管理技能。
     *
     * 一个技能是一个包含以下内容的文件夹:
     * - SKILL.md（必需）: YAML frontmatter + markdown 指令
     * - scripts/（可选）: 模型可以运行的辅助脚本
     * - references/（可选）: 额外文档
     * - assets/（可选）: 模板、输出文件
     */

    private readonly string _skillsDir;
    private readonly Dictionary<string, Skill> _skills = new();

    public SkillLoader(string skillsDir)
    {
        _skillsDir = skillsDir;
        LoadSkills();
    }

    private Skill? ParseSkillMd(string path)
    {
        var content = File.ReadAllText(path);

        var match = Regex.Match(content, @"^---\s*\n(.*?)\n---\s*\n(.*)$", RegexOptions.Singleline);
        if (!match.Success) return null;

        var frontmatter = match.Groups[1].Value;
        var body = match.Groups[2].Value.Trim();

        var metadata = new Dictionary<string, string>();
        foreach (var line in frontmatter.Split('\n'))
        {
            var colonIndex = line.IndexOf(':');
            if (colonIndex > 0)
            {
                var key = line[..colonIndex].Trim();
                var value = line[(colonIndex + 1)..].Trim().Trim('"', '\'');
                metadata[key] = value;
            }
        }

        if (!metadata.TryGetValue("name", out var name) || 
            !metadata.TryGetValue("description", out var description))
            return null;

        return new Skill(name, description, body, path, System.IO.Path.GetDirectoryName(path)!);
    }

    private void LoadSkills()
    {
        if (!Directory.Exists(_skillsDir)) return;

        foreach (var skillDir in Directory.GetDirectories(_skillsDir))
        {
            var skillMd = Path.Combine(skillDir, "SKILL.md");
            if (!File.Exists(skillMd)) continue;

            var skill = ParseSkillMd(skillMd);
            if (skill != null)
                _skills[skill.Name] = skill;
        }
    }

    public string GetDescriptions()
    {
        if (_skills.Count == 0) return "(no skills available)";
        return string.Join("\n", _skills.Select(kv => $"- {kv.Key}: {kv.Value.Description}"));
    }

    public string? GetSkillContent(string name)
    {
        if (!_skills.TryGetValue(name, out var skill)) return null;

        var content = $"# Skill: {skill.Name}\n\n{skill.Body}";

        var resources = new List<string>();
        foreach (var (folder, label) in new[] { ("scripts", "Scripts"), ("references", "References"), ("assets", "Assets") })
        {
            var folderPath = Path.Combine(skill.Dir, folder);
            if (Directory.Exists(folderPath))
            {
                var files = Directory.GetFiles(folderPath).Select(Path.GetFileName);
                if (files.Any())
                    resources.Add($"{label}: {string.Join(", ", files)}");
            }
        }

        if (resources.Count > 0)
        {
            content += $"\n\n**{skill.Dir} 中的可用资源:**\n";
            content += string.Join("\n", resources.Select(r => $"- {r}"));
        }

        return content;
    }

    public IEnumerable<string> ListSkills() => _skills.Keys;
}

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
