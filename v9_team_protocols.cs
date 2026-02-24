#!/usr/bin/dotnet run

#:sdk Microsoft.NET.Sdk

#:package Anthropic@12.2.0
#:package dotenv.net@4.0.0

#:property LangVersion=latest
#:property ImplicitUsings=enable
#:property PublishAot=false

/*
 * v9_team_protocols.cs - Mini Claude Code: Team Protocols (~550 lines)
 *
 * 对应上游: s10_team_protocols.py
 *
 * 核心哲学: "同一个 request_id 握手模式驱动关闭与计划审批两种协议"
 * =====================================================================
 * v8 的队友可以沟通，但缺少结构化协议。
 * v9 添加了两种 request_id 关联模式:
 *
 * 关闭协议 FSM:
 *     Lead                Teammate
 *     shutdown_request --> receives request
 *        (req_id=abc)     decides: approve?
 *     shutdown_response <-- {req_id=abc, approve=true}
 *     status -> "shutdown", thread stops
 *
 * 计划审批 FSM:
 *     Teammate            Lead
 *     plan_approval   --> reviews plan text
 *        (req_id=xyz)     approve/reject?
 *     plan_approval_response <-- {req_id=xyz, approve=true}
 *
 * 追踪器: {request_id: {target|from: name, status: pending|approved|rejected}}
 */

using System.Collections.Concurrent;
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
var teamDir = Path.Combine(workDir, ".team");
var inboxDir = Path.Combine(teamDir, "inbox");

var validMsgTypes = new HashSet<string>
    { "message", "broadcast", "shutdown_request", "shutdown_response", "plan_approval_response" };

// =============================================================================
// Request trackers - correlate by request_id
// =============================================================================

var shutdownRequests = new ConcurrentDictionary<string, Dictionary<string, string>>();
var planRequests = new ConcurrentDictionary<string, Dictionary<string, string>>();

// =============================================================================
// MessageBus
// =============================================================================

var bus = new MessageBus(inboxDir, validMsgTypes);

// =============================================================================
// 系统提示
// =============================================================================

var systemPrompt = $"""
    你是一个位于 {workDir} 的团队领导。
    管理队友，使用关闭和计划审批协议。
    - 用 shutdown_request 请求队友优雅关闭。
    - 用 plan_approval 审批/拒绝队友提交的计划。
    规则: 行动优先，不要只是解释。
    """;

// =============================================================================
// 基础工具实现 (lead 和 teammate 共用)
// =============================================================================

string SafePath(string p)
{
    var full = Path.GetFullPath(Path.Combine(workDir, p));
    if (!full.StartsWith(workDir)) throw new InvalidOperationException($"Path escapes workspace: {p}");
    return full;
}

