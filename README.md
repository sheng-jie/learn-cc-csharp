# Learn Claude Code (C#) - Bash 就是 Agent 的一切

[![.NET 10.0](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](./LICENSE)

> **声明**: 这是 [shareAI Lab](https://github.com/shareAI-lab) 的独立教育项目 `learn-claude-code` 的 C# (.NET) 实现版本。与 Anthropic 无关，未获其认可或赞助。"Claude Code" 是 Anthropic 的商标。

**从零开始构建你自己的 AI Agent (.NET 版本)。**

[Python 原版](https://github.com/shareAI-lab/learn-claude-code)

---

## 为什么有这个仓库？

这个仓库是 [learn-claude-code](https://github.com/shareAI-lab/learn-claude-code) 的 .NET 移植版。原仓库以 Python 语言深入浅出地剖析了 Claude Code 的核心原理。为了方便 .NET 开发者学习掌握 Agent 的构建精髓，我们将其核心代码使用 C# 的最新特性进行了完整重写。

我们认为 **Claude Code 是世界上最优秀的 AI 编程代理**，通过学习它的设计模式，你将对 **"什么才是真正的 AI Agent"** 有全新的认知。

---

> 兼容 **[Kode CLI](https://github.com/shareAI-lab/Kode)**、**Claude Code**、**Cursor**，以及任何支持 [Agent Skills Spec](https://github.com/anthropics/agent-skills) 的 Agent。

## 你将学到什么

完成本教程后，你将理解：

- **Agent 循环** - 所有 AI 编程代理背后那个令人惊讶的简单模式
- **工具设计** - 如何让 C# 代码与 AI 模型及真实世界交互
- **显式规划** - 使用约束让 AI 行为可预测
- **上下文管理** - 通过子代理（进程隔离）保持代理记忆干净
- **知识注入** - 按需加载领域专业知识，无需重新训练

## 学习路径

```
从这里开始
    |
    v
[v0: Bash Agent] -----> "一个工具就够了"
    |                    v0_bash_agent.cs
    v
[v1: Basic Agent] ----> "完整的 Agent 模式"
    |                    4 个工具: Bash, Read, Write, Ls
    v
[v2: Todo Agent] -----> "让计划显式化"
    |                    +TodoManager
    v
[v3: Subagent] -------> "分而治之"
    |                    +Task 工具 (进程隔离)
    v
[v4: Skills Agent] ---> "按需领域专业"
                         +Skill 工具
    v
[v5-v11: 新增课时] --> "长会话与多智能体协作"
                         v5 上下文压缩
                         v6 任务系统
                         v7 后台任务
                         v8 智能体团队
                         v9 团队协议
                         v10 自治智能体
                         v11 Worktree + 任务隔离
```

**推荐学习方式：**
1. 先阅读并运行 `v0` - 理解核心循环
2. 对比 `v0` 和 `v1` - 看工具如何演进
3. 学习 `v2` 的规划模式
4. 探索 `v3` 的复杂任务分解
5. 掌握 `v4` 构建可扩展的 Agent
6. 继续学习 `v5-v11` 的长会话、持久化与多智能体机制

## 快速开始

### 前置要求

- [.NET SDK](https://dotnet.microsoft.com/download) (推荐 .NET 10.0 )
- 一个 Anthropic API Key (或兼容的 API 代理)

### 运行步骤

1. **克隆仓库**
   ```bash
   git clone https://github.com/your-repo/learn-cc-csharp
   cd learn-cc-csharp/dotnet
   ```

2. **配置 API Key**
   复制 `.env.example` 为 `.env` 并填入你的 Key。国内用户可使用智谱 GLM-4.7 等兼容模型。
   
   Powershell:
   ```powershell
   cp .env.example .env
   # 编辑 .env 文件填入 ANTHROPIC_API_KEY=sk-ant-xxx...
   ```

3. **运行**
   使用 `dotnet run` 运行对应的 Agent。

   ```bash
   # 极简版（从这里开始！）
   dotnet run v0_bash_agent.cs

   # 核心 Agent 循环
   dotnet run v1_basic_agent.cs

   # + Todo 规划
   dotnet run v2_todo_agent.cs

   # + 子代理
   dotnet run v3_subagent.cs

   # + Skills
   dotnet run v4_skills_agent.cs

   # 新增 7 个课时（同步自上游 s06-s12）
   dotnet run v5_context_compact.cs
   dotnet run v6_task_system_agent.cs
   dotnet run v7_background_tasks_agent.cs
   dotnet run v8_agent_teams.cs
   dotnet run v9_team_protocols.cs
   dotnet run v10_autonomous_agents.cs
   dotnet run v11_worktree_task_isolation.cs
   ```

## 核心模式

每个 Agent 都只是这个循环（C# 伪代码）：

```csharp
while (true)
{
    var response = await model.ChatAsync(history, tools);
    
    if (response.StopReason != StopReason.ToolUse)
        return response.Text;
        
    var results = await ExecuteTools(response.ToolCalls);
    history.Add(results);
}
```

就这样。模型持续调用工具直到完成。其他一切都是精化。

## 版本对比

| 版本 | 文件 | 工具 | 核心新增 | 关键洞察 |
|------|------|------|---------|---------|
| v0 | [v0_bash_agent.cs](v0_bash_agent.cs) | bash | 递归子代理 | 一个工具就够了 |
| v1 | [v1_basic_agent.cs](v1_basic_agent.cs) | bash, read, write, edit | 核心循环 | 模型即代理 |
| v2 | [v2_todo_agent.cs](v2_todo_agent.cs) | +TodoWrite | 显式规划 | 约束赋能复杂性 |
| v3 | [v3_subagent.cs](v3_subagent.cs) | +Task | 上下文隔离 | 干净上下文 = 更好结果 |
| v4 | [v4_skills_agent.cs](v4_skills_agent.cs) | +Skill | 知识加载 | 专业无需重训 |
| v5 | [v5_context_compact.cs](v5_context_compact.cs) | compact | 上下文压缩 | 策略性遗忘 |
| v6 | [v6_task_system_agent.cs](v6_task_system_agent.cs) | tasks | 任务持久化 | 状态跨压缩存活 |
| v7 | [v7_background_tasks_agent.cs](v7_background_tasks_agent.cs) | background | 后台执行 | 发射后不管 |
| v8 | [v8_agent_teams.cs](v8_agent_teams.cs) | mailbox | 团队协作 | 追加即发送，排空即读取 |
| v9 | [v9_team_protocols.cs](v9_team_protocols.cs) | protocol | 协议化协作 | 同 request_id 双协议 |
| v10 | [v10_autonomous_agents.cs](v10_autonomous_agents.cs) | autonomous | 自组织执行 | 轮询、认领、工作、重复 |
| v11 | [v11_worktree_task_isolation.cs](v11_worktree_task_isolation.cs) | worktree | 目录隔离 | 任务 ID 协调并行 |

## 深入阅读 (文章)

该系列配套了详细的公众号风格文章，深入剖析设计理念：

- [v0: Bash 就是一切](articles/v0文章.md)
- [v1: 模型即代理 - 价值 3000 万美金的 400 行代码](articles/v1文章.md)
- [v2: 用 Todo 实现自我约束](articles/v2文章.md)
- [v3: 子代理机制 - 上下文隔离的艺术](articles/v3文章.md)
- [v4: Skills 机制 - 知识外部化](articles/v4文章.md)
- [v5: 上下文压缩（Compact）](articles/v5文章.md)
- [v6: 任务系统（Task System）](articles/v6文章.md)
- [v7: 后台任务（Background Tasks）](articles/v7文章.md)
- [v8: 智能体团队（Agent Teams）](articles/v8文章.md)
- [v9: 团队协议（Team Protocols）](articles/v9文章.md)
- [v10: 自治智能体（Autonomous Agents）](articles/v10文章.md)
- [v11: Worktree + 任务隔离](articles/v11文章.md)
- [上下文缓存经济学](articles/上下文缓存经济学.md)

## 设计哲学

> **模型是 80%，代码是 20%。**

Kode 和 Claude Code 等现代 Agent 能工作，不是因为巧妙的工程，而是因为模型被训练成了 Agent。我们的工作就是给它工具，然后闪开。

## License

MIT

---

**模型即代理。这就是全部秘密。**
