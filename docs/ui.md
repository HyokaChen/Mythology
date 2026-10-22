# Mythology TUI 设计规范

> 本文档整合了 TUI 布局规范、写作体验增强设计、实体字段差异分析等 UI 相关设计。
> 最后更新：2026-10-06（TUI 总纲创建与全文阅读入口、检查结果/运行操作拆分、大纲无效菜单清理）

---

## 目录

1. [整体布局结构](#1-整体布局结构)
2. [状态原型](#2-状态原型)
3. [右侧面板 Tab 详情](#3-右侧面板-tab-详情)
4. [StatusBar 规范](#4-statusbar-规范)
5. [对话流内容类型规范](#5-对话流内容类型规范)
6. [用户交互面板](#6-用户交互面板)
7. [工具调用生命周期](#7-工具调用生命周期)
8. [PromptEditor 行为](#8-prompteditor-行为)
9. [Markdown 渲染规范](#9-markdown-渲染规范)
10. [Desert 主题规范](#10-desert-主题规范)
11. [对话框清单](#11-对话框清单)
12. [写作体验增强（Wave 11）](#12-写作体验增强wave-11)
13. [实体字段差异分析](#13-实体字段差异分析)
14. [图形字符宽度安全](#14-图形字符宽度安全)

---

## 1. 整体布局结构

```
┌─────────────────────────────────────────────────────────────────────┐
│  Header: 📖 Mythology │ ✨ AI 小说写作助手                            │
├──────────────────────────────────────────┬──────────────────────────┤
│                                          │ ┌──┬──┬──┬──┬──┬──┬──┬──┐│
│                                          │ │角│组│地│情│场│道│关│大││
│                                          │ │色│织│点│节│景│具│系│纲││
│  DocumentFlow (无边框, 7/10 宽)          │ ├──┴──┴──┴──┴──┴──┴──┴──┤│
│                                          │ │                      │ │
│  - 用户气泡 (右对齐, MaxWidth 65%)       │ │  设定面板内容         │ │
│  - 助手气泡 (左对齐, MarkdownControl)    │ │  (3/10 宽)           │ │
│  - 工具调用气泡 (左对齐)                 │ │                      │ │
│  - 思考气泡 (左对齐, dim 样式)           │ │                      │ │
│  - 中断气泡 (左对齐, 赤陶色)            │ │                      │ │
│  - 错误气泡 (左对齐, 红色)              │ │                      │ │
│  - 系统气泡 (居中, dim)                 │ │                      │ │
│                                          │ └──────────────────────┘ │
├──────────────────────────────────────────┴──────────────────────────┤
│  ❯ 聊天 | /命令 | #角色 | @文件【ESC/Ctrl+C 中断回复】              │
├─────────────────────────────────────────────────────────────────────┤
│  🔄 诗词 │ 📖 小说名 │ 📊 1.2k tok │ model │ tools │ Ctrl+Q      │
└─────────────────────────────────────────────────────────────────────┘
```

右侧 **8 个 Tab**：📋 大纲 / 🎭 角色 / 🏰 组织 / 📍 地点 / 📜 情节 / 🎬 场景 / 🎒 道具 / 🔗 关系

### DockLayout 层次

```
ToastHost (root, TopRight, max 3, 1.5s duration)
 └── DockLayout (full-screen)
      ├── .Top:    BackgroundPanel + Footer (Header)
      ├── .Bottom: VStack
      │     ├── FixedRatioPanel(PromptEditor, BottomSidePanel)
      │     └── MythStatusBar.Footer
      └── .Content: FixedRatioPanel(ConversationPanel, SetUpPanel)
                 (7:3 default ratio, built-in divider)
```

### ToastHost 配置

| 属性 | 值 | 说明 |
|------|-----|------|
| Position | TopRight | 右上角 |
| MaxVisible | 3 | 最多同时显示 3 条 |
| DefaultDuration | 1.5s | 自动消失时间 |

Toast 图标：Info: 💡 / Success: ✅ / Warning: ⚡ / Error: ❌ / Close: ✕（U+2715 细十字，宽度 1）

### 布局控件映射

| 区域 | 控件 | 说明 |
|------|------|------|
| Header | `BackgroundPanel` + `Footer` | 📖 标题 + ✨ 描述 |
| 对话区 | `ConversationPanel` → `DocumentFlow` (裸, 无 Group 边框) | 气泡对齐，Markdown 渲染，工具结果内嵌，FollowTail 自动滚动 |
| 右侧面板 | `SetUpPanel` → 分区分栏 `WrapHStack`(8 分区) + 常驻 `VStack`（只切可见性） | 大纲/角色/组织/地点/情节/场景/道具/关系（窄面板自动折行，标题永不截断） |
| 输入栏 | `MythPromptEditor` | 多行输入，`#`/`@`/`/` 三种补全，Agent 运行时不锁定 |
| 底部状态 | `MythStatusBar` + `Footer` | Spinner + 诗词 + 小说名 + Token 数 + 模型信息 |
| 分割线 | `FixedRatioPanel` | 7:3 默认比例，内置 divider |
| Toast | `ToastHost` | TopRight, max 3, 宽字形 emoji + ✕ 关闭键 |

---

## 2. 状态原型

### 2.1 空状态

```
┌─────────────────────────────────────────────────────────────────────┐
│  📖 Mythology │ ✨ AI 小说写作助手                                    │
├──────────────────────────────────────────┬──────────────────────────┤
│                                          │ ┌──┬──┬──┬──┬──┬──┬──┬──┐│
│                                          │ │角│组│地│情│场│道│关│大││
│                                          │ ├──┴──┴──┴──┴──┴──┴──┴──┤│
│                                          │ │  (暂无角色)            │ │
│                                          │ │                      │ │
│                                          │ │                      │ │
│                                          │ │                      │ │
│                                          │ └──────────────────────┘ │
├──────────────────────────────────────────┴──────────────────────────┤
│  ❯ _                                                                │
├─────────────────────────────────────────────────────────────────────┤
│  💤 idle │ 📖 │ 📊 0 tok │ claude-sonnet │ 116 tools │ Ctrl+Q quit │
└─────────────────────────────────────────────────────────────────────┘
```

### 2.2 普通对话

```
┌─────────────────────────────────────────────────────────────────────┐
│  📖 Mythology │ ✨ AI 小说写作助手                                    │
├──────────────────────────────────────────┬──────────────────────────┤
│                          帮我创建一个角色 │ ┌──┬──┬──┬──┬──┬──┬──┬──┐│
│                                          │ │角│组│地│情│场│道│关│大││
│  ┌────────────────────────────────────┐  │ ├──┴──┴──┴──┴──┴──┴──┴──┤│
│  │ 好的，我来为你创建一个角色。        │  │ │ ▸ 林月如              │ │
│  │                                    │  │ │   刚烈·侠义·武艺高强  │ │
│  │ 角色名为**林月如**，性格刚烈…      │  │ │ ▸ 赵灵儿              │ │
│  │                                    │  │ │   温柔·善良·女娲后人  │ │
│  │ 她的外貌是…                        │  │ │ ▸ 李逍遥              │ │
│  └────────────────────────────────────┘  │ │   正义·幽默·剑术精湛  │ │
│                                          │ │                      │ │
│                                          │ │                      │ │
│                                          │ │                      │ │
│                                          │ └──────────────────────┘ │
├──────────────────────────────────────────┴──────────────────────────┤
│  ❯ _                                                                │
├─────────────────────────────────────────────────────────────────────┤
│  💤 idle │ 📖 仙剑奇侠 │ 📊 1.2k tok │ claude-sonnet │ Ctrl+Q    │
└─────────────────────────────────────────────────────────────────────┘
```

### 2.3 工具调用 + Diff

```
│  ┌────────────────────────────────────┐
│  │ ▶ write_chapter_content            │
│  │ [#fb4934]-[/] 月色如水，洒在仙灵岛上。│
│  │ [#8ec07c]+[/] 林月如独立崖边，远望…    │
│  │ [#8ec07c]+[/] 灵儿轻声说道："月如姐姐…"│
│  │ ✓ 已写入 (4 lines added)           │
│  └────────────────────────────────────┘
```

### 2.4 Agent 运行中

```
├─────────────────────────────────────────────────────────────────────┤
│  ❯ (可提前输入下一条消息)                                            │
├─────────────────────────────────────────────────────────────────────┤
│  🔄 会当凌绝顶一览众山小 │ 📖 仙剑 │ 📊 5.1k tok │ ● run │ Ctrl+S│
└─────────────────────────────────────────────────────────────────────┘
```

### 2.5 思考气泡

```
┌────────────────────────────────────┐
│ 💭 让我分析一下当前的情节走向…     │
│ 考虑到林月如的性格设定，她应该…   │
└────────────────────────────────────┘
```
- 背景：desert0 / 文本：sand (可读但弱化) / 左对齐

### 2.6 中断气泡

```
┌────────────────────────────────────┐
│ ⛔ 生成已中断                      │
└────────────────────────────────────┘
```
- 背景：暗赤陶色 / 文本：赤陶色 / 左对齐

### 2.7 系统气泡

```
              📝 修改角色「林月如」: personality, appearance
```
- 居中 / dim 样式 / 实体变更时自动生成

---

## 3. 右侧面板分区详情

8 个分区共用一条**可折行的分区分栏**（`WrapHStack`，按钮保持自然宽度，面板变窄时折行而不是压缩文字），
内容区是「**全分区常驻 + 只切 `IsVisible`**」的宿主（8 个子面板都挂在同一 `VStack` 上）。
> 早前用 `ContentSwitcher`：它只把「活动子项」暴露给树遍历（`ChildrenCount`/`GetChild` 只含选中项），
> 非活动子树永远不执行 `AttachToApp`（`App == null`）→ app 侧绑定依赖（`Collapsible.IsExpanded → PrepareChildren/Measure`、
> 刷新重建）在其上全部失效——症状是「切过去的 tab 折叠项点不开」。常驻 + 切可见性没有这个问题。

**每个分区只有一行工具带**（分区分栏正下方，两端同构）：
`[ 搜索 <当前分区> …（占满整行）] [✕ 清空]` + 分区动作（Desktop 在元信息行：`＋ 添加` / `筛选`）
- **搜索字段占满整行宽度**（TUI = `Grid` 的 `Star` 列——`HStack` 只给自然宽度，框不会拉伸；
  Desktop = 单列 `*` Grid），右端**内嵌清除图标** `✕`（有内容才出现；TUI 为独立按钮列）。
- 筛选走独立入口 + 对话框（清除 / 应用）：实体分区 1~2 个维度（角色 = 定位 + 性别 /
  组织·地点·道具 = 类型 / 情节 = 类型 + 紧张度 / 场景 = 所属卷 / 关系 = 类型 + 强度；
  选项与编辑窗字段同源），大纲分区 = 范围 + 状态。
  TUI = 工具带 `🔍 筛选` 按钮（`EntityFilterDialog` / `ChapterFilterDialog`）；
  Desktop = 元信息行 `筛选`（`EntityFilterDialog`）。
- **占位文案不含放大镜**（放大镜只出现在「筛选」入口），随分区切换
  （`搜索 角色（名字 / 关键词）` / `搜索章节标题`）——位置 + 文案一起说明「搜的是当前分区的内容」；
  同一查询也同步作用于其余分区的列表。
- 命中数写进占位：`搜索章节标题 · 筛选中 12/48`（N = 搜索 + 筛选后的条数，M = 总数；两端同口径）。
- Desktop 的计数、`＋ 添加`、`筛选` 在字段下方的元信息行（`· 12 条` + 动作），不与搜索框抢宽度。
- 过滤发生在「行数上限裁剪（60 条）」**之前**（TUI——第 61 条以后也搜得到）；Desktop 列表虚拟化、无裁剪。
- 空态三态区分：`暂无 X`（无数据）/ `未命中「X」`（搜索）/ `（筛选后无命中）`（筛选）。
- 零 I/O：`RefreshAsync(reload: false)` 复用实体缓存重建列表（TUI）；Desktop 按全量基准重填集合。
- 匹配口径 = `Core.Shared.FuzzySearch`（子序列、忽略大小写、多词全中），TUI 与 Desktop 同一实现。
- 分区动作（DESIGN.md：动作目标必须明确）：实体分区 = `＋ 新建`（该类型唯一新建入口）+ `🔍 筛选`；
  大纲分区 = `🔍 筛选`（书级新建在树节点 `＋` / `⋯` 上）。
  关键字只走搜索框——**一个条件一个输入口**，旧对话框里的重复关键字框已删。
- 编辑 / 删除 / 引用 / 历史**下沉到每行行首右侧**（`📝 📋 📜 🗑`），目标永远是你点的那一行；
  旧工具栏那组「📝 编辑 / ❌ 删除」作用于「当前展开项」这种不可见状态（未展开时静默无效）——已删。

右栏默认只占 30% 宽度，分区分栏在 100 列终端上折成 3 行、140 列 2 行、200 列以上 1 行——
**所有分区标题始终完整可见**（旧 `TabControl` 单行 strip 会溢出，最后一个分区只露图标）。

每个实体分区使用 **Accordion** 布局（`SingleExpanded = true`），实体可展开查看详情。

### 3.1 通用 Tab 结构

```
┌────────────────────────────────────┐
│ [ 搜索 角色（名字 / 关键词）…… ✕ ]    │  ← 工具带：字段占满整行，清除图标内嵌右端
│ · 12 条                    ＋ 添加 筛选 │  ← 元信息行：计数 + 动作
├────────────────────────────────────┤
│ ▸ 林月如                          ⋯ │  ← 行右侧只有一个 ⋯（动作进 popup）
│   刚烈·侠义·武艺高强              │     展开显示详情 + 演化轨迹 + 关系段
│ ▸ 赵灵儿                          ⋯ │
│                                  │
│ (无角色 / 未命中「林」)           │
└──────────────────────────────────┘
```

**交互**：
- 点击实体名称 → 在输入框插入 `类型「名称」` 引用（`OnEntityClicked` 事件）
- 点击行尾 `⋯` → 弹出行动作菜单：`📄 查看 / 编辑`（`EntityEditDialog`）· `📋 插入引用` ·
  `📜 历史`（审计日志）· `❌ 删除`（`ConfirmDeleteDialog` 确认）——行上不再堆一排抽象小图标
  （用户报障：图标小、难辨认）。Desktop 侧同构：实体卡 `⋯` = `ContextMenu`（查看/编辑 · 插入引用 ·
  本体关系 · 历史 · 删除，**Lucide 图标**）；大纲节点 `⋯` = 详情 · 创建 · 重命名 · 移动 · 删除。
- AI 工具创建/修改实体 → 自动高亮 + 切换到对应分区（`SetHighlightEntity` + `NavigateToEntity`）

**Accordion 事件拦截**：行尾 `⋯` 标签是 `Markup`（不是 `Button`）——`AccordionActionPanel` 在
Preview（隧道）阶段按「标签 → 动作」查表拦截点击，绕开 `Collapsible` 对 PointerPressed 的捕获
（以及 `HasFocusWithin` 跟踪崩溃）；弹出层走 `ShowActionMenu`（与大纲节点同一实现）。

### 3.2 角色 Tab 🎭

```
│ ▸ 林月如                 │
│   刚烈·侠义·武艺高强     │
│ ▸ 赵灵儿                 │
│   温柔·善良·女娲后人     │
```

### 3.3 组织 Tab 🏰

```
│ ▸ 仙剑派                 │
│   正派·修仙              │
│ ▸ 锁妖塔守卫             │
│   中立·守护              │
```

### 3.4 地点 Tab 📍

```
│ ▸ 仙灵岛                 │
│   女娲遗迹·灵气充沛      │
│ ▸ 苏州城                 │
│   繁华·商贸中心          │
```

### 3.5 情节 Tab 📜

```
│ ▸ 月如身世之谜           │
│   [!] 伏笔 · 未解决      │
│ ▸ 灵儿寻母               │
│   [=] 支线 · 未解决      │
│ ▸ 仙岛危机               │
│   [✓] 主线 · 第2章已解决  │
```

图标：`[!]` 伏笔 / `[=]` 支线 / `[?]` 暗线 / `[*]` 主线 / `✓` 已解决

### 3.6 场景 Tab 🎬

```
│ ▸ 第1章·仙灵岛黄昏       │
│   对话·灵儿与月如        │
│ ▸ 第2章·蜀山论剑         │
│   动作·剑术对决          │
```

场景类型：Action / Dialogue / Reflection / Transition

### 3.7 道具 Tab 🎒

```
│ ▸ 女娲玉                 │
│   神器·灵力之源          │
│ ▸ 蜀山剑                 │
│   仙剑派镇派之宝          │
```

### 3.8 关系 Tab 🔗

```
│ ▸ 林月如 ↔ 李逍遥       │
│   恋人·深爱              │
│ ▸ 赵灵儿 ↔ 李逍遥       │
│   恋人·命中注定          │
```

### 3.9 大纲 Tab 📋

大纲 = **文件树（TUI = XenoAtom `TreeView`，Desktop = Avalonia `TreeView`）**：
根 = 当前小说，二层 = 卷，三层 = 章；结构/过滤/摘要仍由 `Core/Novels/OutlineRows.cs` 唯一决定
（两端只做「行 → 树节点」映射）。节点**常驻物化**（展开/折叠是控件行为，重建不丢展开态）。

**每行只有一个图标，且图标走框架的节点图标槽**：`TreeViewStyle.IconResolver` 按行类型返回
`📖 小说 / 📦 卷 / 📄 未分卷 / ⚪🟡🟢 章状态`（`SetUpPanel.OutlineIcon`），
**标题文本里不再拼 emoji**——否则会和框架默认图标叠成两个（用户报障：「为啥有 2 个 emoji」），
也省掉了模型数据被当 markup 转义的麻烦。

**TUI 大纲节点动作按层级展示，只保留有明确目标的操作**：`＋` 创建下一层；`⋯` 打开该节点的动作菜单。章节点没有子层，因此不显示 `＋`。
- 小说：`＋`（新建分卷）· `⋯`（详情 / 新建章节 / 查看总纲 / 检查结果 / 运行设定检查 / 世界规则 / 切换完结状态）
- 卷：`＋`（本卷新建章节）· `⋯`（详情 · 重命名 · 上移 · 下移 · 删除）
- 未分卷组头：仅 `＋`（新建章节）；它只是分组，没有独立详情、重命名或删除，不显示空的 `⋯`。
- 章：`⋯`（详情 · 打开正文 · 重命名 · 切换状态 · 插入引用 · 上移 · 下移 · 移动到卷 · 删除）
> 详情、删除等动作收在菜单里；删除先弹确认。大纲页不提供没有后续操作的「本体概览」统计入口。

**删除一律先弹确认**（TUI = `ConfirmDeleteDialog`，Desktop = `ConfirmDeleteWindow`）——防误删。
**大纲工具带已收敛**：只有「搜索章节标题 + 🔍 筛选」（+ 云端同步状态行）；书级操作全部上移到小说节点，
不再占顶部（用户反馈：既然小说已经是树根，顶部那些总纲/检查/规则/新建按钮就是重复）。

```
│ 大纲 角色 组织 地点 情节 场景 道具 关系 │
│ [ 搜索章节标题 ……] [✕] [🔍 筛选]      │
│ ▾ 📖 恋爱吧！折花君                          ＋ ⋯ │
│   ├ 总纲：主角寻找失踪的师父…                │
│   ├ 2 卷 · 5 章 · 终稿 2                    │
│   └ ▸ 📦 第1卷 · 风起 (4 章 · 6.0k 字) ＋ ⋯ │
│       ├ ⚪ 01 雪夜启程 · 3.2k              ⋯ │
│       └ 📄 未分卷 · 1 章                       ＋ │
```

**展开 / 选中 / 滚动**由 TreeView 控件负责：
- TUI = `TreeView.Roots` + `TreeNode.IsExpanded`（重建时用 `_applied*` 区分「用户改的」与「代码设的」）。
- Desktop = `TreeDataTemplate` + `<Style Selector="TreeViewItem" x:DataType="vm:OutlineNodeVm">`
  里 `IsExpanded` 双向绑定节点 VM（Avalonia TreeView how-to 的写法）→ 程序化 reveal 能展开、用户展开能回写
  `_expandedVolumes`；TreeView 自己滚动，**外层不再包 `ScrollViewer`**（否则双重滚动）。
生成后定位（`RevealChapter` / 跳转 AI 改过的实体）：展开祖先节点 + `TrySelectNode` / 选中目标节点。

**排版列（`Core/Novels/OutlineRowText.cs` 定规则，两端同数）**
- 章行：`状态形状` + `序号列`（`01`/`12` 两位对齐，千章自然扩展）+ 标题 + 状态标签 + `字数列`。
- 字数列：`1,001 → 1.0k`、`23,450 → 23k`（千进制缩写）。
- 卷行：`N 章 · 字数 · 终稿 done/total`（同一括号组，空卷不出进度）。
- 状态形状：`⚪ 草稿` / `🟡 审核` / `🟢 终稿`（`📝` 只表示「编辑」，不兼作状态）。
- Desktop 用 mono 字体 + 右对齐渲染这两列（`FontMono`），卷/章行带 `:pointerover` 底色反馈。

**书级摘要（根节点行 / 摘要条）**
- TUI：小说行只显示书名；展开后子行展示总纲首行、卷/章计数和元信息。新建项目表单可填写总纲并保存到 `Novel.Outline`；小说 `⋯ → 详情` 可编辑，`⋯ → 查看总纲` 打开可滚动的全文阅读窗（未填写时显示提示）。
- TUI：`⋯ → 检查结果` 只展开最近一次结构化报告；未运行时显示说明，不会发起检查。`⋯ → 运行设定检查` 才调用云端 `check_setup`，报告追加到对话，结构化结果回填到树中。
- TUI 大纲没有独立的「本体概览」入口；关系仍在「关系」分区及实体详情的「本体关系」段中查看。
- Desktop：树上方摘要条 = 书名 · 走向 · `N 卷` `N 章` `终稿 N` `N 字` + 右侧百分比 + 一条 3px 进度线；
  书级入口全部在小说节点的 `⋯` 菜单（小说信息 / 设定检查 / 世界规则 / 切换完结状态 / 新建分卷 / 新建章节）。

**结构树交互**
- 行尾动作（Always 可见）：小说 `＋ ⋯`；卷 `＋ ⋯`；章 `⋯`；未分卷组头 `＋ ⋯`
  （`＋` = 下一层新建且目标父节点已定；`⋯` = 该层级菜单，**详情置顶 / 删除置底**，删除必走确认弹窗。
  两端同构：TUI = 弹层 + OptionList，Desktop = `ContextMenu`，菜单项一律 Lucide 线性图标，
  菜单里的创建项与行上的 `＋` 不重复）。
- **缩进即层级**：由 TreeView 维护（TUI `IndentSize = 2`；Desktop 用模板默认）。
- **新建**：卷 / 章都从节点 `＋` 出发（TUI = 轻量对话框，Enter 即建；Desktop = 树脚下停靠输入条，
  `Enter` 可**连打**、`Esc` 取消，空白标题落「新章节」）。
  创建后自动展开所在分组 + 选中目标节点，并给新行一次性 `✨ 刚创建` 标记（下一次渲染即消失）。
- **新建章插入到当前位置**（`ChapterOrdering.PlanInsert`）：有 `afterChapterId` 时插到该章之后，
  否则追加到目标卷末尾。空号不足（`RequiresReorder`）→ `PlanFullReorder` 全章重排腾位，
  由 `FileNovelStore.ReorderAllChapters` 两阶段搬移落盘（正文 + `appearances`/`evolution` 分片同步迁移）。
- 展开「卷」：chips（推进情节线 / 关键角色）+ 完整卷纲 + 本卷章行。
- 展开「章」：完整细纲 + 正文记录（实际出场 / 状态变化）+ 关系段；
  **正文永不进列表**——单击标题或菜单「打开正文」走只读阅读器（按需单章读取 `ch{Order:D3}.md`）。
- 大纲分区工具带：搜索框（占位 `搜索章节标题`）+ `🔍 筛选`（范围 / 状态）+ 云端同步行（接线后）；
  命中数标在搜索占位（`搜索章节标题 · 筛选中 N/M`）。
- **规模铁律**：卷默认折叠；模型层不做分页窗口（章行整卷物化），宿主是 TreeView
  （TUI = XenoAtom `TreeView`，Desktop = Avalonia `TreeView` + `VirtualizingStackPanel`）——
  **只实例化可视行**，上千章时首屏仍只构建可见的那几十个节点。

### 3.10 TUI Markup 转义口径（写文案前必读）

|路径|示例|转义责任|
|---|---|---|
|插值处理器|`new Markup($"[bold]{name}[/]")`|取值**自动转义** → 插值里禁止再手工 `Escape`|
|普通字符串|`new Markup("  " + ToolCallRenderer.Escape(text))`|**必须**手工 `Escape`（`Markup(string)` 会解析标签）|
|纯文本|`string` → `TextBlock`（含 `Toast.Content`）|**不要**转义|

- 合法转义写法 = `[[` / `]]`（`ToolCallRenderer.Escape`、`MarkdownConverter.EscapeMarkup` 已统一）；
  `\[` / `\]` 会原样显示反斜杠。
- 自检：`new MarkupTextParser().Parse(text, out _)` 结果里不该残留 `[bold]` 这类标签，且 `Parse(Escape(x)) == x`。

---

## 4. StatusBar 规范 (MythStatusBar)

### 4.1 布局

```
┌─────────────────────────────────────────────────────────────────────┐
│  左侧                              │  中间 │  右侧                  │
├─────────────────────────────────────┼───────┼───────────────────────┤
│  Spinner + 诗词/idle               │       │ 小说名 │ Token │ 模型  │
└─────────────────────────────────────┴───────┴───────────────────────┘
```

### 4.2 字段说明

| 字段 | 位置 | 数据源 | 格式 |
|------|------|--------|------|
| Spinner | 左侧 | `Spinner` (Dots style) | 🔄 运行时旋转动画，空闲时隐藏 |
| 状态/诗词 | 左侧 | `State<string?>` | 运行时显示随机古诗词，空闲显示 💤 idle |
| 小说名 | 右侧 | `Agent.CurrentNovelId` → store 查询 | 📖 小说名 |
| Cloud 状态 | 右侧 | `CloudConnectionService` → `CloudConnectionState` | `🌐` Connected / `🌐`（暗淡）Authenticating·Disabled / `🌐❗` RateLimited / `🌐$` QuotaExceeded / `🌐✕` Unavailable |
| Token 数 | 右侧 | `AgentEventAdapter._cumulativeTokens` | 📊 N tok / 📊 N.Nk tok |
| 模型信息 | 右侧 | `Agent.State.ModelName` + `ToolRegistry.Count` | model │ tools │ Ctrl+Q quit |

### 4.3 状态值

| Agent 状态 | 左侧显示 | 右侧提示 |
|------------|----------|----------|
| Idle | 💤 idle | Ctrl+Q quit |
| Running | 🔄 + 随机古诗词 | Ctrl+S (中断) |
| Error | 💤 err | Ctrl+Q quit |

### 4.4 诗词库

80+ 首古诗词，12 个主题分类：壮志豪情、自然美景、思乡之情、时光流逝、爱情真挚、人生哲理、怀才不遇、友情深厚、文学创作、忧国忧民、悠然闲适、人生感慨。

每次 Agent 开始运行时随机选择一首，空闲时恢复 💤 idle。

---

## 5. 对话流内容类型规范

### 5.1 用户消息
- `FlowDocument` + Paragraph / Right, MaxWidth 65% / 纯文本，转义 markup

### 5.2 助手消息 (Markdown)
- `FlowDocument` + `MarkdownControl` / Left, MaxWidth 70% / Markdown 渲染

### 5.3 思考消息
- `FlowDocument` + dim Markup / Left / 推理/思考内容

### 5.4 工具调用 — 查询类 (Table)

```
┌────────────────────────────────────┐
│ ▶ get_character                    │
│ ┌──────────┬──────────────────┐   │
│ │ Key      │ Value            │   │
│ ├──────────┼──────────────────┤   │
│ │ Name     │ 林月如           │   │
│ │ Id       │ abc123           │   │
│ │ Personality│ 刚烈·侠义      │   │
│ └──────────┴──────────────────┘   │
│ ✓ 已获取                           │
└────────────────────────────────────┘
```

### 5.5 工具调用 — 内容修改类 (Markup Diff)

```
┌────────────────────────────────────┐
│ ▶ write_chapter                    │
│ [#fb4934]-[/] 旧内容行              │
│ [#8ec07c]+[/] 新内容行              │
│ ✓ 已写入 (2 added, 1 removed)      │
└────────────────────────────────────┘
```

数据流：`EditHunk[]` → `ToolCallRenderer.TryRenderDataDrivenDiff()` → `DiffComputer.Compute()` (DiffPlex Myers) → `CountChanges()` → `FoldContext()` → ANSI markup

### 5.6 工具调用 — Explore 变体选择

```
┌────────────────────────────────────┐
│ ▶ explore                          │
│ ┌──────────────────────────────────┐│
│ │ 🎭 悲剧走向                      ││
│ │ 🌟 英雄成长                      ││
│ │ 💔 牺牲救赎                      ││
│ └──────────────────────────────────┘│
│ ⏳ 等待用户选择…                    │
└────────────────────────────────────┘
```

### 5.7 工具调用 — 错误

```
┌────────────────────────────────────┐
│ ▶ create_character                 │
│ ┌──────────────────────────────────┐│
│ │ ✗ API rate limit exceeded.       ││
│ └──────────────────────────────────┘│
│ ✗ 创建失败                         │
└────────────────────────────────────┘
```

### 5.8 工具调用 — 执行中

```
┌────────────────────────────────────┐
│ ▶ create_plotline  ⏳ 执行中…      │
└────────────────────────────────────┘
```

---

## 6. 用户交互面板

### 6.1 Todo 面板

LLM 调用 `todo_create` / `todo_update` 等工具时显示：

```
┌────────────────────────────────────┐
│ 📋 Todo 列表                        │
├────────────────────────────────────┤
│ [1] ✅ 完成章节大纲  BlockedBy: [] │
│ [2] 🔄 角色关系梳理  BlockedBy: [1]│
│ [3] ⏳ 撰写第三章    BlockedBy: [2]│
└────────────────────────────────────┘
```

### 6.2 用户问题面板 (AskUserDialog)

LLM 调用 `ask_user` 工具时弹出：

```
┌────────────────────────────────────┐
│ ❓ 需要您的选择                     │
├────────────────────────────────────┤
│ 主角应该如何应对这个危机？         │
│ ○ 正面冲突                         │
│ ○ 智取                             │
│ ○ 寻求帮助                         │
│                                     │
│ [确认] [取消]                       │
└────────────────────────────────────┘
```

---

## 7. 工具调用生命周期

### 7.1 事件流

```
ToolExecutionStart(id, name, args)
  → 添加 DocumentFlowItem (▶ name ⏳)
  → 记录 _toolCallItemIndices[id] = itemIndex

ToolExecutionEnd(id, isError, result, details)
  → 查找 _toolCallItemIndices[id]
  → 更新 DocumentFlowItem:
      - 查询类: ▶ name + Table + ✓/✗
      - 修改类: ▶ name + Diff Markup + ✓/✗  (IDiffDetails)
      - Explore: ▶ name + 变体选择 + ⏳       (ExploreDetails)
      - 错误:   ▶ name + Markup(red) + ✗
  → 移除 _toolCallItemIndices[id]
```

### 7.2 数据驱动的渲染

`ToolCallRenderer` 通过模式匹配 `details` 对象决定渲染策略：

| Details 类型 | 渲染方式 | 判断条件 |
|-------------|----------|----------|
| `IDiffDetails` | Diff Markup + ANSI color | `details is IDiffDetails` |
| `ExploreDetails` | 变体选择对话框 | `details is ExploreDetails` |
| 工具名含 get/list/find/search/recall | `Table` | 查询类 |
| 工具名含 create/manage/batch_create | `Table` | 创建类 |
| `isError == true` | Markup (红色) | 错误 |
| 默认 | Markup + ANSI color | 通用文本 |

---

## 8. PromptEditor 行为

### 8.1 Agent 空闲时

- 正常输入，Enter 提交
- 三种补全模式：
  - `/` 开头：slash 命令补全（含 `/tools <插件名>` 二级补全）
  - `#` 开头：实体名引用补全（两阶段：`#类型:` → `#类型:名称`）
  - `@` 开头：文件路径补全
- Ctrl+J 换行

### 8.2 Agent 运行时

- **不锁定输入框**，允许用户提前输入下一条消息
- Enter 提交时，消息进入队列
- Agent 空闲后自动处理队列中的消息
- StatusBar 显示 🔄 旋转动画 + 随机诗词 + Ctrl+S 提示

### 8.3 ESC / Ctrl+C 行为

- **Agent 运行时**：中断 Agent（`Abort()`），不清空输入
- **Agent 空闲时**：清空输入框，不中断
- Ctrl+C 通过 `stty isig` 启用 SIGINT，绕过 XenoAtom 按键路由

### 8.4 # 实体引用补全

两阶段补全：
1. `#` → 显示实体类型列表（角色/组织/地点/情节/场景/道具/关系）
2. `#类型:` → 显示该类型的实体名称列表

选择后插入格式：`类型「名称」`

---

## 9. Markdown 渲染规范

> 详细的 Markdown 渲染器技术设计见 [markdown-renderer.md](./markdown-renderer.md)。

### 9.1 渲染管线

```
原始 Markdown
  → PreprocessEmojiKeycaps()     // emoji keycap 序列（1️⃣→1.）
  → PreprocessMarkdownTables()   // 管道表 → Unicode 盒绘表格（含列对齐）
  → PreprocessImages()           // ![alt](url) → 盒绘框卡片展示
  → PreprocessLinks()            // [text](url) → 追加 dim URL 显示
  → PreprocessTaskLists()        // [x]→☑, [ ]→☐
  → InsertZwspAroundEmphasis()   // CJK 与 */_ 之间插入 ZWSP
  → MarkdownMarkupConverter.Convert()  // XenoAtom 库：AST → ANSI markup
  → 移除所有 ZWSP                 // markup.Replace("\u200B", "")
  → StripHeadingHashes()          // 移除 # 前缀（跳过 [code] 块）
  → InsertHeadingDecorations()    // H1 双线 ═══ / H2 点线 ···
  → 输出
```

### 9.2 标题样式

| 级别 | 颜色 | 样式 | 装饰 |
|------|------|------|------|
| H1 | `#FABD2F` 金色 | Bold + Underline | 底部双线 `═══` |
| H2 | `#FABD2F` 金色 | Bold | 底部点线 `···` |
| H3 | `#D79921` 深金色 | Bold | 无 |
| H4 | `#B5A03C` 暗金色 | Bold | 无 |
| H5 | `#A89984` 灰色 | Bold | 无 |
| H6 | `#928374` 暗灰色 | Bold + Dim | 无 |

### 9.3 引用块与 Alert 块

**标准引用块**：前缀 `▎ `（替代 `│ `），颜色 `#504945` desert3 暗石色

**Alert 块配色**：

| Alert 类型 | 边框/标题颜色 | 色名 |
|-----------|-------------|------|
| NOTE | `#D68A48` | 暖铜色 |
| TIP | `#8EC07C` | 鼠尾草绿 |
| IMPORTANT | `#D3869B` | 沙漠玫瑰 |
| WARNING | `#FABD2F` | 金琥珀 |
| CAUTION | `#FB4934` | 赤陶色 |

### 9.4 列表与表格

- 无序列表 bullet：`▸`（替代 `•`）
- 有序列表保持数字格式
- 表格：管道表 → Unicode 盒绘（`┌┬┐│├┼┤└┴┘`），支持列对齐

### 9.5 图片卡片展示

```
┌─ 🖼 ──────────────────────────┐
│  filename.png              │
│  alt: 示例截图                │
└───────────────────────────────┘
```

### 9.6 链接 URL 显示

`[text](url)` 后追加灰色 URL：`[dim](url)[/]`

### 9.7 任务列表

`[x]` / `[X]` → `☑` / `[ ]` → `☐`

### 9.8 水平线

实线 `────`，占满整行

### 9.9 字符宽度处理

**零宽字符**（宽度 0）：ZWSP / VS / ZWJ / ZWNJ / Word Joiner / Mongolian Vowel Separator / 组合用标记

**双宽字符**（宽度 2）：CJK / 假名 / 谚文 / Emoji

### 9.10 CJK 强调修复

`InsertZwspAroundEmphasis()` 在 CJK 文本与 `*`/`_` 之间插入 ZWSP，修复 Markdig 解析。管线后续步骤中移除。

---

## 10. Desert 主题规范

### 10.1 色阶定义

| 色阶 | Hex | 用途 |
|------|-----|------|
| desert0 | `#1C1917` | 深暖黑（主背景 ConversationBg） |
| desert1 | `#2C2520` | 深土色（SidePanelBg / HeaderBg / PromptBg） |
| desert2 | `#3C3836` | 暖炭色（DialogContentBg / TabSelectedBg / Border） |
| desert3 | `#504945` | 暗石色（Dim / TabFg / ScrollbarThumb） |
| desert4 | `#EBDBB2` | 暖沙色（主文本 SidePanelFg / StatusFg） |
| desert5 | `#FBF1C7` | 浅沙色（高亮文本 TabSelectedFg / UserBubbleFg） |
| copper | `#D68A48` | 暖铜色（主强调色 AccentBlue / PromptFg） |
| amber | `#FABD2F` | 金琥珀（MarkdownHeadingFg / ToolBubbleFg） |
| terracotta | `#FB4934` | 赤陶色（AccentRed / InterruptBubbleFg / Diff 删除） |
| sage | `#8EC07C` | 鼠尾草绿（AccentGreen / Diff 插入） |
| rose | `#D3869B` | 沙漠玫瑰（IMPORTANT alert） |

### 10.2 气泡配色

| 气泡类型 | 背景 | 文本 |
|---------|------|------|
| 用户 | desert1 `#2C2520` | desert5 `#FBF1C7` |
| 助手 | desert0 `#1C1917` | desert4 `#EBDBB2` |
| 工具 | `#2C2218` 暖琥珀暗色 | amber `#FABD2F` |
| 思考 | desert0 `#1C1917` | `#D5C4A1` sand |
| 中断 | `#2C1C1C` 暗赤陶 | terracotta `#FB4934` |
| 错误 | `#3C1C1C` 深红 | `#FF6B6B` 亮红 |

### 10.3 控件样式

| 样式 | 说明 |
|------|------|
| `DesertDialog` | 无边框线，暖炭色弹出表面，铜色标签 |
| `DesertButton` | desert2 背景 + desert4 文本，hover 铜色 |
| `DesertButtonAccent` | 铜色背景 + desert0 文本（主操作按钮） |
| `DesertButtonDanger` | terracotta 文本（删除操作） |
| `DesertCloseButton` | 红色 ✕，融入标签栏 |
| `DesertTabControl` | NoBorder，金琥珀选中，暖灰未选中 |
| `DesertSwitch` | Round switch，铜色 ON / dim stone OFF |
| `DesertCollapsible` | ▸/▾ 箭头，desert4 header / copper hover |

### 10.4 浅色主题

`PanelThemes.Light` 提供完整的浅色主题配色，与深色主题使用相同色阶但反转明暗。`ThemeService` 使用 `Theme.DefaultLight` 作为浅色主题基础。

---

## 11. 对话框清单

| 对话框 | 触发方式 | 说明 |
|--------|----------|------|
| `EntityEditDialog` | 侧面板 ✎ 按钮 | 实体编辑表单（角色/组织/地点/情节/场景/道具/关系） |
| `ConfirmDeleteDialog` | 侧面板 🗑 按钮 | 删除确认 |
| `AskUserDialog` | ask_user 工具 | 用户问题交互 |
| `NovelProjectDialog` | Ctrl+O / 启动时 | 小说项目选择/创建 |
| `CloudLoginDialog` | :cloud login / /login | 云端登录三态：本地回调 OAuth（默认）→ 失败一键改用设备流 → 手动粘贴 `mk_live_` Key 兜底；SSH 环境自动跳过本地回调 |
| `SessionPickerDialog` | Ctrl+E / /session | 会话切换 |
| `SettingsDialog` | Ctrl+P | 系统设置，四个标签页：连接器（ProviderTab）/ 嵌入（EmbeddingTab）/ MCP（McpTab）/ 工具（PluginTab） |
| `ProviderTab` | SettingsDialog 内 | 连接器配置（Provider 选择 + 模型下拉/手动输入，含自定义 Base URL） |
| `EmbeddingTab` | SettingsDialog 内 | 文本向量化模型配置（模型名输入 + OpenAI 预设按钮；Provider/API Key 与连接器共享，支持 Ollama/vLLM/云端端点；留空降级纯 BM25） |
| `McpTab` | SettingsDialog 内 | MCP 服务器配置（config.yaml `mcp.servers`） |
| `PluginTab` | SettingsDialog 内 | 工具/插件启停配置（config.yaml `tools`） |
| `AboutDialog` | Ctrl+H / /help | 帮助信息 |
| `MarkdownDialog` | 代码上下文 | Markdown 预览 |
| `ToolsDialog` | /tools list | 工具列表 |
| `TreeSelectorPanel` | /tree | 对话分支树 |
| `NotesDialog` | /notes（别名 /note） | 作者随手记：Enter 编辑、Tab 切按钮（过滤 / 新建 / 确认 / 落地 / 废弃）；状态切换即重建检索索引 |
| `MythSelect` | 通用 | 选择控件 |

---

## 12. 写作体验增强（Wave 11）

### 12.1 功能总览

```
WritingTools 插件 (新增)
├── analyze_style          ← 依赖 NovelStore + LLM 分析文本
├── build_scene            ← 依赖 NovelStore + SceneDefinition
├── manage_scene           ← 依赖 NovelStore + SceneDefinition
├── detect_plot_holes      ← 依赖 NovelStore + Plotline/Foreshadowing 状态
├── detect_inconsistencies ← 依赖 NovelStore + 多实体交叉分析
├── export_novel           ← 依赖 NovelStore + 文件系统写入
└── get_writing_stats      ← 依赖 NovelStore + IStatsRepository 扩展

数据层变更
├── SceneDefinitions (新表)
├── IStatsRepository 扩展 (新增方法)
└── ISceneRepository (新接口)

TUI 变更
├── SetUpPanel: 新增「场景」Tab
├── StatusBar: Token 计数已有，写作统计可复用
└── /stats 命令: 扩展输出写作统计信息
```

### 12.2 写作风格分析器 (analyze_style)

**纯统计层**（不需要 LLM，确定性计算）：

1. **句式分析** (sentence)：按标点拆句 → 平均句长、长短句比例、节奏冲刺段检测
2. **词汇分析** (vocabulary)：简易中文分词 → 词汇丰富度、Top-N 高频词
3. **段落分析** (paragraph)：按 `\n` 拆段 → 平均段落长度、分布曲线
4. **对话分析** (dialogue)：检测引号文本 → 对话占比、轮次统计
5. **综合分析** (comprehensive)：执行以上 4 项 + 风格画像向量 + 标签匹配

**风格标签**：
- 对话占比 > 40% + 短句比 > 30% → "紧凑对话型"
- 平均句长 > 40 + 词汇丰富度 > 0.4 → "抒情叙事型"
- 段落长度方差大 + 对话/叙事交替频繁 → "节奏多变型"
- 默认 → "均衡叙事型"

### 12.3 场景构建工具 (manage_scene)

**SceneDefinition 数据模型**：

| 字段 | 类型 | 说明 |
|------|------|------|
| Title | string(200) | 场景标题 |
| ChapterId | string(24) | 所属章节 |
| Order | int | 章节内排序 |
| LocationId | string(24)? | 地点 ID |
| TimeDescription | string(200) | 时间描述 |
| ParticipantIds | string (JSON) | 参与角色 ID 列表 |
| PlotlineIds | string (JSON) | 关联情节线 ID 列表 |
| Conflict | string | 场景冲突/核心张力 |
| Goal | string | 场景目标 |
| EmotionalArc | string(200) | 情绪弧线 |
| SceneType | string(20) | action/dialogue/reflection/transition |
| Notes | string | 备注 |

### 12.4 冲突/伏笔检测 (detect_plot_holes / detect_inconsistencies)

- **detect_plot_holes**：检测未解决的伏笔、断裂的情节线、角色行为矛盾
- **detect_inconsistencies**：多实体交叉分析，检测时间线冲突、地点矛盾、角色状态不一致

### 12.5 内容导出增强 (export_novel)

支持导出为多种格式：纯文本、Markdown 合集、EPUB（预留）

### 12.6 写作统计仪表盘 (get_writing_stats)

- 总字数 / 章节字数分布
- 角色出场频率
- 情节线完成度
- 写作速度趋势

---

## 13. 实体字段差异分析

> SetUpPanel（折叠预览）与 EntityEditDialog（编辑弹窗）之间的字段展示差异。

### 13.1 差异严重程度总览

| 实体类型 | SetUpPanel 展示字段数 | EntityEditDialog 编辑字段数 | 差异程度 |
|---------|---------------------|--------------------------|---------|
| **角色 Character** | 12 | 14 | ❗ 轻微 |
| **情节线 Plotline** | 7 + 1(关联场景) | 7 | 🔴 严重 |
| **场景 Scene** | 10 | 12 | 🟡 中等 |
| **组织 Faction** | 6 + 2(成员+对外关系) | 6 | 🔴 严重 |
| **地点 Location** | 6 + 1(下级地点) | 5 | 🟡 中等 |
| **道具 Item** | 4 + 1(持有者状态) | 4 | ❗ 轻微 |
| **关系 Relationship** | 3 | 5 | ✅ 对齐 |
| **章节 Chapter** | 3 | 4 | ✅ 对齐 |

### 13.2 核心问题

1. **情节线**：SetUpPanel 展示了"关联场景"列表，EntityEditDialog 完全缺失。这是极其重要的创作信息——作者需要知道这条情节线在哪些场景中被推进。

2. **组织**：SetUpPanel 展示了成员列表和对外关系，EntityEditDialog 完全缺失。

3. **地点**：SetUpPanel 展示了层级关系（下级地点），EntityEditDialog 缺失。

4. **场景**：EntityEditDialog 多了 Order 字段（冗余，应由系统自动管理）。

### 13.3 修复建议

- 情节线 Dialog：新增只读"关联场景"展示
- 组织 Dialog：新增只读"成员列表"和"对外关系"展示
- 地点 Dialog：新增只读"下级地点"展示
- 场景 Dialog：移除 Order 字段（系统自动管理）
- 角色 Dialog：FactionId 改为 Select 下拉（当前为 TextBox）

---

## 14. 图形字符宽度安全

> 2026-10 黑块清剿。部分终端对「修饰序列」与 BMP 争议字符的宽度判定与框架账本不一致；
> 弹窗（覆盖写入）不清除多出的「伙伴格」，失配即呈黑块 / 残影。外壳文本一律使用单码点安全字形。

### 14.1 问题模型

- **修饰序列**：VS16（`U+FE0F`）/ VS15（`U+FE0E`）/ ZWJ（`U+200D`）/ ZWNJ（`U+200C`）附着于基础字符——
  部分终端按 emoji 呈现计 2 格，框架按首码点计 1 格（实测：`☁` = 1 格，`☁` + VS16 = 2 格）。
- **BMP 争议字**（East Asian Width = Ambiguous）：`☁` `⚠` `⚔` `⛩` `⚧` `⛓` `⬆` `⬇` 等在 CJK 终端
  可能按 2 格渲染，与框架 1 格判定分歧。
- **emoji 化十字**：`❎`（U+274E）在部分终端按 emoji 宽块渲染、部分按窄文本渲染，与细十字预期
  及宽度账本易失配（黑块残影）——关闭键统一 `✕`（U+2715，实测宽度 1）。
- **安全字形**：单码点、两种模型均计 2 格的 emoji（🌐 💥 🏯 …），或纯 ASCII / 窄符号（`↑` `↓` `✕` `✓` `..`）。

### 14.2 清剿映射

| 旧（易碎） | 新（安全） | 场景 |
|---|---|---|
| `☁` / `☁️` | `🌐` | 云端状态、登录、设定检查 |
| `⚠` / `⚠️` | `❗` | 警示、限流、配额 |
| `⚔` / `⚔️` | `💥` | 冲突 / 战斗类图标 |
| `⛩` / `⛩️` | `🏯` | 地标 / 场景类图标 |
| `🏛` / `🏛️` | `🏦` | 组织类图标 |
| `🛡` / `🛡️` | `🤝` | 守护 / 关系类图标 |
| `🏷` / `🏷️` | `🔖` | 标签、书签 |
| `⚧` | `🧑` | 性别 / 人物 |
| `⛓` / `⛓️` | `🚧` | 任务阻塞 |
| `⬆` / `⬇` | `↑` / `↓` | 上移 / 下移 |
| `🏙` / `🏙️` | `🌆` | 城市 / 黄昏 |
| ZWJ 家族（`👨` + ZWJ + `👦`） | `👪` | 家庭 / 人物组 |
| `❎`（U+274E） | `✕`（U+2715） | 弹窗 / Toast 关闭键（emoji 宽块与账本不一致 → 黑块） |

### 14.3 机制与回归门

- `ChromeText`（`src/Tui/Controls/ChromeText.cs`）：动态文本（会话名 / 章节名 / 删除确认标题）进弹窗标题前
  `Clean`（剥除 `FE0E` / ZWJ / ZWNJ）→ `Clip`（按显示宽度截断，ASCII `..` 标记，绝不超宽）。
- `ChromeGlyphPolicyTests`：宽字形白名单必须计 2 格、窄字形计 1 格；扫描 `src/Tui` 生产源码
  （跳过测试与纯注释行），禁止 `FE0E FE0F 200C 200D` 与 `☁ ⚠ ⚔ ⛩ 🏙 🏛 🛡 🏷 ⚧ ⛓ ⬆ ⬇ 👨 ❎` 回流。
- `ChromeTextTests`：`Clean` / `Clip` / `ForTitle` 行为锁（含「截断结果绝不超过请求宽度」断言）。
- **TabControl = Pill tab（DESIGN.md）**：strip / 未选中 tab / 溢出按钮的底色取**对话框内容面**
  （不是独立的「tab 条色」）——框架重画 strip 时宽度会算错（用户实机：切 tab 后一条色块在对话框
  右缘内外移动，点内容又消失），底色与面同色后这类错画在视觉上消失；选中态仍是篝火填色 + 粗体。
  回归锁 `TabStyles_StripBlendsWithDialogSurface`。
- **终端格宽探测（EAW=A + 字体回退）**：框架默认 `TerminalWideRuneResolvers.Default` 只把 emoji 按 2 格，
  其余按 1 格。若终端/字体把 Unicode East_Asian_Width=Ambiguous（`→ ⇔ ─ │ █ ● ★ × … •`）或
  中文 fallback 下的装饰字形（`▸ ✕ ▾ ❯`，Unicode 判 Neutral）画成 2 格，就会整行错位、行尾露底（黑带）、
  字形被顶掉（缺字）。`TerminalWidthProbe.Probe` 在 UI 接管屏幕前用 DSR 实测代表字形格宽，
  `BuildResolver` 生成 `TerminalRunOptions.WideRuneResolver`；探测失败回退框架默认（行为不变）。
  EAW=A 表由 `scripts/gen-ambiguous-table.py`（Python unicodedata）机器生成，勿手改；
  封闭集字形清单（`TerminalWidthProbe.InventoryCodePoints`）由该脚本从 `ChromeGlyphPolicyTests`
  的字形表生成，逐个实测，只覆盖「框架按 1 格」的字形（加宽是该旋钮唯一可修方向）。
  回归锁 `TerminalWidthProbeTests`。
- **滚动条必须显式上色**：框架 `ScrollBarStyle.Default` / `ScrollViewerStyle.Default` 的
  `TrackStyle`/`ThumbStyle` 都是 `null` → 滚动条格落到终端默认样式（深色主题下就是一条黑竖条）。
  用户实机：对话框里内容超长的页面 hover 显滚动条 → 右缘黑条、切 tab 换位置。
  `PanelThemes.InjectOverlayStyles` 同时注入 `ThemeStyles.BuildScrollBar` 与
  `ThemeStyles.BuildScrollViewer`（取调色板 `ScrollbarTrack` / `ScrollbarThumb`）；
  实测未注入时滚动条 6 个字形格 fg/bg 全为 `none`，注入后为 `#1f1f1f` / `#6e4a36`。
  回归锁 `ScrollBarThemeLiveTests`。
- **弹层家族样式统一注入**（`PanelThemes.InjectOverlayStyles`，由 `ThemeService` 在切换/启动时
  注入根视觉）：`Popup` / `OptionList` / `ListBox`（框架自带 `Select` 的列表）/ `MenuList` /
  `Tooltip` —— 框架自带的弹层与我们自己的 `AnchoredPopup` 都靠这条继承链拿到主题浮层色，
  不必逐个控件设样式；`OverlayStyles_CarryThemeSurfaceBackground` 钉死五者都带浮层底色。
- **弹层底色**（下拉 / ⋯ 菜单 / 补全，三处共用 `AnchoredPopup`）：`PanelThemes.BuildPopup` /
  `BuildOptionList` 给 `Popup` + `OptionList` 上主题浮层色；同时 `Theme.PopupSurface` 显式设为浮层色
  （框架从 scheme 推导出的是中性冷灰 `#1E2D37`，Desert 下就是「一块黑」）。不设的话弹层表面透 app 基础底、
  边缘留冷灰圈——用户报障的两处黑块（对话框里的类型下拉、面板里的 ⋯ 菜单）都是它。
- **对话框页内容面**：`PanelThemes.CreateDialogTabs` 给每个标签页内容包一层「内容面（DesertContentFill）
  + 主题化滚动条（`ContentScrollStyle`）」容器——裸 `TabControl` 的页面宿主不画背景、页内滚动条用默认样式
  （只有前景色），内容溢出/悬停时那些格会透出终端默认底（黑块）。与 `CreateScrollableDialogContent` 同形态。
- `RealFrameBlackBlockTests`（真实 App 帧守卫，2026-10-05 重写；**初始化时注入生产主题**
  `ThemeRegistry.Desert` + 控件样式包——不注入的话跑的是框架默认主题，测的不是生产形态）：
  覆盖「弹层展开后整块必须是主题浮层色（旧实现是基础底 + 冷灰圈）」；在 `InMemoryTerminalBackend` 上跑
  真实 `TerminalApp` + 生产布局（header / 主分栏（对话·输入｜设定面板·任务）/ status），
  断言「整帧无未绘制格」——含**整行未绘制**（旧检测器漏判的一类）；并覆盖弹窗悬停/聚焦、
  下拉弹层展开两条路径。
  > 旧版用 `VisualSnapshotRenderer` 离屏渲染生产根视觉：`ThinSplitter` 不参与布局 →
  > 44 行的帧只有 header/status 两行有效内容，守卫形同虚设（实测绿灯）。