async Task<string> RunBashAsync(string command)
{
    string[] dangerous = ["rm -rf /", "sudo", "shutdown", "reboot"];
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

// 基础工具执行器，供 lead 和 teammate 共用
async Task<string> ExecuteBaseToolAsync(string toolName, IReadOnlyDictionary<string, JsonElement> args)
{
    return toolName switch
    {
        "bash" => await RunBashAsync(args["command"].GetString()!),
        "read_file" => RunRead(args["path"].GetString()!,
            args.TryGetValue("limit", out var l) ? l.GetInt32() : null),
        "write_file" => RunWrite(args["path"].GetString()!, args["content"].GetString()!),
        "edit_file" => RunEdit(args["path"].GetString()!,
            args["old_text"].GetString()!, args["new_text"].GetString()!),
        _ => $"Unknown tool: {toolName}"
    };
}

// =============================================================================
// Lead 协议处理器
// =============================================================================

string HandleShutdownRequest(string teammate)
{
    var reqId = Guid.NewGuid().ToString()[..8];
    shutdownRequests[reqId] = new Dictionary<string, string>
        { ["target"] = teammate, ["status"] = "pending" };
    bus.Send("lead", teammate, "Please shut down gracefully.",
        "shutdown_request", new Dictionary<string, object> { ["request_id"] = reqId });
    return $"Shutdown request {reqId} sent to '{teammate}' (status: pending)";
}

string HandlePlanReview(string requestId, bool approve, string feedback = "")
{
    if (!planRequests.TryGetValue(requestId, out var req))
        return $"Error: Unknown plan request_id '{requestId}'";
    req["status"] = approve ? "approved" : "rejected";
    bus.Send("lead", req["from"], feedback, "plan_approval_response",
        new Dictionary<string, object> { ["request_id"] = requestId, ["approve"] = approve, ["feedback"] = feedback });
    return $"Plan {req["status"]} for '{req["from"]}'";
}

// =============================================================================
// Lead 工具定义 (12 个工具)
// =============================================================================

var tools = new List<Tool>
{
    // 以下 4 个基础工具同时供 lead 和 teammate 使用
    new() { Name = "bash", Description = "运行 shell 命令。",
        InputSchema = new InputSchema {
            Properties = new Dictionary<string, JsonElement> { ["command"] = JsonSerializer.SerializeToElement(new { type = "string" }) },
            Required = ["command"] } },
    new() { Name = "read_file", Description = "读取文件。",
        InputSchema = new InputSchema {
            Properties = new Dictionary<string, JsonElement> {
                ["path"] = JsonSerializer.SerializeToElement(new { type = "string" }),
                ["limit"] = JsonSerializer.SerializeToElement(new { type = "integer" }) },
            Required = ["path"] } },
    new() { Name = "write_file", Description = "写入文件。",
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
    new() { Name = "spawn_teammate", Description = "生成持久化队友。",
        InputSchema = new InputSchema {
            Properties = new Dictionary<string, JsonElement> {
                ["name"] = JsonSerializer.SerializeToElement(new { type = "string" }),
                ["role"] = JsonSerializer.SerializeToElement(new { type = "string" }),
                ["prompt"] = JsonSerializer.SerializeToElement(new { type = "string" }) },
            Required = ["name", "role", "prompt"] } },
    new() { Name = "list_teammates", Description = "列出所有队友。",
        InputSchema = new InputSchema { Properties = new Dictionary<string, JsonElement>(), Required = [] } },
    new() { Name = "send_message", Description = "向队友发送消息。",
        InputSchema = new InputSchema {
            Properties = new Dictionary<string, JsonElement> {
                ["to"] = JsonSerializer.SerializeToElement(new { type = "string" }),
                ["content"] = JsonSerializer.SerializeToElement(new { type = "string" }),
                ["msg_type"] = JsonSerializer.SerializeToElement(new { type = "string" }) },
            Required = ["to", "content"] } },
    new() { Name = "read_inbox", Description = "读取 lead 收件箱。",
        InputSchema = new InputSchema { Properties = new Dictionary<string, JsonElement>(), Required = [] } },
    new() { Name = "broadcast", Description = "广播消息给所有队友。",
        InputSchema = new InputSchema {
            Properties = new Dictionary<string, JsonElement> { ["content"] = JsonSerializer.SerializeToElement(new { type = "string" }) },
            Required = ["content"] } },
    // v9 新增: 协议工具
    new() { Name = "shutdown_request", Description = "请求队友优雅关闭，返回 request_id。",
        InputSchema = new InputSchema {
            Properties = new Dictionary<string, JsonElement> { ["teammate"] = JsonSerializer.SerializeToElement(new { type = "string" }) },
            Required = ["teammate"] } },
    new() { Name = "shutdown_response", Description = "回应关闭请求。approve=true 则关闭，false 继续工作。",
        InputSchema = new InputSchema {
            Properties = new Dictionary<string, JsonElement> {
                ["request_id"] = JsonSerializer.SerializeToElement(new { type = "string" }),
                ["approve"] = JsonSerializer.SerializeToElement(new { type = "boolean" }),
                ["reason"] = JsonSerializer.SerializeToElement(new { type = "string" }) },
            Required = ["request_id", "approve"] } },
    new() { Name = "plan_approval", Description = "队友提交计划 / 领导审批计划。",
        InputSchema = new InputSchema {
            Properties = new Dictionary<string, JsonElement> {
                ["request_id"] = JsonSerializer.SerializeToElement(new { type = "string" }),
                ["approve"] = JsonSerializer.SerializeToElement(new { type = "boolean" }),
                ["feedback"] = JsonSerializer.SerializeToElement(new { type = "string" }),
                ["plan"] = JsonSerializer.SerializeToElement(new { type = "string" }) },
            Required = [] } }
};

// =============================================================================
// TeammateManager with shutdown + plan approval
// =============================================================================

var team = new TeammateManager(teamDir, bus, client, modelId, workDir, validMsgTypes,
    shutdownRequests, planRequests, ExecuteBaseToolAsync, tools);

// =============================================================================
// 工具分发
// =============================================================================

async Task<string> ExecuteToolAsync(string name, IReadOnlyDictionary<string, JsonElement> args)
{
    return name switch
    {
        "bash" or "read_file" or "write_file" or "edit_file"
            => await ExecuteBaseToolAsync(name, args),
        "spawn_teammate" => team.Spawn(args["name"].GetString()!, args["role"].GetString()!, args["prompt"].GetString()!),
        "list_teammates" => team.ListAll(),
        "send_message" => bus.Send("lead", args["to"].GetString()!, args["content"].GetString()!,
            args.TryGetValue("msg_type", out var mt) ? mt.GetString() ?? "message" : "message"),
        "read_inbox" => JsonSerializer.Serialize(bus.ReadInbox("lead"), Helpers.JsonPretty),
        "broadcast" => bus.Broadcast("lead", args["content"].GetString()!, team.MemberNames()),
        "shutdown_request" => HandleShutdownRequest(args["teammate"].GetString()!),
        "plan_approval" => HandlePlanReview(args["request_id"].GetString()!, args["approve"].GetBoolean(),
            args.TryGetValue("feedback", out var fb) ? fb.GetString() ?? "" : ""),
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
        var inbox = bus.ReadInbox("lead");
        if (inbox.Count > 0)
        {
            messages.Add(new MessageParam { Role = Role.User,
                Content = $"<inbox>{JsonSerializer.Serialize(inbox, Helpers.JsonPretty)}</inbox>" });
            messages.Add(new MessageParam { Role = Role.Assistant, Content = "Noted inbox messages." });
        }

        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = modelId, Messages = [.. messages],
            System = systemPrompt, Tools = [.. tools], MaxTokens = 8000
        });

        foreach (var block in response.Content)
            if (block.TryPickText(out var text)) Console.WriteLine(text.Text);

        messages.Add(new MessageParam { Role = Role.Assistant, Content = Helpers.ToParams(response.Content) });

        if (response.StopReason != StopReason.ToolUse)
            return;

        var results = new List<ToolResultBlockParam>();
        foreach (var block in response.Content)
        {
            if (!block.TryPickToolUse(out var tu)) continue;
            var output = await ExecuteToolAsync(tu.Name, tu.Input);
            Console.WriteLine($"\n> {tu.Name}: {(output.Length > 200 ? output[..200] + "..." : output)}");
            results.Add(new ToolResultBlockParam { ToolUseID = tu.ID, Content = output });
        }
        messages.Add(new MessageParam { Role = Role.User, Content = results.Select(r => (ContentBlockParam)r).ToList() });
    }
}

