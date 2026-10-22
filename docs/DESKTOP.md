# Mythology Desktop — 跨平台桌面客户端

> 面向不习惯 TUI 的写作者，提供基于 Avalonia 12 + SukiUI 的图形界面。
> 与 TUI（Host）共享 Core 层数据，独立编译为原生桌面应用（.exe/.deb/.dmg）。
> 最后更新：2026-10-18（新增桌面端自动更新：UpdateCheckService / UpdateState / UpdateCheckScheduler / UpdatePromptDialog + 设置页「关于」Tab）

---

## 目录

1. [双入口架构](#1-双入口架构)
2. [项目结构](#2-项目结构)
3. [Desktop.csproj 包引用](#3-desktopcsproj-包引用)
4. [代码共享策略](#4-代码共享策略)
5. [启动流程](#5-启动流程)
6. [主题与样式系统](#6-主题与样式系统)
7. [View 定位器](#7-view-定位器)
8. [ViewModel 架构](#8-viewmodel-架构)
9. [关键服务](#9-关键服务)
10. [自定义控件](#10-自定义控件)
11. [数据流与信号链路](#11-数据流与信号链路)
12. [Agent 事件 → UI 事件映射](#12-agent-事件--ui-事件映射)
13. [实施阶段](#13-实施阶段)
14. [构建与发布](#14-构建与发布)
15. [两入口能力对比](#15-两入口能力对比)
16. [风险清单](#16-风险清单)

---

## 1. 双入口架构

```
┌──────────────────────────────┐     ┌──────────────────────────────────┐
│  Host (TUI)                  │     │  Desktop (GUI)                   │
│                              │     │                                  │
│  src/Host/                   │     │  src/Desktop/                    │
│  OutputType=Exe              │     │  OutputType=WinExe               │
│  PackAsTool=true             │     │  PackAsTool=false                │
│                              │     │                                  │
│  安装:                       │     │  安装:                           │
│    dotnet tool install -g    │     │    MythDesktop.exe (Win)         │
│    Mythology                 │     │    （SelfContained + PublishSingleFile）│
│                              │     │                                  │
│  启动:                       │     │  启动:                           │
│    $ mythology               │     │    双击图标 / $ MythDesktop      │
│    $ mythology --debug       │     │                                  │
│                              │     │                                  │
│  引用: Core + Tui            │     │  引用: Core + ClientPlatform     │
│                              │     │       + CloudDiagnosis           │
└──────────┬───────────────────┘     └──────────┬───────────────────────┘
           │                                    │
           └──────────────┬─────────────────────┘
                          ▼
          ┌──────────────────────────────────────┐
          │  Core (src/Core/)                     │
          │                                       │
          │  Agent · ToolRegistry · FileNovelStore│
          │  RetrievalKernel · PluginLoader       │
          │  ConfigStore · LlmTornado             │
          │  CoreServiceExtensions                │
          └──────────────────────────────────────┘
```

### 1.1 关键假设

- TUI 和 Desktop 是**完全独立的二进制文件**，不互相引用，不共享运行时。
- Desktop **不引用** Host 或 Tui 项目。
- 两入口共享 `~/.mythology/` 配置目录，操作同一个小说的同一份数据。
- 插件（`Plugins/` 下各 DLL）由两入口各自加载，共享同一份编译输出。
- Desktop 无后端服务器，Agent 完全本地运行；embedding 走本地 LlmTornado（`CoreServiceExtensions` 的 `embeddingFactory` 当前传 `null`，未配置时 `DummyEmbeddingService` 降级纯词法）。
- Desktop **已入 `src/Mythology.slnx`**（`Desktop/Desktop.csproj` + `Desktop/Tests/Desktop.Tests.csproj`），随解决方案一起构建。
- Desktop 已装配 ToolInjector / tool_search / decompress / AgentMemory 工具注册（镜像 Host 管线）：延迟插件名单、dynamicMode 灰度开关、tool_search 熔断、插件卸载索引失效、AgentMemoryHook 自动标注全部对齐 Host。

### 1.2 设计边界

| 边界 | 规则 |
|---|---|
| Desktop 能打开 Host 创建的小说吗 | **能**。两入口操作同一个 `FileNovelStore` |
| Desktop 能读取 Host 的对话历史吗 | **不**。TUI 用 `FileSessionStore`（JSONL），Desktop 用 `ChatHistoryService`（SQLite） |
| 能同时运行 TUI 和 Desktop 操作同一本小说吗 | **不推荐**。写操作非事务安全 |
| Desktop 有独立的 appsettings.json 吗 | **有**。`src/Desktop/appsettings.json` |
| 需要为 Desktop 重复实现所有插件工具吗 | **不需要**。PluginLoader 在 Core 中，共享 DLL |

---

## 2. 项目结构

```
src/Desktop/
├── Desktop.csproj
├── Program.cs                            # AppBuilder 入口 + DI 容器
├── App.axaml                             # Application 主题/样式合并
├── App.axaml.cs                          # OnFrameworkInitializationCompleted + SukiTheme 色卡
├── ViewLocator.cs                        # FrozenDictionary VM→View (AOT 安全)
│
├── Assets/
│   ├── app.ico / app-icon.png / mythology-logo.png / mythology-mark.png / logo.svg
│   └── Fonts/
│       ├── Newsreader.ttf
│       ├── FragmentMono-Regular.ttf
│       └── FragmentMono-Italic.ttf
│
├── Styles/
│   ├── MythologyTheme.axaml              # DESIGN.md (Adaline) 全部 Color/Font/Radius Token
│   └── MythologyStyles.axaml             # PillButton / Card / Eyebrow / 气泡 / 全局样式
│
├── Models/
│   ├── Enums.cs                          # MessageSender, StepKind, TagKind, AgentStepStatus
│   ├── ChatMessage.cs                    # 对话消息 (ObservableStringBuilder Content)
│   ├── AgentStep.cs                      # 智能体步骤 (StepKind, Status, ThinkingBuilder)
│   ├── ConversationSummary.cs            # 侧边栏对话摘要
│   ├── EntityItem.cs                     # 设定面板实体项
│   ├── FileSystemItem.cs                 # @ 路径补全建议
│   ├── SourceInfo.cs                     # 信息来源
│   └── CloudAuthState.cs                 # 云端账号状态 (SignedIn/Validating/Invalid/Offline/...)
│
├── ViewModels/
│   ├── ViewModelBase.cs                  # ReactiveObject 基类
│   ├── MainWindowViewModel.cs            # Header 导航 + 对话生命周期 + 状态栏 + 云端状态
│   ├── ConversationViewModel.cs          # 消息列表 + 输入 + 流式处理 + 斜杠命令
│   ├── SetUpPanelViewModel.cs            # 右侧 8-Tab 实体管理面板
│   ├── TodoPanelViewModel.cs             # 右下 Todo 任务面板
│   └── SettingsViewModel.cs              # 连接器/MCP/工具/账号配置
│
├── Views/
│   ├── MainWindow.axaml + .cs            # Header + 对话流 + 设定面板 + 状态栏
│   ├── ConversationView.axaml + .cs       # 对话流 + 输入区 + 快捷键
│   ├── SetUpPanelView.axaml + .cs         # 8-Tab 实体面板 (大纲/角色/组织/地点/情节/场景/道具/关系)
│   ├── TodoPanelView.axaml + .cs          # Todo 任务面板
│   ├── SettingsView.axaml + .cs           # 设置表单 (TabControl)
│   └── SettingsWindow.cs                 # 设置模态窗口
│
├── Controls/
│   ├── AutoScrollItemsControl.cs          # 自动滚动聊天列表
│   ├── MentionParser.cs                   # @ 提及补全解析器
│   ├── EntityRefParser.cs                 # # 实体引用补全解析器（两级：类型 → 名称）
│   ├── EntityFieldRegistry.cs             # 实体字段定义注册表 (动态表单)
│   ├── EntityEditWindow.axaml + .cs       # 实体编辑动态表单窗口
│   ├── AskUserWindow.axaml + .cs          # ask_user / 云端裁决问题弹窗
│   ├── CloudLoginWindow.axaml + .cs       # 云端登录窗口 (OAuth + 设备流 + 手动 Key)
│   ├── WelcomeWindow.axaml + .cs          # 首次启动引导
│   ├── ProjectPickerWindow.axaml + .cs    # 小说项目选择/创建
│   ├── SessionManagerWindow.axaml + .cs   # 会话管理
│   ├── NotesWindow.axaml + .cs            # 作者随手记（命令面板「笔记」：确认候选/编辑/状态流转）
│   ├── TreeWindow.axaml + .cs             # 对话分支树（/tree：切换分支/摘要/标签）
│   ├── VariantReviewWindow.axaml + .cs    # explore 变体审阅（采用/继续修改/忽略）
│   ├── CommandPaletteWindow.axaml + .cs   # 命令面板（Ctrl+K：搜索 + 键盘导航 + 执行）
│   ├── MessageBox / ConfirmDialog / ConfirmDeleteWindow / MarkdownDialog / AboutDialog (.axaml + .cs)
│   └── DialogTheme.cs                     # 对话框共享色卡 (Adaline)
│
├── Converters/
│   ├── SenderToBoolConverter.cs           # 消息发送者 → 布尔 (User/Agent 判断)
│   ├── CountToVisibilityConverter.cs      # 集合数量 → 可见性
│   ├── ObservableStringBuilderToStringConverter.cs
│   ├── StringNotEmptyConverter.cs         # 非空字符串 → 可见性
│   ├── VisibilityConverters.cs            # BoolToVisibility 等
│   └── ZeroCountAndPercentConverters.cs   # 零计数/百分比
│
└── Services/
    ├── DesktopAgentBridge.cs              # Agent 桥接 (单例, 事件推送)
    ├── StreamEventHandler.cs              # 流式事件分发 (去 SignalR)
    ├── ChatHistoryService.cs              # 对话历史 SQLite 持久化 (异步)
    ├── CloudAuthService.cs                # 云端账号门面 (OAuth/设备流/手动 Key + 心跳 + 吊销)
    ├── DesktopThemeService.cs             # 深/浅色主题切换 + 持久化
    ├── ModelDiscoveryService.cs           # 模型列表发现（镜像 Tui 实现，Desktop 不引用 Tui）
    └── NovelContextHook.cs                # 小说上下文钩子 (manage_novel 后注入 WritingProfile)
```

### 2.1 文件统计

| 类别 | 文件数 |
|---|---|
| 项目/入口 | 5 |
| 样式 | 2 |
| Models | 8 |
| ViewModels | 6 |
| Views | 11 |
| Controls | 23 |
| Converters | 6 |
| Services | 6 |
| Tests | 3 |
| Assets | 5+ |
| **总计** | **~75** |

### 2.2 核心差异：文档 vs 实际

| 文档 (旧) | 实际 |
|---|---|
| SukiSideMenu 侧边栏导航 | 自定义 Header + 工作台三栏布局 (对话流 + 右侧设定面板 + 状态栏) |
| SkillsView / AgentsView 独立页面 | 插件管理在 SettingsView 的"工具"Tab，Agent 状态在状态栏 |
| MentionTextBox 控件 | `MentionParser` + Popup 补全 (更轻量) |
| OptionGroupPopup 弹窗 | `AskUserWindow`（ask_user 与云端裁决问题共用，订阅 `UserQuestionStore.PendingQuestionsCreated`） |
| Iris Brand / Orchid / Obsidian 配色 | Adaline 色系 (Forest Ink / Olive Press / Linen / Bone) |
| Poppins / CanelaDeck 字体 | Inter / Newsreader 300 / Fragment Mono |
| SukiSideMenu 覆盖样式 (NavigationStyles.axaml) | 已删除 (死代码) |

---

## 3. Desktop.csproj 包引用

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>
    <AssemblyName>MythDesktop</AssemblyName>
    <RootNamespace>Desktop</RootNamespace>
    <SelfContained>true</SelfContained>
    <PublishSingleFile>true</PublishSingleFile>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
    <NoWarn>$(NoWarn);AVLN3001</NoWarn>  <!-- 带参构造对话框仅代码路径加载 -->
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="../CloudDiagnosis/CloudDiagnosis.csproj" />
    <ProjectReference Include="../Core/Core.csproj" />
    <ProjectReference Include="../ClientPlatform/ClientPlatform.csproj" />
    <!-- 不引用 Host 或 Tui -->
  </ItemGroup>

  <ItemGroup>
    <!-- Avalonia 核心 -->
    <PackageReference Include="Avalonia" Version="12.1.1" />
    <PackageReference Include="Avalonia.Desktop" Version="12.1.1" />
    <PackageReference Include="Avalonia.Fonts.Inter" Version="12.1.1" />
    <PackageReference Include="Avalonia.Themes.Fluent" Version="12.1.1" />

    <!-- SukiUI 主题 (仅用于主题系统) -->
    <PackageReference Include="SukiUI" Version="7.0.1" />

    <!-- ReactiveUI -->
    <PackageReference Include="ReactiveUI" Version="24.1.0" />
    <PackageReference Include="ReactiveUI.Avalonia" Version="12.1.1" />
    <PackageReference Include="ReactiveUI.Avalonia.Microsoft.Extensions.DependencyInjection" Version="12.1.1" />

    <!-- LiveMarkdown 流式渲染 -->
    <PackageReference Include="LiveMarkdown.Avalonia" Version="2.4.0" />
    <PackageReference Include="LiveMarkdown.Avalonia.Math" Version="2.4.0" />
    <PackageReference Include="LiveMarkdown.Avalonia.Mermaid" Version="2.4.0" />
    <PackageReference Include="LiveMarkdown.Avalonia.Svg" Version="2.4.0" />

    <!-- 图标 -->
    <PackageReference Include="Lucide.Avalonia" Version="0.2.19" />

    <!-- 平滑滚动 -->
    <PackageReference Include="SmoothScroll.Avalonia" Version="12.0.0.14" />

    <!-- 微软扩展 -->
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.11" />
    <PackageReference Include="Microsoft.Extensions.Configuration" Version="10.0.11" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="10.0.11" />
    <PackageReference Include="Microsoft.Extensions.Configuration.EnvironmentVariables" Version="10.0.11" />
    <PackageReference Include="Microsoft.Extensions.Logging" Version="10.0.11" />
    <PackageReference Include="Microsoft.Extensions.Http" Version="10.0.11" />

    <!-- SQLite -->
    <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.11" />

    <!-- 工具 -->
    <PackageReference Include="Ulid" Version="1.4.1" />
    <PackageReference Include="QRCoder" Version="1.8.0" />
  </ItemGroup>
</Project>
```

> 版本号以 `src/Desktop/Desktop.csproj` 为准（2026-08-30 校准）。`Microsoft.Extensions.Http` 在 csproj 中重复声明了 10.0.11 与 10.0.10 两项——MSBuild 取最高版本，但重复项应清理。

### 3.1 插件与资源编译 Target

```xml
<!-- 编译 5 个插件项目 -->
<Target Name="BuildPlugins" BeforeTargets="BeforeBuild">
  <MSBuild Projects="..\Plugins\FileSystem\Plugins.FileSystem.csproj" Targets="Restore;Build" />
  <MSBuild Projects="..\Plugins\NovelTools\Plugins.NovelTools.csproj" Targets="Restore;Build" />
  <MSBuild Projects="..\Plugins\SemanticRetrievalTool\Plugins.SemanticRetrievalTool.csproj" Targets="Restore;Build" />
  <MSBuild Projects="..\Plugins\WebSearchTools\Plugins.WebSearchTools.csproj" Targets="Restore;Build" />
  <MSBuild Projects="..\Plugins\WritingTools\Plugins.WritingTools.csproj" Targets="Restore;Build" />
</Target>

<!-- 复制插件 DLL 到 Desktop 输出目录 -->
<Target Name="CopyPluginsToDesktop" AfterTargets="BuildPlugins">
  <ItemGroup>
    <_PluginSource Include="..\Host\bin\$(Configuration)\$(TargetFramework)\plugins\**\*" />
  </ItemGroup>
  <Copy SourceFiles="@(_PluginSource)" DestinationFolder="$(OutputPath)plugins\%(RecursiveDir)" SkipUnchangedFiles="true" />
</Target>

<!-- jieba.NET 词典资源 -->
<Target Name="CopyJiebaResources" AfterTargets="Build">
  ...
</Target>
```

---

## 4. 代码共享策略

### 4.1 CoreServiceExtensions — 异步注册

`CoreServiceExtensions.AddMythologyCoreServicesAsync()` 在 `src/Core/CoreServiceExtensions.cs`：

```csharp
services.AddMythologyCoreServicesAsync(
    configuration,
    useDebug,
    CancellationToken.None,
    embeddingFactory: null   // 当前不注入云端 embedding
).GetAwaiter().GetResult();
```

注册内容：Serilog 日志、数据迁移、FileNovelStore、ConfigStore、PluginStore、ToolRegistry、LlmTornadoApi、Agent、PluginLoader、RetrievalKernel、IEmbeddingService。

embedding 选择优先级（`CoreServiceExtensions.RegisterRetrievalAsync`）：`embeddingFactory`（Desktop 传 null）＞ `config.yaml` `Embedding` 段独立密钥 ＞ 回退聊天 TornadoApi ＞ `DummyEmbeddingService`（纯词法降级）。云端 `CloudEmbeddingClient` 未实现。

### 4.2 各入口独有的服务

| 服务 | Host (TUI) | Desktop (GUI) |
|---|---|---|
| UI 框架 | TuiApp (XenoAtom.Terminal) | MainWindow + Views (Avalonia) |
| 对话管理 | TuiApp 内直接更新 | DesktopAgentBridge + StreamEventHandler |
| 对话持久化 | FileSessionStore（JSONL） | ChatHistoryService（SQLite，异步） |
| 设置界面 | YAML 编辑器 | SettingsView 表单 (TabControl) |
| 插件管理 | 命令行 | SettingsView 工具 Tab |
| 实体编辑 | TUI 对话框 | EntityEditWindow 动态表单 |
| 云端登录 | /login 命令 | CloudLoginWindow（OAuth 本地回调 + 设备流 + 手动 Key）+ SettingsView 账号 Tab |
| 按需工具注入 | ✅ ToolInjector | ✅ ToolInjector（dynamicMode，镜像 Host） |
| 云端诊断链路 | ✅ CloudDiagnosis | ✅ CloudDiagnosis（Program.InitializeRuntime 装配） |

---

## 5. 启动流程

### 5.1 Program.Main

```
Program.Main(string[] args)
    │
    ├── 1. 解析命令行参数 (--debug / --mock / --novel X)
    │
    ├── 2. BuildAvaloniaApp()
    │       .UsePlatformDetect()
    │       .WithInterFont()
    │       .UseReactiveUIWithMicrosoftDependencyResolver(
    │           services => ConfigureServices(services),
    │           withResolver: provider => InitializeRuntime(provider)
    │       )
    │
    └── 3. StartWithClassicDesktopLifetime(args)
```

### 5.2 App.OnFrameworkInitializationCompleted

```
App.OnFrameworkInitializationCompleted()
    │
    ├── 1. SukiTheme 配置 (Light 基色 + Mythology 色卡: Olive Press 主色 + Sage Leaf 强调色)
    ├── 2. SukiPopupShadow 透明化延迟覆盖 (修复 SukiUI ContextMenu 阴影 bug)
    ├── 3. 全局异常处理注册
    │
    ├── 4. 从 DI 获取 MainWindowViewModel
    ├── 5. 创建 MainWindow → vm.InitializeAsync()
    │       ├── 加载小说列表 → 选中上次 / 首部小说
    │       ├── 恢复上次对话
    │       ├── 云端自动登录校验（CloudAuthService.InitializeAsync，与小说加载并行）
    │       └── 刷新右侧设定面板
    │
    └── 6. 运行时初始化（BuildAvaloniaApp 的 withResolver 回调，早于 App 生命周期）
            Plugins 加载 + CloudDiagnosis 装配 + Retrieval 同步
```

### 5.3 DI 容器完整注册清单

| 生命周期 | 服务 | 所在项目 |
|---|---|---|
| Singleton | `IConfiguration` | Core |
| Singleton | `FileNovelStore` + `INovelStore` | Core |
| Singleton | `ConfigStore` | Core |
| Singleton | `ToolRegistry` | Core |
| Singleton | `Agent` | Core |
| Singleton | `PluginLoader` | Core |
| Singleton | `RetrievalKernel` | Core |
| Singleton | `IEmbeddingService` → `EmbeddingService`（LlmTornado）或 `DummyEmbeddingService` | Core |
| Singleton | `DesktopAgentBridge` | Desktop |
| Singleton | `ChatHistoryService` | Desktop |
| Singleton | `DesktopThemeService` | Desktop |
| Singleton | `UpdateCheckService` → `IUpdateCheckService` + `UpdateState` + `UpdateCheckScheduler` | Desktop（GitHub 自动更新） |
| Singleton | `CloudSessionStore` + `CloudAuthInvalidationBus` + `CloudAuthService` | Desktop/ClientPlatform |
| Singleton | `RetrievalRestClient` + `IFictionHarnessClient`（动态 Key，经 CloudAuthService.Session） | ClientPlatform |
| Singleton | `ToolSearchEngine` | Desktop（InitializeRuntime 装配） |
| Singleton | `ToolInjector` | Desktop（agent.ToolInjector 赋值） |
| Singleton | `ToolSearchTool`（注册到 ToolRegistry） | Core |
| Singleton | `MainWindowViewModel` | Desktop |
| Transient | `ConversationViewModel` / `SetUpPanelViewModel` / `SettingsViewModel` | Desktop |

运行时初始化（`InitializeRuntime`，provider 就绪后）：
  1. `CoreServiceExtensions.LoadPlugins` 加载插件
  2. Tool Search 基础设施：ToolSearchEngine + deferredPlugins + dynamicMode → ToolInjector + ToolSearchTool → BuildIndexAsync
  3. 注册 DecompressTool + AgentMemory 工具（17 个记忆工具 + AnalyzeStyle，全部 DeferLoading）+ AgentMemoryHook
  4. 二次索引重建（包含 decompress/memory 工具）+ CloudDiagnosis 链路装配（CompositeToolHook 含 AgentMemoryHook）
  5. `SyncRetrievalIndexAsync` 启动检索对账 + 后台 Worker。

---

## 6. 主题与样式系统

### 6.1 设计语言：Adaline

DESIGN.md 定义的自然主义者标本纸感风格。
- **色板**：Linen 纸底 (`#f8f9f5`)、Forest Ink 墨 (`#0a1d08`)、Olive Press CTA (`#2b390a`)、Sage Leaf 强调 (`#4a6d47`)
- **字体**：Akkurat → Inter (工作无衬线)、Newsreader 300 (衬线标题)、Fragment Mono (字段标签)
- **表面**：Bone 卡 (`#eff2e8`)、Mist 1px 边框 (`#e1e6df`)、无阴影
- **间距**：紧凑密度，6px 元素间距，radius 10px 卡片 / 20px 按钮

### 6.2 层次结构

```
App.axaml
├── Application.Resources → MythologyTheme.axaml (Token 映射)
├── Application.Styles
│   ├── suki:SukiTheme (Light 基色)
│   ├── MythologyStyles.axaml (PillButton / Card / Eyebrow / 气泡 / 全局样式)
│   ├── LiveMarkdown.Styles
│   └── LiveMarkdown.Mermaid.Styles
```

### 6.3 MythologyTheme.axaml — Token 映射

| DESIGN.md Token | Avalonia Key | 值 |
|---|---|---|
| `--color-forest-ink` | `ColorForestInk` | `#0a1d08` |
| `--color-olive-press` | `ColorOlivePress` | `#2b390a` |
| `--color-sage-leaf` | `ColorSageLeaf` | `#4a6d47` |
| `--color-linen` | `ColorLinen` | `#f8f9f5` |
| `--color-bone` | `ColorBone` | `#eff2e8` |
| `--color-mist` | `ColorMist` | `#e1e6df` |
| `--color-slate-hollow` | `ColorSlateHollow` | `#2a332a` |
| `--color-sage-gray` | `ColorSageGray` | `#6b7860` |
| `--color-sage-mist` | `ColorSageMist` | `#a5ac9f` |
| `--color-eucalyptus` | `ColorEucalyptus` | `#c9d5c5` |
| `--color-lichen` | `ColorLichen` | `#c5ccb6` |
| `--color-surface-glow` | `ColorSurfaceGlow` | `#fdfefb` |
| `--font-akkurat` | `FontSans` | Inter |
| `--font-newsreader` | `FontSerif` | Newsreader.ttf |
| `--font-fragment-mono` | `FontMono` | FragmentMono-Regular.ttf |
| Type Scale (11/14/16/18/26/30/53) | `FontSizeTag` / `FontSizeCaption` / ... | 精确匹配 |
| Spacing (4-64) | `Spacing4` / `Spacing6` / ... | 精确匹配 |
| Radius (1.5/4/6/10/20/9999) | `RadiusSmall` / `RadiusInputs` / `RadiusCardsSm` / `RadiusCards` / ... | 精确匹配 |
| hover wash (20%) | `BrushEucalyptusWash` | Eucalyptus @ Opacity 0.2 |
| 警示 wash (13%) | `BrushAmberWash` | AmberPin @ Opacity 0.13（ProjectPicker 的 LLM 提示横幅） |

> `FontSans` 的值是**字体栈**而非单字体：`Inter, PingFang SC, Microsoft YaHei, Noto Sans CJK SC`。
> Inter 无 CJK 字形，缺省会回退到系统字体并在「中英混排」处产生行高/基线漂移——显式声明回退栈
> 让所有 `FontSans` 消费点度量一致（与 `TabItem` 的既有做法同源）。

### 6.4 SukiUI 色卡 (App.axaml.cs)

```csharp
SukiTheme.GetInstance().ChangeBaseTheme(ThemeVariant.Light);
var brandTheme = new SukiColorTheme(
    "Mythology",
    Color.Parse("#2b390a"),   // Olive Press — 主色
    Color.Parse("#4a6d47")    // Sage Leaf — 强调色
);
SukiTheme.GetInstance().AddColorTheme(brandTheme);
SukiTheme.GetInstance().ChangeColorTheme(brandTheme);
```

### 6.5 全局组件样式 (MythologyStyles.axaml)

| 组件 | Selector | 关键属性 |
|---|---|---|
| Primary Pill 按钮 | `Button.Primary` | **Olive Press** 填充, Linen 文字, 20px 圆角, hover 加深为 Forest Ink |
| Ghost 描边按钮 | `Button.Ghost` | 透明底, Lichen 1px 边框, Olive Press 文字 |
| Danger 描边按钮 | `Button.Danger` | 透明底, Crimson 1px 边框 + Crimson 文字, hover 反转为填充 |
| 透明导航 Pill | `Button.NavPill` | 透明底, Forest Ink 文字, 6px 圆角 |
| Card 卡片 | `Border.Card` | Bone 底, Mist 1px 边框, 10px 圆角, 无阴影 |
| 亮面卡片 | `Border.Card.Glow` | SurfaceGlow 底 (高于 Bone 一级) |
| Eyebrow 标签 | `TextBlock.Eyebrow` | FontMono 11px, 0.4 tracking |
| Mono 标签 | `TextBlock.Tag` | FontMono 11px, Olive Press 文字 |
| 对话框标题 | `TextBlock.DialogTitle` | 18px Regular, Forest Ink（中型窗口头部层级） |
| 大纲行 hover | `Border.OutlineRow` | hover 提亮为 Bone；`.Solid` 用于已用 SurfaceGlow 的章行 |
| 用户气泡 | `Border.UserBubble` | Olive Press 填充, Linen 文字, 16px 圆角 |
| 助手气泡 | `Border.AgentBubble` | Bone 底, Mist 1px 边框, 10px 圆角 |
| 输入框 | `TextBox.Adaline` | 透明底, Mist 1px 边框, 4px 圆角 |
| 会话输入框 | `TextBox.PromptBox` | 透明底, 无边框, 15px 字号 |
| 滚动条 | `ScrollBar Thumb` | Lichen 色调, 窄条 |

### 6.6 SukiUI 资源覆盖

```xml
<!-- 阴影全部透明 (Adaline 无阴影原则) -->
<BoxShadows x:Key="SukiPopupShadow">0 0 0 0 Transparent</BoxShadows>
<BoxShadows x:Key="SukiBigPopupShadow">0 0 0 0 Transparent</BoxShadows>
<!-- SukiUI 颜色 → Adaline 色调 -->
<Color x:Key="SukiCardBackground">#fdfefb</Color>
<Color x:Key="SukiText">#0a1d08</Color>
<Color x:Key="SukiMuteText">#6b7860</Color>
```

### 6.7 自定义 ComboBox 模板 (AdalineComboBox)

因 SukiUI / Fluent 的 ComboBox 默认模板不符合 Adaline 风格，提供了一个自定义 `ControlTheme`，包含：
- 透明底 + Mist 1px 边框 + 8px 圆角
- 弹出浮层：SurfaceGlow 底 + Mist 1px 边框 + 8px 圆角
- 无箭头、无阴影

### 6.8 自定义 ContextMenu 模板 (AdalineContextMenu)

SukiUI 的 ContextMenu 模板内嵌 Margin-16 阴影层和渐变叠层，即使 `SukiPopupShadow=Transparent` 仍有可见色块。提供了替换模板，仅保留单一内容 Border + 缩放动画。

### 6.9 Adaline 视觉约束（改 UI 前必读）

DESIGN.md 的下列红线在 Avalonia 里**不会在编译期报错**，只会让界面静默偏离，因此有回归测试兜底
（`src/Desktop/Tests/AdalineDesignSystemTests.cs`）：

| 约束 | 违反后的表现 | 测试 |
|---|---|---|
| 资源键必须在 `MythologyTheme.axaml` 声明 | `DynamicResource` 查找失败**静默回退**默认色，无异常 | `ResourceKeys_AreAllDeclaredInTheme` |
| `TextBox` 不得挂 `TargetType="ComboBox"` 的 ControlTheme | 类型不匹配 → Avalonia 静默忽略 → 控件回退到 SukiUI 默认外观 | `TextBox_NeverUsesComboBoxControlTheme` |
| 可见文本与窗口标题不得含 emoji | emoji 是彩色位图字形，与 Lucide 1px 描边体系割裂 | `Axaml_ContainsNoEmoji` / `WindowTitles_ContainNoEmoji` |
| `Border` 圆角走 token，不写裸数字 | 裸值与 `RadiusCards` / `RadiusCardsSm` 漂移 | `CornerRadius_UsesTokensNotLiteralNumbers` |

两条易踩的具体规则：

1. **图标一律用 `lucide:LucideIcon`**（`Size` 11–14，`StrokeWidth` 1.6），不用 emoji、不用图标字体。
   纯文本箭头字形（`← →`）是允许的——DESIGN.md §Category Badge 明确要求
   「trailing arrow glyph in the same color」。
2. **行动调色板只有 Forest Ink 与 Olive Press**。DESIGN.md 明令「不得引入彩色 CTA（蓝、红、亮绿）」，
   且 Crimson Specimen 「是 supporting accent，**不可作状态色**」——所以危险动作用 `Button.Danger`
   的**描边**形态，而非红色填充。


---

## 7. View 定位器

使用 `FrozenDictionary` 硬编码 VM→View 映射（AOT 安全，无反射）：

```csharp
public class ViewLocator : IDataTemplate
{
    private static readonly FrozenDictionary<string, Func<Control>> ViewFactories = new()
    {
        [nameof(MainWindowViewModel)] = () => new MainWindow(),
        [nameof(ConversationViewModel)] = () => new ConversationView(),
        [nameof(SetUpPanelViewModel)] = () => new SetUpPanelView(),
        [nameof(SettingsViewModel)] = () => new SettingsView(),
    }.ToFrozenDictionary();

    public Control? Build(object? param) { ... }
    public bool Match(object? data) => data is ViewModelBase;
}
```

---

## 8. ViewModel 架构

### 8.1 ViewModelBase

```csharp
public abstract class ViewModelBase : ReactiveObject;
```

所有 ViewModel 从此基类继承。因 `ReactiveUI.SourceGenerators` 与当前 SDK Roslyn 不兼容，属性使用手写 `RaiseAndSetIfChanged`。

### 8.2 MainWindowViewModel

| 属性 | 类型 | 说明 |
|---|---|---|
| `Novels` | `ObservableCollection<Novel>` | 小说列表 (Header 选择器) |
| `SelectedNovel` | `Novel?` | 当前选中小说 |
| `RecentConversations` | `ObservableCollection<ConversationSummary>` | 最近对话列表 (Header 下拉) |
| `ActiveConversationVm` | `ConversationViewModel?` | 当前对话 VM |
| `SetUpPanel` | `SetUpPanelViewModel` | 右侧设定面板 |
| `StatusText` | `string` | 状态栏左侧 (idle/写作中...) |
| `TokenText` | `string` | 累计 Token 计数 |
| `ModelText` | `string` | 当前模型名 |
| `ToolsText` | `string` | 工具数 |
| `ConversationTitle` | `string` | Header 当前对话标题 |
| `IsProcessing` | `bool` | Agent 处理中 |

**导航路由：**
- Header 小说选择器 → `SelectedNovel` → `ApplyNovelAsync()` → 注入 Agent 上下文 + 刷新设定面板
- Header 对话下拉 → `SelectedRecentConversation` → `OpenConversation()`
- 新建对话按钮 → `NewConversationCommand` → 创建 VM → 注册到 `_activeVms`

### 8.3 ConversationViewModel

| 属性 | 类型 | 说明 |
|---|---|---|
| `Messages` | `ObservableCollection<ChatMessage>` | 消息列表 |
| `InputText` | `string` | 输入框内容 |
| `IsProcessing` | `bool` | Agent 正在处理 |
| `PathSuggestions` | `ObservableCollection<FileSystemItem>` | @ 补全列表 |
| `IsPathPopupVisible` | `bool` | 补全弹窗可见 |
| `SelectedPathIndex` | `int` | 补全选中索引 |
| `HasMessages` | `bool` | 是否有消息 (空状态切换) |
| `CurrentConversationId` | `string` | 当前对话 ID |
| `CurrentNovelId` | `string?` | 当前小说 ID |
| `PlaceholderText` | `string` | 输入框占位符 |

**命令：**
- `SendMessageCommand` → 创建对话 → 创建用户/Agent 消息 → `DesktopAgentBridge.SendAsync()`
- `StopProcessingCommand` → `CancellationTokenSource.Cancel()` + `Agent.Abort()`
- `NewConversationCommand` → 清空消息 → 创建新对话 (异步)

### 8.4 SetUpPanelViewModel

| 属性 | 类型 | 说明 |
|---|---|---|
| `Characters` / `Factions` / `Locations` / `Plotlines` / `Scenes` / `Items` / `Relationships` / `Chapters` | `ObservableCollection<EntityItem>` | 8 个实体 Tab |
| `ProgressText` | `string` | 大纲进度文本 |
| `ProgressPercent` | `double` | 大纲进度百分比 |

`RefreshAsync()` 并行加载 8 类实体，在 `ToolExecutionEnd` 事件后自动刷新。

### 8.5 SettingsViewModel

| 分组 | 字段 |
|---|---|
| 连接器 | Provider, Model, ThinkingLevel, ContextWindow, API Key 卡片列表 |
| MCP | 服务器列表 (Name/Transport/Url/Command/Args/Enabled) |
| 工具 | 插件列表 (Name/Enabled) |
| 账号 | Cloud 登录状态 (OAuth + 手动 Key) |

数据源绑定到 `ConfigStore`，保存后即时写入 YAML 并应用到 Agent。

---

## 9. 关键服务

### 9.1 DesktopAgentBridge

职责：将 `Core.Agent` 的流式执行封装为 UI 友好的事件推送。

```csharp
public sealed class DesktopAgentBridge
{
    public event EventHandler<AgentEvent>? OnStreamEvent;
    public event Action<bool>? OnProcessingChanged;
    public event Action<int, int>? OnTokenUsage;

    public async Task SendAsync(string userMessage, CancellationToken ct) { ... }
    public void Stop() { ... }
}
```

- 直接调用 `Core.Agent`，无网络层／SignalR
- 订阅 `Agent.OnEvent` 转发所有事件
- 处理状态保护和异常处理

### 9.2 StreamEventHandler

将 Agent 事件映射到目标 `ChatMessage` 的 UI 状态。StreamEventHandler 由 `ConversationViewModel` 直接创建（非 DI 管理），保证与消息生命周期绑定。

| 事件类型 | 处理 |
|---|---|
| `StreamChunk { Text }` | `ChatMessage.Content.Append(chunk.Delta)` → LiveMarkdown 增量渲染 |
| `StreamChunk { Thinking }` | 创建 `AgentStep(Kind=Thinking)`，追加 ThinkingBuilder |
| `StreamChunk { ThinkingEnd }` | 标记 Thinking step 为 Completed |
| `ToolExecutionStart` | 创建 `AgentStep(Kind=ToolCall)` |
| `ToolExecutionUpdate` | 更新 `ResultSummary` |
| `ToolExecutionEnd` | 标记步骤完成 |
| `AgentError` | 追加错误文本到消息 |
| `CircuitBreakerTriggered` | 添加断路器步骤 |

### 9.3 ChatHistoryService

轻量 SQLite 存储对话历史，所有 I/O 异步执行。

**数据库位置：** `~/.mythology/desktop/chat_history.db`

**表结构：**
```
conversations: id, title, created_at, updated_at, message_count
messages: id, conversation_id, sender, content, timestamp, metadata (JSON)
```

**接口：**
```csharp
Task<string> CreateConversationAsync(string title = "");
Task SaveMessageAsync(string conversationId, ChatMessage message);
Task<List<ConversationSummary>> GetRecentConversationsAsync(int pageSize = 50, int offset = 0);
Task<List<ChatMessage>> GetMessagesAsync(string conversationId);
Task<string> GetOrCreateTitleAsync(string conversationId, string fallback);
Task DeleteConversationAsync(string id);
```

### 9.4 NovelContextHook

实现 `IToolHook`，在 `manage_novel` 工具成功时自动设置 `Agent.CurrentNovelId` 并注入 `WritingProfile`。

### 9.5 自动更新（UpdateCheckService / UpdateState / UpdateCheckScheduler）

GitHub Releases 自动更新（仓库公开后生效；私有期匿名 404 → 静默休眠只记日志）：

- `UpdateCheckService`（`IUpdateCheckService`）：匿名直连 `repos/HyokaChen/Mythology/releases/latest`，按 `UpdateAssetMap`（RID → `Mythology-Desktop-<V>-<rid>` 资产名）匹配安装包与 `.sha256`，`UpdateVersion` 做数值版本比较（仅 current < latest 才算有更新，剥 `v`/`+sha`）；任何失败静默按「无更新」返回。下载走资产 `browser_download_url`（`.part` 临时文件 + 流式进度，落地 `~/.mythology/updates/`）。
- `UpdateState`（DI 单例）：弹窗与设置页「关于」共用的唯一状态源（Idle/Checking/Downloading/Ready/Failed）；检测到更新即静默下载并 sha256 校验（失败删文件置 Failed）；`IsBusyProbe`（MainWindowViewModel 注入 `IsBusy`）为真时不弹窗、只亮红点，回合结束 `OnProcessingChanged(false)` 补弹；「稍后提醒」只关弹窗（不取消下载、不清状态）。
- `UpdateCheckScheduler`：启动 +20s 首查、此后每日东八区 03:00（固定 +8）循环；Desktop 无 IHost，由 `MainWindowViewModel.InitializeAsync` 手动启动（同微信长轮询）。
- 安装：Ready 后按平台启动安装器（win `setup.exe` / mac `open` dmg / linux `xdg-open` deb）→ 1.5s → `RequestAppExit`（App 注入 `desktop.Shutdown()`）。

---

## 10. 自定义控件

### 10.1 AutoScrollItemsControl

端口自 LKK，封装聊天列表自动滚动行为：
- 新消息自动滚动到底部 (`ScrollToEndImmediate`)
- 用户手动上滚时停止自动跟随
- 流式内容增长时自动滚动
- 模板要求 `x:Name="PART_ScrollViewer"` 的 ScrollViewer

### 10.2 MentionParser / EntityRefParser

轻量级输入补全解析器（`@` 与 `#` 共用同一 Popup 弹窗），替代原规划中的 `MentionTextBox` 控件：
- `MentionParser`：正则 `@([\p{L}\p{N}_\-]*)$` 定位光标前的查询词；裸 `@`（空查询）也触发弹窗；引用必须紧贴光标（Enter 换行后弹窗自动关闭）
- `EntityRefParser`：与 TUI 同款的 `#` 两级补全——一级 `#前缀` 列类型显示名（角色/组织/地点/情节/道具/关系/大纲），二级 `#类型:前缀` 按前缀过滤该类型实体名；选中名称插入 `#类型:名称␣`
- 补全来源：`INovelStore.GetCharacters()` + `GetChapters()`（`@`）；`INovelStore` 各实体查询（`#`，名册口径 = 章节用标题、关系用「甲↔乙」）
- 弹窗锚定输入卡片（`Placement=TopEdgeAlignedLeft`，`Width` 绑定 `InputCard.Bounds.Width`）⇒ 宽度与输入卡片一致
- 发送前 `InlineEntityReferencesAsync` 把可解析的 `#类型:名称` 内联为 `类型[ID:xxx]:名称`（用户气泡与输入历史保留原文；名称/类型解析不到时原样发送）

### 10.3 MessageBox / ConfirmDeleteWindow

简易模态消息框（Avalonia 无内置 MessageBox）。`ConfirmDeleteWindow` 移植自 TUI 的确认删除对话框，红色危险确认 + 取消 + Esc 关闭。

### 10.4 EntityFieldRegistry / EntityEditWindow

动态表单实体编辑系统（移植自 TUI `EntityEditDialog`）：
- `EntityFieldRegistry` 按实体类型定义字段（Tab 分组、类型、选项）
- 支持 TextBox / TextArea / Select / FreeTag 四种字段类型
- Select 支持枚举选项和 ID 引用（角色/组织/地点/章节）
- 编辑后保存到 `INovelStore`

### 10.5 UpdatePromptDialog

自动更新非模态弹窗（挂 `MainWindow` 根 Grid，`Grid.RowSpan="4"`）：全屏遮罩 `IsHitTestVisible="False"`（不拦截主界面点击）、ESC = 稍后提醒（RootPanel 可见时聚焦以捕获 Esc 隧道）；内容含版本对照 / 发布日期 / 安装包大小 / 下载进度（原生 ProgressBar）/ 更新内容 / 状态行；按钮为「稍后提醒 +（失败）重试 /（就绪）开始安装」。DataContext 为共享单例 `UpdateState`（与设置页「关于」Tab 同源）。

---

## 11. 数据流与信号链路

### 11.1 发送消息

```
用户输入 → MentionParser 解析 @ → Popup 补全 → Enter 发送
    ↓
ConversationViewModel.SendMessageCommand.Execute()
    ↓
1. 创建对话 (CreateConversationAsync)
2. 创建 User ChatMessage + Agent ChatMessage (Loading=true)
3. DesktopAgentBridge.SendAsync(text, cts.Token)
    → Agent.Prompt() → 流式事件推送
    ↓
HandleAgentEvent (UI 线程)
    → StreamEventHandler 映射事件到消息
    → Content 追加 / 步骤更新 / 来源暂存
    ↓
AgentEnd → FinalizeAgentMessage()
    → Loading=false → 异步持久化 (SaveMessageAsync)
    → ConversationCompleted 事件 → 刷新侧边栏
```

### 11.2 加载历史对话

```
MainWindowViewModel.InitializeAsync()
    → ChatHistoryService.GetRecentConversationsAsync()
    → 恢复上次对话 (LoadConversationAsync)
    → SwitchToConversation()

Header 对话下拉选择
    → OpenConversation(summary)
    → 查找 _activeVms → 命中切换 / 未命中 LoadConversationAsync
```

---

## 12. Agent 事件 → UI 事件映射

| Agent 事件 | StreamEventHandler 处理 | UI 效果 |
|---|---|---|
| `StreamChunk { Text }` | `ChatMessage.Content.Append(delta)` | LiveMarkdown 增量渲染 |
| `StreamChunk { Thinking }` | 创建 AgentStep(Kind=Thinking) | 步骤点出现 + 思考文本 |
| `StreamChunk { ThinkingEnd }` | AgentStep.Status = Completed | 步骤点完成态 |
| `ToolExecutionStart` | 创建 AgentStep(Kind=ToolCall) | 步骤点出现 |
| `ToolExecutionUpdate` | 更新 ResultSummary | 步骤摘要更新 |
| `ToolExecutionEnd` | 标记步骤完成 | 步骤状态刷新 |
| `AgentError` | 追加错误文本 | 错误信息显示 |
| `CircuitBreakerTriggered` | 添加断路器步骤 | 断路器提示 |
| `AgentEnd` | FinalizeAgentMessage + 持久化 | 消息完成，侧边栏刷新 |

---

## 13. 实施阶段

### Phase 0 — 共享基础 (已完成)

CoreServiceExtensions 创建，两入口共用 DI 注册。

### Phase 1 — Desktop 骨架 (已完成)

项目创建、App 入口、Theme/Styles、ViewLocator、MainWindow 布局、4 个空壳 View。

### Phase 2 — 对话系统 (已完成)

Model 端口、DesktopAgentBridge、StreamEventHandler、ConversationViewModel+View、LiveMarkdown 集成、发送/停止交互。

### Phase 3 — 设置与管理 (已完成)

ChatHistoryService (异步 SQLite)、SettingsView (连接器/MCP/工具/账号)、SetUpPanelView (8-Tab 实体管理)、EntityEditWindow (动态表单)。

### Phase 4 — 样式精确 + 打包 + 工具管线 (已完成)

- 字体嵌入 (Newsreader + Fragment Mono 已就绪，Poppins 替换为 Inter)
- PillButton/Card/Eyebrow 样式精确匹配 (已完成)
- 消息出现动画、步骤状态过渡 (待细化)
- ToolInjector + tool_search + decompress + AgentMemory 工具接入 (已完成)
- 快捷键：Enter 发送，Shift+Enter 换行，Esc 取消 (已完成)
- 窗口尺寸/位置持久化 (待实现)
- 跨平台打包脚本 (待实现)

---

## 14. 构建与发布

### 14.1 开发

```bash
dotnet run --project src/Desktop -- --debug
```

### 14.2 发布

```bash
# Windows
dotnet publish src/Desktop -c Release -r win-x64 --self-contained

# Linux
dotnet publish src/Desktop -c Release -r linux-x64 --self-contained

# macOS
dotnet publish src/Desktop -c Release -r osx-x64 --self-contained
```

---

## 15. 两入口能力对比

| 能力 | Host (TUI) | Desktop (GUI) |
|---|---|---|
| 小说管理 | ✅ | ✅ |
| 对话式写作 | ✅ | ✅ (流式 Markdown 渲染) |
| 工具调用 | ✅ | ✅ |
| 插件管理 | 命令行开关 | SettingsView 图形开关 |
| 向量检索 + FTS | ✅ | ✅ |
| 用户记忆 | ✅ | ✅ |
| 反复古检测 | ✅ | ✅ |
| LLM 配置 | YAML 编辑 | 图形表单 (连接器/MCP/工具) |
| 对话历史 | `--session` 参数 | 侧边栏列表 + 下拉切换 |
| 实体管理 | 命令行 | 右侧 8-Tab 面板 + 动态表单编辑 |
| 云端登录 | `/login` 命令 | OAuth 回调 + 手动 Key |
| Markdown 实时渲染 | ❌ | ✅ LiveMarkdown |
| @ 路径补全 | ❌ | ✅ MentionParser + Popup |
| # 实体引用补全 | ✅ 两级（类型 → 名称）+ 内联 ID | ✅ EntityRefParser 两级 + 发送内联 ID |
| 拖放路径 | ❌ | ✅ |
| 快捷操作 | ❌ | ✅ 空状态快捷按钮 |
| 对话分支树 | `/tree` 弹窗 | ✅ `/tree` 窗口（缩进树 + 活跃路径标记） |
| 作者随手记 | `/notes` 弹窗 | ✅ 笔记窗口（收件箱候选确认 + 编辑；`capture_note`/`manage_note`） |
| 变体审阅 | ✓ 弹窗（纯文本 Tab） | ✅ 窗口 + LiveMarkdown 预览 |
| 命令面板 | 终端内置 CommandPalette | ✅ `Ctrl+K` 窗口（搜索 + 键盘导航） |
| 模型发现 | 切换提供商时自动发现 | ✅ 连接器 + 嵌入 Tab「发现模型」按钮（共用发现逻辑） |

---

## 16. 风险清单

| # | 风险 | 影响 | 概率 | 缓解 |
|---|---|---|---|---|
| 1 | Adaline 风格覆盖 vs SukiUI 原生组件冲突 | 升级 SukiUI 时样式覆盖失效 | 低 | 锁定 LKK 已验证版本组合 |
| 2 | LiveMarkdown 在大文档上增量渲染卡顿 | UI 冻结 | 中 | 每个 `.Append()` 渲染范围有限 |
| 3 | Desktop 引 Core + Avalonia 后体积膨胀 | 发布包 > 100MB | 中 | `PublishSingleFile=true` 减体积 |
| 4 | 窗口关闭时 Agent 正在处理 | 数据丢失 | 中 | `Window.Closing` 中调用 `Agent.Abort()` |
| 5 | 两入口同时操作同一本小说 | 文件损坏 | 低 | 文档警告用户不推荐同时运行 |
| 6 | ChatHistoryService 同步事务 | 误用导致 UI 线程阻塞 | 低 | 已移除所有同步调用 |