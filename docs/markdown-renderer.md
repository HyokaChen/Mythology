# Mythology Markdown 渲染器设计

> 本文档详细描述 TUI 终端 Markdown 渲染器的技术设计，包括渲染管线、宽度计算、Diff 展示等。
> 最后更新：2026-07-24

---

## 目录

1. [渲染管线概览](#1-渲染管线概览)
2. [CommonMark 语法符号对齐影响](#2-commonmark-语法符号对齐影响)
3. [H1-H6 标题渲染](#3-h1-h6-标题渲染)
4. [图片/链接/引用展示](#4-图片链接引用展示)
5. [Diff 展示设计](#5-diff-展示设计)
6. [CJK 强调修复](#6-cjk-强调修复)
7. [字符宽度处理](#7-字符宽度处理)
8. [实现优先级与状态](#8-实现优先级与状态)

---

## 1. 渲染管线概览

`MarkdownConverter.Convert()` 的完整预处理管线：

```
原始 Markdown
  → PreprocessEmojiKeycaps()     // emoji keycap 序列（1️⃣→1.）
  → PreprocessMarkdownTables()   // 管道表 → Unicode 盒绘表格（含列对齐）
  → PreprocessImages()           // ![alt](url) → 盒绘框卡片展示
  → PreprocessLinks()            // [text](url) → 追加 dim URL 显示
  → PreprocessTaskLists()        // [x]→☑, [ ]→☐
  → InsertZwspAroundEmphasis()   // CJK 与 */_ 之间插入 ZWSP
  → MarkdownMarkupConverter.Convert()  // XenoAtom 库：AST → ANSI markup
  → 移除所有 ZWSP (\u200B)       // markup.Replace("\u200B", "")
  → StripHeadingHashes()          // 移除 # 前缀（跳过 [code] 块）
  → InsertHeadingDecorations()    // H1 双线 ═══ / H2 点线 ···
  → 输出
```

**关键架构约束**：`PreprocessMarkdownTables()` 将管道表转为纯文本盒绘行后，XenoAtom 的 `MarkdownMarkupConverter` 不再识别这些表格——它们变成了普通段落文本。因此 XenoAtom 的 `TableStyle` / `MarkdownRenderOptions.TableStyle` 对这些表格**无效**。

---

## 2. CommonMark 语法符号对齐影响

### 2.1 VisibleLength() 处理的语法符号

`MarkdownConverter.PreprocessMarkdownTables()` 在将 Markdown 管道表转换为 Unicode 盒绘表格时，使用 `VisibleLength()` 计算单元格可见宽度。

| 语法 | 示例 | 不可见部分 | 处理状态 |
|------|------|-----------|---------|
| 粗体 `**` | `**bold**` | 4 个 `*` | ✅ 已处理 |
| 斜体 `*` | `*italic*` | 2 个 `*` | ✅ 已处理 |
| 粗斜体 `***` | `***both***` | 6 个 `*` | ✅ 已处理（按 `**`+`*` 逐个跳过） |
| 粗体 `__` | `__bold__` | 4 个 `_` | ✅ 已实现 |
| 斜体 `_` | `_italic_` | 2 个 `_` | ✅ 已实现（含 `IsUnderscoreDelimiter` 词内下划线保护） |
| 删除线 `~~` | `~~del~~` | 4 个 `~` | ✅ 已实现 |
| 内联代码 `` ` `` | `` `code` `` | 2 个反引号 | ✅ 已处理 |
| 链接 `[text](url)` | `[Go](https://x)` | `[`、`](`、url、`)` | ✅ 已实现（仅计算 text 宽度） |
| 图片 `![alt](url)` | `![img](x.png)` | `!`、`[`、`](`、url、`)` | ✅ 已实现（仅计算 alt 宽度） |
| 转义字符 `\*` | `\*not bold\*` | 反斜杠本身 | ✅ 已实现 |
| 行内 HTML | `<em>hi</em>` | 标签部分 | ⚠️ XenoAtom 默认渲染为纯文本但宽度仍被计入 |
| 标题 `#` | `# Heading` | `# ` 前缀 | ❌ 未处理（标题极少在表格内） |
| 引用 `>` | `> quote` | `> ` 前缀 | ❌ 未处理（引用极少在表格内） |

### 2.2 `***` 粗斜体的边界情况

`***text***` 在 CommonMark 中是**粗斜体**。当前 `VisibleLength()` 的处理逻辑是逐字符扫描、遇到 `*` 就跳过 1 字符，遇到 `**` 就跳过 2 字符。对于 `***text***`：
- 扫描到 `***`：先匹配 `**` 跳过 2 字符，再匹配 `*` 跳过 1 字符 → 共跳过 3 字符 ✅
- 闭合端同理 → 共跳过 3 字符 ✅

**结论**：`***` 粗斜体在当前逐个跳过逻辑下**恰好正确**，但属于巧合而非设计。长期应改为基于 Markdig AST 的精确计算。

### 2.3 改进方案

**短期**：增强现有的 `VisibleLength()` 手动解析器（已实现 `__`、`_`、`~~`、链接、图片的处理）。

**长期**：基于 Markdig AST 的精确宽度计算——将每个表格单元格的文本单独送入 Markdig 解析，遍历 AST 的 inline 节点树，仅计算 `LiteralInline` 的字符宽度，忽略所有标记字符。

---

## 3. H1-H6 标题渲染

### 3.1 当前方案：颜色明度 + 样式组合 ✅ 已实现

| 级别 | 前景色 | 字体样式 | 装饰 |
|------|--------|---------|------|
| H1 | `#FABD2F` 金色 | Bold + Underline | 底部双线 `═══` |
| H2 | `#FABD2F` 金色 | Bold | 底部点线 `···` |
| H3 | `#D79921` 深金色 | Bold | 无 |
| H4 | `#B5A03C` 暗金色 | Bold | 无 |
| H5 | `#A89984` 灰色 | Bold | 无 |
| H6 | `#928374` 暗灰色 | Bold + Dim | 无 |

**实现方式**：
- H1-H6 颜色和样式通过 `MarkdownStyle.Heading1Style` ... `Heading6Style` 直接配置
- 底部装饰线（H1 双线 `═══` / H2 点线 `···`）通过 `InsertHeadingDecorations()` 后处理插入
- `IsHeadingLine()` 检测 XenoAtom 输出的 heading 样式标记（按颜色区分级别）

### 3.2 StripHeadingHashes() ✅ 已实现

- 追踪 `[code]`/`[/code]` 标记，维护 `inCodeBlock` 状态
- 代码块内的 `# ` 前缀不再被误删
- 非代码块内的 `# ` 前缀照常移除

---

## 4. 图片/链接/引用展示

### 4.1 图片展示 ✅ 已实现

`PreprocessImages()` 将 `![alt](url)` 替换为盒绘框展示：

```
┌─ 🖼 ──────────────────────────┐
│  filename.png              │
│  alt: 示例截图                │
└───────────────────────────────┘
```

- 框使用 `[dim]` 样式，文件名用 `[bold]`，alt 文本原样显示
- 文件名从 URL 提取（取最后一个 `/` 后的部分）
- 超长时截断，框宽度自适应内容，最大 60 字符

### 4.2 链接展示 ✅ 已实现

`PreprocessLinks()` 在链接文本后追加灰色 URL 显示：

| 链接类型 | 展示方式 |
|---------|---------|
| 行内链接 `[text](url)` | `text`（XenoAtom LinkStyle）后跟 `[dim](url)[/]` |
| 自动链接 `<url>` | 完整 URL + 下划线 |

- URL 超过 60 字符时截断显示
- URL 与链接文本相同时不重复显示
- 跳过 `![image](url)`（由 `PreprocessImages` 处理）

### 4.3 引用块展示 ✅ 部分已实现

**已实现**：
- 标准引用块：`QuotePrefix` 改为 `"▎ "`，`QuotePrefixStyle` 为 desert3 暗石色
- Alert 块：五种 Alert 独立配色已配置

| Alert 类型 | 边框/标题颜色 | 色名 |
|-----------|-------------|------|
| NOTE | `#D68A48` | 暖铜色 |
| TIP | `#8EC07C` | 鼠尾草绿 |
| IMPORTANT | `#D3869B` | 沙漠玫瑰 |
| WARNING | `#FABD2F` | 金琥珀 |
| CAUTION | `#FB4934` | 赤陶色 |

**未实现**：Alert 块的盒绘框包裹、图标前缀、背景色

### 4.4 其他 Markdown 元素

| 元素 | 状态 | 说明 |
|------|------|------|
| 代码块 | ✅ 已实现 | 背景色 `#3C3836`，语言标签 dim，`MaxCodeBlockHeight=12` |
| 水平线 | ✅ 已实现 | 实线 `────`，占满整行 |
| 无序列表 | ✅ 已实现 | `▸` 替代 `•` |
| 任务列表 | ✅ 已实现 | `[x]` → `☑`，`[ ]` → `☐` |
| 表格 | ✅ 已实现 | 管道表 → Unicode 盒绘，支持列对齐 |
| 内联代码 | ✅ 已实现 | 前景 `#FB9437`，背景 `#3C3836` |

---

## 5. Diff 展示设计

### 5.1 架构：数据生产与渲染分离 ✅ 已实现

```
┌──────────────────────┐     ┌──────────────────────┐     ┌──────────────────────┐
│   EditFileTool       │     │   ToolCallRenderer   │     │   DiffComputer       │
│   (数据生产)          │ ──→ │   (数据消费+渲染)     │ ──→ │   (Diff 计算)         │
│                      │     │                      │     │                      │
│  输入: old/new 文本  │     │  输入: EditHunk[]    │     │  DiffPlex Myers diff │
│  输出: EditHunk[]    │     │  渲染: ANSI markup   │     │  → DiffLine[]        │
└──────────────────────┘     └──────────────────────┘     └──────────────────────┘
```

### 5.2 数据模型

**EditHunk** — 单次替换的结构化数据：

```csharp
public sealed record EditHunk
{
    public int OldStartLine { get; init; }     // 原始文件中的起始行号（1-based）
    public string OldText { get; init; } = ""; // 被替换的文本
    public string NewText { get; init; } = ""; // 替换后的文本
}
```

**DiffLine** — Diff 行模型（`src/Core/Shared/DiffComputer.cs`，TUI 与 Desktop 共用）：

```csharp
public enum DiffLineType { Context, Delete, Insert }

public readonly record struct DiffLine(
    DiffLineType Type,
    int? OldLineNum,
    int? NewLineNum,
    string Text
);
```

### 5.3 DiffComputer API

实现位于 `src/Core/Shared/DiffComputer.cs`（Core 层），TUI 的 `ToolCallRenderer` 与 Desktop 的 `ToolDiffBuilder` 共用同一实现，避免两端算法漂移。

| 方法 | 说明 |
|------|------|
| `Compute(oldText, newText)` | DiffPlex Myers diff，返回 `DiffLine[]` |
| `CountChanges(lines)` | 统计 Insert/Delete 行数，返回 `(added, removed)` |
| `FoldContext(lines, contextLines=5)` | GitHub 式上下文折叠 |

### 5.4 渲染管线

```
EditHunk[] (工具产出，Core 层)
  │
  ▼
ToolCallRenderer.TryRenderDataDrivenDiff()
  │
  ├─ DiffComputer.Compute(oldText, newText)    ← DiffPlex 1.9.0 Myers diff 算法
  │   → DiffLine[]  (Context/Delete/Insert + 行号)
  │
  ├─ DiffComputer.CountChanges(diffLines)      ← 统计 +N -M
  │   → (added, removed)
  │
  ├─ DiffComputer.FoldContext(diffLines, 5)    ← GitHub 式上下文折叠
  │   → 折叠后的 DiffLine[]（⋯ N lines skipped ⋯）
  │
  └─ 格式化为 ANSI markup 文本
      [#fb4934]-[/] 删除 / [#8ec07c]+[/] 插入
      → new Markup(text).Wrap(true) 渲染
```

### 5.5 渲染效果

```
┌────────────────────────────────────┐
│ ▶ write_chapter                    │
│ [#fb4934]-[/] 月色如水，洒在仙灵岛上。│
│ [#8ec07c]+[/] 林月如独立崖边，远望…    │
│ [#8ec07c]+[/] 灵儿轻声说道："月如姐姐…"│
│ ✓ 已写入 (4 lines added)           │
└────────────────────────────────────┘
```

### 5.6 渲染决策

`ToolCallRenderer` 通过模式匹配 `details` 对象决定渲染策略：

| Details 类型 | 渲染方式 |
|-------------|----------|
| `IDiffDetails` | Diff Markup + ANSI color |
| `ExploreDetails` | 变体选择对话框 |
| 查询类工具名 | Table |
| `isError == true` | Markup (红色) |
| 默认 | Markup + ANSI color |

### 5.7 行背景色方案（XenoAtom 限制）

CodeEditor Highlighter 忽略 Background 颜色（XenoAtom 框架限制），因此行背景色方案改为**前景色 + DiffIndicatorMargin（+/- gutter）**方式实现。

| 类型 | 前景 | gutter 前景 |
|------|------|-------------|
| Delete | 红色 + Dim | 红色 Bold |
| Insert | 绿色 | 绿色 Bold |
| Context | 默认 | Dim |

### 5.8 不在当前范围内

| 功能 | 原因 |
|------|------|
| Intra-line diff（行内字符级差异） | 需要更复杂算法或 DiffPlex，后续迭代 |
| Side-by-side diff（并排视图） | 需要 XenoAtom 自定义 Visual 控件 |
| 语法高亮 | 需要语言检测 + TextMateSharp 集成 |
| Context 行折叠 | 锦上添花，非核心 |

---

## 6. CJK 强调修复

### 6.1 InsertZwspAroundEmphasis() ✅ 已实现

在 CJK 文本与 `*`/`_` 之间插入 ZWSP（零宽空格），修复 Markdig 解析 CJK 强调语法的 bug。

- 同时检测 `*` 和 `_` 的连续运行
- 在 CJK 或标点字符与 `*`/`_` 之间插入 ZWSP
- 逻辑与现有 `*` 处理完全对称
- 插入的 ZWSP 会在 `Convert()` 后续步骤中被 `markup.Replace("\u200B", string.Empty)` 移除

---

## 7. 字符宽度处理

### 7.1 CharDisplayWidth() ✅ 已实现

**零宽字符**（宽度 0）：
- ZWSP (U+200B)、Variation Selector (U+FE0F)、ZWJ (U+200D)、ZWNJ (U+200C)
- Word Joiner (U+2060)、Mongolian Vowel Separator (U+180E)
- 组合用标记：U+0300–U+036F、U+1AB0–U+1AFF、U+1DC0–U+1DFF、U+20D0–U+20FF、U+FE20–U+FE2F

**双宽字符**（宽度 2）：
- CJK / 假名 / 谚文
- BMP Emoji：U+2600–U+27BF、U+2702–U+27B0
- Supplementary plane Emoji（surrogate pairs）：通过 `char.IsHighSurrogate`/`char.IsLowSurrogate` 检测

---

## 8. 实现优先级与状态

| 优先级 | 改进项 | 状态 |
|--------|--------|------|
| P0 | `VisibleLength()` 补充 `__`/`_`/`~~`/链接/图片 | ✅ 已实现 |
| P0 | `InsertZwspAroundEmphasis()` 补充 `_` 处理 | ✅ 已实现 |
| P1 | H2-H6 标题视觉层次区分 | ✅ 已实现 |
| P1 | 工具输出结构化 diff 数据（EditHunk） | ✅ 已实现 |
| P1 | Diff 渲染（Markup + ANSI color） | ✅ 已实现 |
| P2 | 图片卡片式展示 | ✅ 已实现 |
| P2 | 链接 URL 显示 | ✅ 已实现 |
| P2 | 引用块 + Alert 块自定义样式 | ✅ 部分实现 |
| P2 | 代码块/水平线/列表自定义样式 | ✅ 已实现 |
| P3 | 表格列对齐支持 | ✅ 已实现 |
| P3 | `CharDisplayWidth` 零宽字符/Emoji | ✅ 已实现 |
| P3 | `StripHeadingHashes` 代码块感知 | ✅ 已实现 |
| P4 | 基于 AST 的 `VisibleLength` | ❌ 未实现 |
| P4 | 并排 DiffViewer 组件 | ❌ 未实现 |
| P4 | TextMateSharp 语法高亮集成 | ❌ 未实现 |