// =============================================================================
// 主 REPL
// =============================================================================

Console.WriteLine($"Mini Claude Code v9 (Team Protocols) - {workDir}");
Console.WriteLine("命令: /team (列出队友), /inbox (查看收件箱)");
Console.WriteLine("输入 'exit' 退出。\n");

var history = new List<MessageParam>();

while (true)
{
    Console.Write("You: ");
    var userInput = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(userInput) || userInput.ToLower() is "exit" or "quit" or "q") break;

    if (userInput == "/team") { Console.WriteLine(team.ListAll()); continue; }
    if (userInput == "/inbox")
    {
        Console.WriteLine(JsonSerializer.Serialize(bus.ReadInbox("lead"), Helpers.JsonPretty));
        continue;
    }

    history.Add(new MessageParam { Role = Role.User, Content = userInput });
    try { await AgentLoopAsync(history); }
    catch (Exception ex) { Console.WriteLine($"Error: {ex.Message}"); }
    Console.WriteLine();
}

// =============================================================================
// 类型定义
// =============================================================================

class MessageBus
{
    private readonly string _dir;
    private readonly HashSet<string> _validTypes;
    private readonly object _lock = new();

    public MessageBus(string dir, HashSet<string> validTypes) { _dir = dir; _validTypes = validTypes; Directory.CreateDirectory(dir); }

    public string Send(string sender, string to, string content,
        string msgType = "message", Dictionary<string, object>? extra = null)
    {
        if (!_validTypes.Contains(msgType)) return $"Error: Invalid type '{msgType}'";
        var msg = new Dictionary<string, object>
        {
            ["type"] = msgType, ["from"] = sender, ["content"] = content,
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0
        };
        if (extra != null) foreach (var kv in extra) msg[kv.Key] = kv.Value;
        lock (_lock) { File.AppendAllText(Path.Combine(_dir, $"{to}.jsonl"), JsonSerializer.Serialize(msg) + "\n"); }
        return $"Sent {msgType} to {to}";
    }

