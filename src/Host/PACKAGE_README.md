# Mythology

**终端下的 AI 小说写作助手** —— 基于 ReAct Agent Loop 架构：LLM 自主决策调用工具、推进写作，关键创作判断（重大剧情、主线、角色命运）由作者确认。

- 创作资产（角色 / 章节 / 情节线）是本地 YAML/Markdown 文件，人可编辑、可 Git 管理
- 116 个注册工具按需注入每轮对话（Tool Search），覆盖写作、检索、分析与网络资料
- 语义检索内核：dense（vec0）+ FTS5 中文分词 + RRF 融合，未配置 embedding 时自动降级纯词法
- 本地优先：创作与检索不依赖云端即可工作；云端（MythologyCloud）按需提供分析与增强 Harness，不可达时自动降级

> 完整文档（架构、插件开发指南、桌面客户端）见 [GitHub 仓库](https://github.com/HyKaChen/Mythology)。

## 安装

```bash
dotnet tool install --global Mythology
```

更新 / 卸载：

```bash
dotnet tool update --global Mythology
dotnet tool uninstall --global Mythology
```

## 快速上手

```bash
mythology                          # 启动交互式 TUI
mythology --novel abc123           # 打开指定小说
mythology --debug                  # 调试模式
mythology --mock                   # Mock 模式（无 LLM）
```

首次启动自动创建 `~/.mythology/config.yaml`，编辑填入 API Key：

```yaml
providers:
  default: openai
  default_model: gpt-4o-mini
  api_keys:
    - provider: openai
      api_key: sk-...
    - provider: custom             # 自定义 OpenAI 兼容端点
      name: my-provider
      api_key: sk-xxx
      base_url: https://api.example.com/v1
```

也支持环境变量（`OPENAI_API_KEY` 等）与 `appsettings.json`。

## 内置工具集

| 插件 | 工具数 | 说明 |
|------|-------:|------|
| NovelTools | 67 | 项目 / 章节 / 角色 / 情节线 / 场景 CRUD + 批量操作 + 23 个云端写作分析器 |
| WritingTools | 10 | `diagnose_writing` + 9 个 `enhance_*`（诊断走云端，增强走本地） |
| SemanticRetrievalTool | 8 | 角色回忆、情节召回、一致性检查、时间线检索 |
| WebSearchTools | 6 | 网络搜索、网页阅读、文学批评检索、相似文风推荐 |
| FileSystem | 6 | read / write / edit / todo / ask_user |
| Core | 20 | tool_search、上下文压缩恢复、13 个 Agent 记忆 + 4 个偏好工具 |

**写作增强维度（9）**：immersion、satisfaction、pacing、description、dialogue、foreshadowing、humanizer、prose_quality、chapter_hook。

## 常用命令

| 命令 | 说明 |
|------|------|
| `/help` | 关于 Mythology |
| `/state` | Agent 状态 + 工具统计 + 写作统计 |
| `/history` `/resume` | 对话历史 / 恢复上次会话 |
| `/compact [keepRecent\|smart [N]]` | 压缩上下文（可逆，`/decompress` 恢复原文） |
| `/tree` `/session` | 对话分支树 / 切换会话 |
| `/mode` | 增强模式循环（手动 → 半自动 → 全自动） |
| `/theme` | 切换主题 |
| `/login` `:cloud status` | 云端登录与状态（OAuth / 设备流，可选） |

## 插件开发

第三方插件只需引用 NuGet 包 [`Mythology.Core`](https://www.nuget.org/packages/Mythology.Core)：

```bash
dotnet add package Mythology.Core
```

- 实现 `IToolPlugin` 契约，返回你的 `AgentTool` 集合
- 三级信任体系（Builtin / Verified / External）+ `[PluginPermission]` 声明式权限
- `mythology build --install` 编译并安装到 `~/.mythology/plugins`

完整插件开发指南见 [docs/PLUGIN.md](https://github.com/HyKaChen/Mythology/blob/main/docs/PLUGIN.md)。

## 相关包

| 包 | 说明 |
|----|------|
| [Mythology.Core](https://www.nuget.org/packages/Mythology.Core) | 插件开发 SDK：IToolPlugin / AgentTool 契约、数据层、检索内核 |
| [StabilityHarness](https://github.com/HyKaChen/Mythology/tree/main/src/StabilityHarness) | 稳定性监控库（Lyapunov + VSA），随工具自动注入 |

## 桌面客户端

除终端 TUI 外，还提供 Avalonia 桌面客户端（Windows / macOS / Linux 安装包），见 [Releases](https://github.com/HyKaChen/Mythology/releases)。

## 许可

MIT
