# Mythology 插件开发指南

> 本文档整合了插件开发指南、工具分析报告等插件相关设计。
> 最后更新：2026-07-24

---

## 目录

1. [快速开始](#1-快速开始)
2. [插件架构](#2-插件架构)
3. [信任体系与安全](#3-信任体系与安全)
4. [权限系统](#4-权限系统)
5. [plugin.json 构建时生成](#5-pluginjson-构建时生成)
6. [IToolPlugin 接口](#6-itoolplugin-接口)
7. [数据访问](#7-数据访问)
8. [工具参数 & JSON Schema](#8-工具参数--json-schema)
9. [Batch 工具模式](#9-batch-工具模式)
10. [工具分析器（23 个，全部走云端）](#10-工具分析器23-个全部走云端)
11. [构建与安装](#11-构建与安装)
12. [版本兼容性](#12-版本兼容性)
13. [调试](#13-调试)
14. [外置插件安装流程](#14-外置插件安装流程)
15. [攻击面推演](#15-攻击面推演)
16. [已有插件](#16-已有插件)

---

## 1. 快速开始

### 1.1 创建项目

```
MyPlugin/
├── MyPlugin.csproj
└── MyPluginPlugin.cs
```

> **注意**：`plugin.json` 不再需要手动创建，由 MSBuild Target 在编译后自动生成。

### 1.2 .csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>

    <!-- 插件元数据 — plugin.json 的数据源 -->
    <Version>1.0.0</Version>
    <PluginId>my-plugin</PluginId>
    <PluginName>My Plugin</PluginName>
    <PluginDescription>我的自定义工具</PluginDescription>
    <PluginAuthor>your-name</PluginAuthor>
    <PluginMinCoreVersion>1.0.0</PluginMinCoreVersion>
    <PluginPermissions>novel:read</PluginPermissions>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Mythology.Core" Version="1.0.0" />
  </ItemGroup>
</Project>
```

**变更说明**：
- 新增 `Version`、`PluginId`、`PluginName`、`PluginDescription`、`PluginAuthor`、`PluginMinCoreVersion`、`PluginPermissions` 属性
- `PluginPermissions` 用分号分隔（如 `novel:read;novel:write`），与 MSBuild ItemGroup 的分隔符一致
- 不再需要 `<None Include="plugin.json" CopyToOutputDirectory="PreserveNewest" />`

### 1.3 实现契约

```csharp
using Core;
using Core.Plugins;

// 权限声明 — 编译进 DLL，受强名称签名保护
[assembly: PluginPermission(PluginPermissions.NovelRead)]

namespace MyPlugin;

public sealed class MyPluginPlugin : IToolPlugin
{
    private PluginStore _store = null!;

    public void Initialize(IServiceProvider sp)
    {
        _store = sp.GetRequiredService<PluginStore>();
    }

    public IEnumerable<AgentTool> GetTools()
    {
        yield return new MyTool(_store);
    }
}
```

### 1.4 定义工具

```csharp
using Core;

public sealed class MyTool(PluginStore store) : AgentTool<MyToolParams, MyToolDetails>
{
    public override string Name => "my_tool";
    public override string Label => "我的工具";
    public override string Description => "工具描述";

    public override async Task<AgentToolResult<MyToolDetails>> ExecuteAsync(
        string toolCallId, MyToolParams args, CancellationToken ct,
        IProgress<AgentToolResult<MyToolDetails>>? onUpdate = null)
    {
        var characters = await store.GetCharacters(args.NovelId);
        return new AgentToolResult<MyToolDetails>
        {
            Content = [new TextContent($"找到 {characters.Count} 个角色")],
            Details = new MyToolDetails { Count = characters.Count },
        };
    }
}

public sealed record MyToolParams
{
    [Description("小说项目ID")]
    public string NovelId { get; init; } = "";

    [Description("搜索关键词")]
    public string Input { get; init; } = "";
}

public sealed record MyToolDetails
{
    public int Count { get; init; }
}
```

### 1.5 构建 & 安装

```bash
mythology build --install            # 编译 + 安装到 ~/.mythology/plugins
mythology build --install-local      # 编译 + 安装到 .mythology/plugins
```

---

## 2. 插件架构

### 2.1 文件结构

```
src/Core/Plugins/
  ├── IToolPlugin.cs                  ← 插件契约接口
  ├── PluginManifest.cs               ← plugin.json 的 POCO + 序列化
  ├── PluginTrustLevel.cs             ← Builtin / Verified / External 三级枚举
  ├── PluginLoader.cs                 ← 扫描、加载、注册、隔离
  ├── RestrictedPluginServiceProvider.cs ← 权限驱动 DI 拦截（按声明权限放行）
  ├── PluginPermissions.cs ← 权限常量（novel:read/write, vector:*, config:*, fs:*, net:*, ui:*）
  ├── PluginPermissionAttribute.cs ← 程序集级权限声明（编译进 DLL）
  └── PluginCapabilities.cs ← 权限 → 服务类型映射（FrozenDictionary）

src/Plugins/                           ← 5 个内置插件
  ├── NovelTools/plugin.json + NovelToolsPlugin.cs
  ├── SemanticRetrievalTool/plugin.json + SemanticRetrievalToolPlugin.cs
  ├── FileSystem/plugin.json + FileSystemPlugin.cs
  ├── WebSearchTools/plugin.json + WebSearchToolsPlugin.cs
  └── WritingTools/                      diagnose_writing + 8 个 enhance_* 工具（P1–P5 全部实现）

src/Host/
  ├── HostBootstrapper.cs             ← 插件目录收集 + LoadAll 调用
  ├── PluginCommands.cs               ← CLI: build/install/list/remove/update
  └── Program.cs                      ← 子命令路由
```

### 2.2 插件加载机制

- `PluginLoader` 扫描多个目录层级，按优先级从高到低加载
- 同名插件（按 `IToolPlugin.Name` 去重）高优先级覆盖低优先级
- 每个插件使用独立的 `AssemblyLoadContext`（隔离加载）
- `AssemblyDependencyResolver` 解析依赖
- 可选强名称签名验证

**插件目录优先级**：

| 优先级 | 目录 | 信任级别 | 说明 |
|:---:|------|:---:|------|
| 1 | `NOVEL_PLUGINS_DIR` 环境变量 | External | 测试/调试 |
| 2 | `.mythology/plugins`（当前目录） | External | 项目级 |
| 3 | `~/.mythology/plugins`（全局） | External | 用户安装 |
| 4 | `AppContext.BaseDirectory/plugins` | Trusted | 随 Host 发布 |

**内置插件自动安装**：Host 构建时通过 MSBuild `InstallPluginsToHome` Target 将编译产物复制到 `~/.mythology/plugins`，每个插件在独立子目录（DLL + `plugin.json`）。

---

## 3. 信任体系与安全

### 3.1 三级 Trust 模型

| 级别 | 来源 | 判定方式 | 可访问服务 |
|---|---|---|---|
| **Builtin** | 主程序同目录下的 `plugins/` 子目录 | 目录位置判定 | 全部 IServiceProvider |
| **Verified** | `~/.mythology/plugins/` 或项目级，DLL 签名在白名单中 | 签名验证 | 按 Attribute 授权 |
| **External** | 其他所有，无签名或签名不在白名单 | 默认 | 硬编码最小权限 |

### 3.2 Trust 判定流程

```
加载 DLL
  │
  ├─ 来自 AppContext.BaseDirectory/plugins/ ?
  │   └─ YES → Builtin → 原始 ServiceProvider
  │
  ├─ DLL 有强名称签名 ?
  │   ├─ YES → 公钥 token 在白名单中 ?
  │   │   ├─ YES → Verified → Attribute 授权的 ServiceProvider
  │   │   └─ NO  → External → 硬编码最小权限
  │   └─ NO → External → 硬编码最小权限
  │
  └─ 默认 → External
```

### 3.3 已知安全问题与修复

| 问题 | 严重程度 | 修复方案 |
|------|---------|---------|
| author 字段可伪造 | P0-SEC | 改为 DLL 签名验证，不再依赖 plugin.json 的 author |
| 内置/外置插件共用同一目录 | P0-SEC | Builtin 只从 `AppContext.BaseDirectory/plugins/` 加载 |
| permissions 字段是死代码 | P0-SEC | `[assembly: PluginPermission]` 编译进 DLL，运行时检查 |
| plugin.json 与代码双源维护 | P1-MAINT | csproj 属性驱动，构建时自动生成 plugin.json |
| IToolPlugin 接口冗余 plugin.json 数据 | P2-DESIGN | 精简接口，移除 Name/Version/Description |

---

## 4. 权限系统

### 4.1 权限声明

```csharp
[assembly: PluginPermission(PluginPermissions.NovelRead)]
[assembly: PluginPermission(PluginPermissions.NovelWrite)]
```

**权限常量**：

| 常量 | 值 | 说明 |
|------|-----|------|
| `PluginPermissions.NovelRead` | `novel:read` | 读取创作资产 |
| `PluginPermissions.NovelWrite` | `novel:write` | 写入创作资产 |
| `PluginPermissions.ConfigRead` | `config:read` | 读取配置 |
| `PluginPermissions.NetworkAccess` | `network:access` | 网络访问 |

### 4.2 权限执行

- **Builtin**：全权限，不受限制
- **Verified**：按声明的 `[assembly: PluginPermission]` 授权，运行时检查
- **External**：硬编码最小权限（仅 `IPluginStore` + `ILogger`），不读任何声明

### 4.3 csproj 中的权限声明

```xml
<PluginPermissions>novel:read;novel:write</PluginPermissions>
```

构建时 MSBuild Target 将权限写入 `plugin.json` 和编译为 `[assembly: PluginPermission]`。

---

## 5. plugin.json 构建时生成

### 5.1 MSBuild Target

```xml
<Target Name="GeneratePluginManifest" AfterTargets="Build">
  <PropertyGroup>
    <PluginManifestPath>$(OutputPath)plugin.json</PluginManifestPath>
  </PropertyGroup>
  <ItemGroup>
    <PluginManifestData Include="name" Value="$(PluginName)" />
    <PluginManifestData Include="id" Value="$(PluginId)" />
    <PluginManifestData Include="version" Value="$(Version)" />
    <PluginManifestData Include="description" Value="$(PluginDescription)" />
    <PluginManifestData Include="author" Value="$(PluginAuthor)" />
    <PluginManifestData Include="minCoreVersion" Value="$(PluginMinCoreVersion)" />
    <PluginManifestData Include="permissions" Value="$(PluginPermissions)" />
  </ItemGroup>
  <!-- 生成 plugin.json -->
</Target>
```

### 5.2 生成结果示例

```json
{
  "name": "Web Search Tools",
  "id": "web-search-tools",
  "version": "1.0.0",
  "description": "网络搜索工具",
  "author": "mythology",
  "minCoreVersion": "1.0.0",
  "permissions": ["novel:read", "config:read"]
}
```

---

## 6. IToolPlugin 接口

### 6.1 精简后的接口

```csharp
public interface IToolPlugin
{
    void Initialize(IServiceProvider serviceProvider);
    IEnumerable<AgentTool> GetTools();
}
```

**移除的字段**：`Name`、`Version`、`Description` — 这些信息由 csproj 属性驱动，构建时写入 plugin.json，不再需要在 C# 代码中重复声明。

### 6.2 工具基类

```csharp
public abstract class AgentTool<TParams, TDetails> : AgentTool
{
    public abstract Task<AgentToolResult<TDetails>> ExecuteAsync(
        string toolCallId, TParams args, CancellationToken ct,
        IProgress<AgentToolResult<TDetails>>? onUpdate = null);
}
```

### 6.3 工具元数据

`AgentTool` 携带分类和实体类型元数据，驱动 TUI 渲染逻辑：

```csharp
public enum ToolCategory
{
    Generic,        // 通用工具
    FileEdit,       // 文件编辑（→ Diff 渲染）
    EntityCrud,     // 实体增删改查（→ 侧面板高亮）
    ChapterManagement, // 章节管理
    EntityRead,     // 实体读取
    SearchTool,     // 搜索工具
    BashTool,       // Bash 命令
}
```

- `ToolCategory.EntityCrud` / `EntityRead` → TUI 工具结束后刷新侧面板 + 高亮实体
- `ToolCategory.FileEdit` → TUI 使用 Diff 渲染
- `AgentTool.EntityType` → 标识实体类型（"character"/"faction"/...），用于 `EntityDisplayHelper`

### 6.4 工具结果详情接口

| 接口 | 说明 | 实现者 |
|------|------|--------|
| `IDiffDetails` | 包含 `EditHunk[]` + `Path`，TUI 渲染 Diff | WriteFileDetails / EditFileDetails / ChapterDetails / EditTextDetails |
| `INovelDetails` | 包含 `Id`，Host `LoadNovelHook` 自动切换当前小说 | ManageNovelDetails |
| `ExploreDetails` | 包含 `VariantResult[]`，TUI 弹出变体选择对话框 | ExploreTool |

---

## 7. 数据访问

### 7.1 双接口架构

| 接口 | 访问级别 | 适用对象 |
|------|---------|---------|
| `INovelStore` | 完全 CRUD | Builtin / Verified（声明了 novel:write） |
| `IPluginStore` | 只读 | External |

### 7.2 IPluginStore 只读接口

> 实际接口以 `src/Core/Data/PluginStore.cs` 为准。小说摘要返回精简的 `NovelSummary`（Id/Title/Genre），单实体读取需要 `novelId + id` 两个参数，不暴露计数方法。

```csharp
public interface IPluginStore
{
    Task<NovelSummary?> GetNovelSummary(string id);
    Task<List<NovelSummary>> GetAllNovelSummaries();
    Task<Chapter?> GetChapter(string novelId, string id);
    Task<List<Chapter>> GetChapters(string novelId);
    Task<Character?> GetCharacter(string novelId, string id);
    Task<List<Character>> GetCharacters(string novelId);
    Task<List<Plotline>> GetPlotlines(string novelId);
    Task<List<Faction>> GetFactions(string novelId);
    Task<List<FactionRelationship>> GetFactionRelationships(string novelId);
    Task<Location?> GetLocation(string novelId, string id);
    Task<List<Location>> GetLocations(string novelId);
    Task<Item?> GetItem(string novelId, string id);
    Task<List<Item>> GetItems(string novelId);
    Task<List<TimelineEvent>> GetTimelineEvents(string novelId);
    Task<List<Relationship>> GetRelationshipsByNovel(string novelId);
    Task<List<SceneDefinition>> GetScenesByNovel(string novelId);
}
```

### 7.3 正确模式

第三方插件只能读取创作资产作为上下文参考，不能直接写入文件。正确模式：插件读取数据提供上下文，让 Agent 调用宿主内置工具完成写入。

### 7.4 可用服务（从 IServiceProvider 获取）

| 服务 | Builtin | Verified | External |
|------|:-------:|:--------:|:--------:|
| `INovelStore` | ✅ | ✅（需 novel:write） | ❌ |
| `IPluginStore` | ✅ | ✅ | ✅ |
| `ILogger<T>` | ✅ | ✅ | ✅ |
| `IConfiguration` | ✅ | ✅ | ✅ |
| `PluginStore`（具体类） | ✅ | ❌ | ❌ |
| `ConfigStore` | ✅ | ❌ | ❌ |

---

## 8. 工具参数 & JSON Schema

### 8.1 参数定义

工具参数使用 `record` 类型，每个属性用 `[Description]` 标注：

```csharp
public sealed record ManageCharacterParams
{
    [Description("操作: create / update / delete / get / list")]
    public string Action { get; init; } = "list";

    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("角色ID（update/delete/get 时必填）")]
    public string? CharacterId { get; init; }

    [Description("角色姓名")]
    public string? Name { get; init; }

    // ... 更多字段
}
```

### 8.2 JSON Schema 自动生成

`ConversationLoop.CreateConversation` 自动收集工具的 JSON Schema，传给 LlmTornado 的 `ChatRequest.Tools`。

**JSON 修复**：`JsonRepairUtil` 处理 LLM 返回的畸形 JSON（单引号、缺少逗号、注释等），修复后反序列化。

### 8.3 novel_id 自动注入

工具参数 `novel_id` 为空时自动填充 `Agent.CurrentNovelId`。

---

## 9. Batch 工具模式

Batch 工具允许 LLM 在一次调用中操作多个实体，名称前缀 `batch_` 自动识别。

- `batch_create_characters` — 批量创建角色
- `batch_create_plotlines` — 批量创建情节线
- `batch_create_locations` — 批量创建地点
- `batch_create_items` — 批量创建道具
- `batch_create_factions` — 批量创建组织
- `batch_create_scenes` — 批量创建场景
- `batch_web_search` — 批量网络搜索
- `batch_search_memory` — 批量语义检索

**ToolUsageStats** 中 batch 工具占比自动计算（名称前缀 `batch_` 识别）。

---

## 10. 工具分析器（23 个，全部走云端）

> **CHANGE_07 架构决议**：本地分析引擎（`AnalyzerBase` / `本地诊断引擎` / 各 `*Analyzer` 类）
> **已删除**，全部分析能力迁移到云端 云端私有仓库（云端私有引擎）。
> 本仓库不再有本地分析器实现——插件侧只剩 `CloudAnalysisTool<TParams,TDetails>` 基类 + 23 个子类，
> 统一经 `IFictionHarnessClient`（capability `fiction.story.check`）调云端。云端不可用时返回可读错误，
> **不伪造分数、不阻塞本地创作**。

### 10.1 工具 → 云端维度映射

23 个 `CloudAnalysisTool` 子类（`src/Plugins/NovelTools/Tools/`），全部声明 `cloud:harness` 权限：

| 工具 | 维度键 | 说明 |
|------|--------|------|
| evaluate_immersion_strength | immersion | 代入感（精确数字/作者介入/行为铺垫） |
| evaluate_satisfaction_intensity | satisfaction | 爽感（伏笔回收/情绪释放/结局补偿） |
| track_character_arc | arc | 角色弧光 |
| evaluate_conflict_intensity | conflict | 冲突强度 |
| evaluate_opening_strength | opening | 开篇质量 |
| analyze_pacing_quality | pace | 节奏（波动性/张力曲线） |
| detect_description_quality | description | 描写质量 |
| analyze_supporting_character | supporting-characters | 配角功能/工具人检测 |
| detect_avoidable_mistakes | plausibility | 可避免错误 |
| check_naming_quality | naming | 命名质量 |
| evaluate_power_system | power-system | 力量体系 |
| analyze_dialogue_quality | dialogue | 对话自然度/潜台词 |
| detect_content_padding | padding | 注水检测 |
| suggest_foreshadowing | foreshadowing | 伏笔建议 |
| evaluate_plot_structure | narrative | 情节结构 |
| check_character_completeness | characters | 角色完整性 |
| evaluate_excitement_curve | excitement | 兴奋点曲线 |
| evaluate_reader_engagement | engagement | 读者卷入 |
| analyze_genius_character | genius | 高智商角色/创意 |
| detect_self_indulgence | self-indulgence | 自我感动 |
| detect_plot_holes | coherence | 情节漏洞 |
| analyze_style | style | 写作风格 |
| analyze_network_structure | network | 角色网络（PageRank/枢纽章/伏笔网络） |

> 维度键与云端 `fiction.story.check` 返回的 `dimensions` 23 键对应（云端契约细节见 `src/Core/Cloud/FictionHarnessContracts.cs`）。
> 算法/阈值/题材基准全部在云端仓库，本仓库不重复实现。

---

## 11. 构建与安装

### 11.1 构建开源插件

```bash
# 在插件项目目录下运行
mythology build                          # 编译当前目录下的插件 .csproj
mythology build /path/to/MyPlugin        # 编译指定路径的插件
mythology build --install                # 编译 + 安装到 ~/.mythology/plugins
mythology build --install-local          # 编译 + 安装到 .mythology/plugins（项目级）
mythology build -c Release               # 指定编译配置
mythology build --verbose                # 显示详细编译输出
```

### 11.2 云端插件管理

```bash
mythology plugin list                    # 列出所有已安装插件
mythology plugin list --available        # 列出云端可用的插件
mythology plugin install <name>          # 从云端安装
mythology plugin install <name> --local  # 安装到项目级
mythology plugin install <name>@1.2.0    # 安装指定版本
mythology plugin remove <name>           # 移除插件
mythology plugin update <name>           # 更新到最新版本
```

### 11.3 云端 API 设计

```
GET  /api/plugins                    → 列出所有可用插件
GET  /api/plugins/{name}             → 插件详情 + 版本列表
GET  /api/plugins/{name}/{version}/download → 下载 DLL
POST /api/plugins/{name}/publish     → 发布插件
```

---

## 12. 版本兼容性

### 12.1 版本检查

`PluginLoader` 加载时检查 `plugin.json` 中的 `minCoreVersion`，与当前 Core 版本对比：

- 版本号使用 SemVer 语义
- 主版本号不同 → 拒绝加载
- 次版本号低于 Core → 警告但允许
- 修订号差异 → 忽略

### 12.2 版本号统一

`Directory.Build.props` 中定义 `<MythologyVersion>1.0.0</MythologyVersion>`，所有项目引用统一版本号。

---

## 13. 调试

### 13.1 日志输出

```bash
mythology --debug    # 输出详细调试日志
```

日志位于 `~/.mythology/logs/mythology-{date}.log`，使用 Serilog。

### 13.2 Mock 模式

```bash
mythology --mock     # Mock 模式（无 LLM）
```

### 13.3 插件调试

- 设置 `NOVEL_PLUGINS_DIR` 环境变量指向插件编译输出目录
- 使用 `mythology build --verbose` 查看编译详情
- 插件加载失败时检查 `plugin.json` 是否存在且格式正确

---

## 14. 外置插件安装流程

### 14.1 安装路径

| 级别 | 路径 | 说明 |
|------|------|------|
| 全局 | `~/.mythology/plugins/<plugin-id>/` | 所有项目可用 |
| 项目级 | `.mythology/plugins/<plugin-id>/` | 仅当前项目 |

每个插件在独立子目录中，包含 DLL + `plugin.json`。

### 14.2 跨平台 HOME

```csharp
string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
// Windows: C:\Users\<username>
// macOS:   /Users/<username>
// Linux:   /home/<username>
```

### 14.3 去重策略

所有目录都扫描，按 `plugin.json` 的 `id` 去重，高优先级目录覆盖低优先级。

---

## 15. 攻击面推演

### 15.1 External 插件能做什么？

| 能力 | 是否允许 | 防护 |
|------|:-------:|------|
| 读取创作资产 | ✅ | 通过 IPluginStore 只读接口 |
| 写入创作资产 | ❌ | RestrictedPluginServiceProvider（ExternalAllowed 裁剪后不包含 NovelWrite，因此 INovelStore 不可见） |
| 访问文件系统 | ❌ | 无 FileSystem 服务 |
| 网络访问 | ❌ | 无 HttpClient 服务 |
| 读取配置/密钥 | ❌ | ConfigStore 不可见 |
| 获取 INovelStore | ❌ | IsAssignableTo 检查 |
| 获取 PluginStore 具体类 | ❌ | 类型精确匹配 |
| 反射获取服务 | ⚠️ | 无法完全阻止，但 DI 容器只暴露注册的服务 |

### 15.2 安全边界

```
External Plugin
  │
  ├─ IPluginStore  → ✅ 只读创作资产
  ├─ ILogger<T>    → ✅ 日志
  ├─ INovelStore   → ❌ null
  ├─ PluginStore   → ❌ null
  ├─ ConfigStore   → ❌ null
  └─ 其他服务       → 透传（不包含敏感服务）
```

---

## 16. 已有插件

| 插件 | 工具数 | 信任级别 | 存储接口 | 说明 |
|------|--------|:-------:|---------|------|
| NovelTools | 83 | Builtin | INovelStore + IFictionHarnessClient + RetrievalRestClient | 小说创作核心工具（含 9 batch + Explore + 23 云端分析器 + 导出 + 统计 + web_scrape/web_search） |
| SemanticRetrievalTool | 8 | Builtin | PluginStore + RetrievalKernel | 语义检索（含 batch_search_memory / recall_scene / recall_timeline / recall_related） |
| WebSearchTools | 6 | Builtin | IConfiguration | 网络搜索（含 batch_web_search） |
| FileSystem | 6 | Builtin | TodoStore + UserQuestionStore | 文件操作 + Todo 管理 + 用户交互（BashTool 已实现但未注册） |
| WritingTools | 10 | Builtin | INovelStore + IFictionHarnessClient | diagnose_writing + 9 enhance_*（增强工具需 Agent + harness 双非空） |

### 16.1 NovelTools — 83 个工具

**项目管理**

| 工具 | 说明 |
|------|------|
| manage_novel | 小说项目 CRUD |
| edit_entities | 设定面板全字段批量编辑（跨类型字段修改；写入经 INovelStore，成员双向对账） |
| get_project_summary | 获取项目摘要 |
| save_snapshot | 保存快照 |

**章节与大纲**

| 工具 | 说明 |
|------|------|
| write_chapter | 写入章节内容 |
| rewrite_selection | 重写选中内容 |
| edit_text | 编辑文本 |
| create_outline | 创建大纲 |
| get_outline | 获取大纲 |
| reorder_chapters | 重排章节顺序 |
| suggest_continuation | 建议续写方向 |
| get_word_count | 获取字数统计 |
| set_chapter_status | 设置章节状态 |
| delete_chapter | 删除章节 |

> `diagnose_writing` 属 WritingTools 插件，不在 NovelTools。

**角色 / 批量**

| 工具 | 说明 |
|------|------|
| manage_character | 角色 CRUD |
| batch_create_characters | 批量创建角色 |
| find_similar_character | 查找相似角色 |

**情节线 / 批量**

| 工具 | 说明 |
|------|------|
| manage_plotline | 情节线 CRUD |
| advance_plotline | 推进情节线 |
| recall_foreshadowing | 回收伏笔 |
| batch_create_plotlines | 批量创建情节线 |

**组织 / 批量**

| 工具 | 说明 |
|------|------|
| manage_faction | 组织 CRUD |
| update_faction_relationship | 更新组织关系 |
| batch_create_factions | 批量创建组织 |

**地点 / 批量**

| 工具 | 说明 |
|------|------|
| manage_location | 地点 CRUD |
| batch_create_locations | 批量创建地点 |
| find_location_by_description | 按描述查找地点 |

**道具 / 批量**

| 工具 | 说明 |
|------|------|
| manage_item | 道具 CRUD |
| transfer_item | 转移道具 |
| batch_create_items | 批量创建道具 |

**时间线 / 批量**

| 工具 | 说明 |
|------|------|
| check_timeline_consistency | 检查时间线一致性 |
| batch_create_timeline_events | 批量创建时间线事件 |

**关系 / 批量**

| 工具 | 说明 |
|------|------|
| manage_relationship | 关系 CRUD |
| batch_create_relationships | 批量创建关系 |
| get_relationship_network | 获取关系网络 |

**场景 / 批量**

| 工具 | 说明 |
|------|------|
| manage_scene | 场景 CRUD |
| batch_create_scenes | 批量创建场景 |
| build_scene | 构建场景 |

**探索**

| 工具 | 说明 |
|------|------|
| explore | 多变体创作方向选择 |

**风格分析**

| 工具 | 说明 |
|------|------|
| analyze_style | 写作风格分析 |

**漏洞检测**

| 工具 | 说明 |
|------|------|
| detect_plot_holes | 检测情节漏洞 |
| detect_inconsistencies | 检测设定矛盾 |

**导出 / 网络检索**

| 工具 | 说明 |
|------|------|
| export_chapter | 导出章节 |
| export_novel | 导出小说 |
| web_scrape | 网页抓取（走 RetrievalRestClient） |
| web_search | 网络搜索（走 RetrievalRestClient；与 WebSearchTools 的 web_search 同名，注册表按名覆盖） |

**统计**

| 工具 | 说明 |
|------|------|
| get_writing_stats | 写作统计仪表盘 |

**23 个云端分析器工具**（`CloudAnalysisTool` 派生，本地无分析引擎——见 §10）：

evaluate_immersion_strength、evaluate_satisfaction_intensity、track_character_arc、
evaluate_conflict_intensity、evaluate_opening_strength、analyze_pacing_quality、
detect_description_quality、analyze_supporting_character、detect_avoidable_mistakes、
check_naming_quality、evaluate_power_system、analyze_dialogue_quality、detect_content_padding、
suggest_foreshadowing、evaluate_plot_structure、check_character_completeness、
evaluate_excitement_curve、evaluate_reader_engagement、analyze_genius_character、
detect_self_indulgence、detect_plot_holes、analyze_style、analyze_network_structure

> 另有 `recall_foreshadowing`（伏笔回收，走 `INovelStore` 事实源，非云端分析器）。

### 16.2 SemanticRetrievalTool — 8 个工具

| 工具 | 说明 |
|------|------|
| search_memory | 语义搜索记忆 |
| recall_character | 回忆角色信息（按名精确优先 + 语义补充） |
| recall_plot | 回忆情节信息 |
| find_consistency | 查找一致性问题 |
| batch_search_memory | 批量语义搜索 |
| recall_scene | 场景检索（metadata 过滤 + hybrid） |
| recall_timeline | 时间线检索（章节范围 + 词法优先） |
| recall_related | 关联实体检索（精确关联 + hybrid） |

### 16.3 WebSearchTools — 6 个工具

| 工具 | 说明 |
|------|------|
| web_search | 网络搜索 |
| web_read | 读取网页内容 |
| search_literary_criticism | 搜索文学评论 |
| lookup_term | 查询术语 |
| recommend_similar_style | 推荐相似风格 |
| batch_web_search | 批量网络搜索 |

### 16.4 FileSystem — 6 个工具

| 工具 | 说明 |
|------|------|
| read | 读取文件 |
| write | 写入文件 |
| edit | 编辑文件 |
| todo_write | Todo 管理 |
| ask_user | 向用户提问 |
| get_pending_questions | 获取待回复问题 |

> `BashTool`（`bash`）类已实现（`FileTools.cs`），但 `FileSystemPlugin.GetTools()` 当前未注册它——
> 注册表里没有 `bash`。若需启用，在 `GetTools()` 中 `yield return new BashTool();`。

### 16.5 WritingTools — 写作增强（已实现）

| 工具 | 说明 |
|------|------|
| diagnose_writing | 分级诊断（快速/标准/深度三档），覆盖代入感、爽感、节奏、描写、对话、伏笔、文笔、钩子、信息差 9 维 |
| enhance_immersion | 修复精确数字、作者介入、行为缺乏铺垫等代入感问题 |
| enhance_satisfaction | 完善伏笔回收、情绪释放、结局补偿等满意度缺陷 |
| enhance_pacing | 优化节奏密度、句子长短、段落节奏 |
| enhance_description | 增强描写层次与感官细节 |
| enhance_dialogue | 优化对白辨识度与潜台词 |
| enhance_foreshadowing | 增强伏笔埋设与回收 |
| enhance_humanizer | 移除 AI 写作痕迹（机械对称、空洞升华、滥用破折号等） |
| enhance_prose_quality | 优化文笔质量（句法节拍、修辞密度、心理距离） |
| enhance_chapter_hook | 增强章末钩子 |

底层引擎：`EnhancementEngine`（定位 + 候选生成 + 写回，诊断阶段经 `IFictionHarnessClient` 走云端）+ `StoryCheckResultMapper`（把 `fiction.story.check` 的 dimensions 映射为本地 `DiagnosticReport`）+ `AutoSelectBest`（全自动模式下保守版选择，humanizer 仅对明确协作残留自动选）。本地诊断引擎 等本地诊断引擎已随 CHANGE_07 删除。工具依赖 `Agent` 与 `IFictionHarnessClient` 双非空才注册 enhance_*（无 Agent 或无云端时只注册 diagnose_writing）。