    public List<Dictionary<string, object>> ReadInbox(string name)
    {
        lock (_lock)
        {
            var path = Path.Combine(_dir, $"{name}.jsonl");
            if (!File.Exists(path)) return [];
            var msgs = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => JsonSerializer.Deserialize<Dictionary<string, object>>(l)!).ToList();
            File.WriteAllText(path, "");
            return msgs;
        }
    }

    public string Broadcast(string sender, string content, List<string> teammates)
    {
        var c = 0; foreach (var n in teammates) if (n != sender) { Send(sender, n, content, "broadcast"); c++; }
        return $"Broadcast to {c} teammates";
    }
}

class TeammateManager
{
    private readonly string _dir, _configPath, _modelId, _workDir;
    private readonly MessageBus _bus;
    private readonly AnthropicClient _client;
    private readonly HashSet<string> _validMsgTypes;
    private readonly ConcurrentDictionary<string, Dictionary<string, string>> _shutdownReqs, _planReqs;
    private readonly Func<string, IReadOnlyDictionary<string, JsonElement>, Task<string>> _execBaseTool;
    private readonly List<Tool> _allTools;
    private Dictionary<string, object> _config;

    // teammate 可用的工具名称子集 (从 lead tools 中筛选)
    private static readonly HashSet<string> TeammateToolNames =
        ["bash", "read_file", "write_file", "edit_file", "send_message", "read_inbox",
         "shutdown_response", "plan_approval"];

    public TeammateManager(string dir, MessageBus bus, AnthropicClient client, string modelId,
        string workDir, HashSet<string> validMsgTypes,
        ConcurrentDictionary<string, Dictionary<string, string>> shutdownReqs,
        ConcurrentDictionary<string, Dictionary<string, string>> planReqs,
        Func<string, IReadOnlyDictionary<string, JsonElement>, Task<string>> execBaseTool,
        List<Tool> tools)
    {
        _dir = dir; _bus = bus; _client = client; _modelId = modelId;
        _workDir = workDir; _validMsgTypes = validMsgTypes;
        _shutdownReqs = shutdownReqs; _planReqs = planReqs;
        _execBaseTool = execBaseTool; _allTools = tools;
        Directory.CreateDirectory(dir);
        _configPath = Path.Combine(dir, "config.json");
        _config = LoadConfig();
    }

    private Dictionary<string, object> LoadConfig()
    {
        if (File.Exists(_configPath))
            return JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(_configPath))!;
        return new Dictionary<string, object> { ["team_name"] = "default", ["members"] = JsonSerializer.SerializeToElement(new List<object>()) };
    }

    private void SaveConfig() => File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, Helpers.JsonPretty));

    private List<Dictionary<string, object>> GetMembers()
        => _config["members"] is JsonElement je ? je.Deserialize<List<Dictionary<string, object>>>() ?? [] : [];

    private void SetMembers(List<Dictionary<string, object>> m) => _config["members"] = JsonSerializer.SerializeToElement(m);

    public string Spawn(string name, string role, string prompt)
    {
        var members = GetMembers();
        var existing = members.FirstOrDefault(m => m["name"]?.ToString() == name);
        if (existing != null)
        {
            if (existing["status"]?.ToString() is not ("idle" or "shutdown")) return $"Error: '{name}' is currently {existing["status"]}";
            existing["status"] = "working"; existing["role"] = role;
        }
        else members.Add(new Dictionary<string, object> { ["name"] = name, ["role"] = role, ["status"] = "working" });
        SetMembers(members); SaveConfig();

        _ = Task.Run(() => TeammateLoopAsync(name, role, prompt));
        return $"Spawned '{name}' (role: {role})";
    }

    private async Task TeammateLoopAsync(string name, string role, string prompt)
    {
        var sysPrompt = $"You are '{name}', role: {role}, at {_workDir}. " +
                        "Submit plans via plan_approval before major work. Respond to shutdown_request with shutdown_response.";
        var messages = new List<MessageParam> { new() { Role = Role.User, Content = prompt } };
        var tmTools = TeammateTools();
        var shouldExit = false;

        for (var i = 0; i < 50 && !shouldExit; i++)
        {
            var inbox = _bus.ReadInbox(name);
            foreach (var msg in inbox)
                messages.Add(new MessageParam { Role = Role.User, Content = JsonSerializer.Serialize(msg) });

            Message response;
            try
            {
                response = await _client.Messages.Create(new MessageCreateParams
                { Model = _modelId, System = sysPrompt, Messages = [.. messages], Tools = [.. tmTools], MaxTokens = 8000 });
            }
            catch { break; }

            messages.Add(new MessageParam { Role = Role.Assistant, Content = Helpers.ToParams(response.Content) });

            if (response.StopReason != StopReason.ToolUse) break;

            var results = new List<ToolResultBlockParam>();
            foreach (var block in response.Content)
            {
                if (!block.TryPickToolUse(out var tu)) continue;
                var output = await ExecTeammateToolAsync(name, tu.Name, tu.Input);
                Console.WriteLine($"  [{name}] {tu.Name}: {(output.Length > 120 ? output[..120] + "..." : output)}");
                if (tu.Name == "shutdown_response" && tu.Input.TryGetValue("approve", out var ap) && ap.GetBoolean())
                    shouldExit = true;
                results.Add(new ToolResultBlockParam { ToolUseID = tu.ID, Content = output });
            }
            messages.Add(new MessageParam { Role = Role.User, Content = results.Select(r => (ContentBlockParam)r).ToList() });
        }

        var ms = GetMembers();
        var member = ms.FirstOrDefault(m => m["name"]?.ToString() == name);
        if (member != null)
        {
            member["status"] = shouldExit ? "shutdown" : "idle";
            SetMembers(ms); SaveConfig();
        }
    }

    private async Task<string> ExecTeammateToolAsync(string sender, string toolName, IReadOnlyDictionary<string, JsonElement> args)
    {
        return toolName switch
        {
            // bash/read_file/write_file/edit_file 复用共享的基础工具
            "bash" or "read_file" or "write_file" or "edit_file"
                => await _execBaseTool(toolName, args),
            "send_message" => _bus.Send(sender, args["to"].GetString()!, args["content"].GetString()!,
                args.TryGetValue("msg_type", out var mt) ? mt.GetString() ?? "message" : "message"),
            "read_inbox" => JsonSerializer.Serialize(_bus.ReadInbox(sender), Helpers.JsonPretty),
            "shutdown_response" => HandleShutdownResponse(sender, args),
            "plan_approval" => HandlePlanSubmit(sender, args),
            _ => $"Unknown tool: {toolName}"
        };
    }

    private string HandleShutdownResponse(string sender, IReadOnlyDictionary<string, JsonElement> args)
    {
        var reqId = args["request_id"].GetString()!;
        var approve = args["approve"].GetBoolean();
        if (_shutdownReqs.TryGetValue(reqId, out var r))
            r["status"] = approve ? "approved" : "rejected";
        _bus.Send(sender, "lead", args.TryGetValue("reason", out var rs) ? rs.GetString() ?? "" : "",
            "shutdown_response", new Dictionary<string, object> { ["request_id"] = reqId, ["approve"] = approve });
        return approve ? "Shutdown approved" : "Shutdown rejected";
    }

    private string HandlePlanSubmit(string sender, IReadOnlyDictionary<string, JsonElement> args)
    {
        var planText = args.TryGetValue("plan", out var p) ? p.GetString() ?? "" : "";
        var reqId = Guid.NewGuid().ToString()[..8];
        _planReqs[reqId] = new Dictionary<string, string> { ["from"] = sender, ["plan"] = planText, ["status"] = "pending" };
        _bus.Send(sender, "lead", planText, "plan_approval_response",
            new Dictionary<string, object> { ["request_id"] = reqId, ["plan"] = planText });
        return $"Plan submitted (request_id={reqId}). Waiting for lead approval.";
    }

    private List<Tool> TeammateTools()
        => _allTools.Where(t => TeammateToolNames.Contains(t.Name)).ToList();

    public string ListAll()
    {
        var members = GetMembers();
        if (members.Count == 0) return "No teammates.";
        var lines = new List<string> { $"Team: {_config.GetValueOrDefault("team_name", "default")}" };
        foreach (var m in members) lines.Add($"  {m["name"]} ({m["role"]}): {m["status"]}");
        return string.Join("\n", lines);
    }

    public List<string> MemberNames() => GetMembers().Select(m => m["name"]?.ToString() ?? "").ToList();
}

static class Helpers
{
    public static readonly JsonSerializerOptions JsonPretty = new() { WriteIndented = true };

    public static List<ContentBlockParam> ToParams(IReadOnlyList<ContentBlock> content) =>
        content.Select<ContentBlock, ContentBlockParam>(c =>
        {
            if (c.TryPickText(out var t)) return new TextBlockParam { Text = t.Text };
            if (c.TryPickToolUse(out var tu)) return new ToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input };
            throw new InvalidOperationException("Unknown content block type");
        }).ToList();
}
