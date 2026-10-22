# harness-edit-entities — 设定面板全字段批量编辑工具（edit_entities）

## Context

用户要求：Desktop `SetUpPanelView.axaml` / TUI `SetUpPanel.cs` 八个 tab 里每个条目（大纲：小说/卷/章细纲/正文；实体：角色/组织/地点/情节/场景/道具/关系）的内容，都必须能由 harness（`ConversationLoop` 的 tool call）修改，且必须支持批量修改。

现状核查（本会话逐个读了源码，非猜测）：

- **不存在 EditEntity 工具**。`edit` 是 `src/Plugins/FileSystem/Tools/FileTools.cs` 的裸文件搜索替换：直接 `File.WriteAllTextAsync`，不经过 `INovelStore`，因此不触发 `OnEntityChanged`（TUI 不刷新）、不写向量索引、不校验章节 `Order` 文件名约束——不能用于实体编辑。
- 实体写入分散在 `manage_*`（action=create/update/delete/get/list）与章节专用工具里，**有 12 处字段缺口**，且**没有任何批量 update**（只有 `batch_create_*` 七类 + `create_outline`/`reorder_chapters`）：

| Tab | 编辑窗字段（Desktop 注册表） | 现有工具覆盖 | 缺口 |
|---|---|---|---|
| 大纲·小说 | Title/Genre/Author/TargetWords/Description/Outline/WritingStyle/TargetAudience/EnhancementProfile/AutoDiagnose/AutoEnhance/CompletionStatus | `manage_novel` 只 5 个（title/genre/description/writing-style/outline） | Author、TargetWords、TargetAudience、EnhancementProfile、AutoDiagnose、AutoEnhance、CompletionStatus；且后 4 个 `FileNovelStore` 根本不落盘（读/写都没有） |
| 大纲·分卷 | Name/Order/Summary/Description/Goal/PlotlineIds/CharacterIds/Notes | `manage_volume` 全覆盖 | — |
| 大纲·章细纲 | Title/Order/Status/VolumeId/Summary/PlotlineIds/CharacterIds/Notes | `write_chapter` 仅新建时写 title/order，按 id 更新只写 Content；`set_chapter_status` 写 Status；`reorder_chapters` 批量写 Order | Title（更新）、Summary、PlotlineIds、CharacterIds、Notes、VolumeId |
| 大纲·章正文 | Content | `write_chapter` / `edit_text` | — |
| 角色 | Name/Gender/Age/Role/Status/FactionId/Personality/Appearance/Habits/Background/Motivation/Weakness/Tags/Abilities/Notes | `manage_character` 11 个 | Status、Tags、Abilities、Notes |
| 组织 | Name/Type/Leader/HeadquartersId/Description/Goal/Members | `manage_faction` 全覆盖 | — |
| 地点 | Name/Type/Atmosphere/Description/ParentId | `manage_location` 全覆盖 | — |
| 情节 | Name/Type/Tension/PlannedRevealChapterId/ResolvedAtChapterId/Description/Turnpoints | `manage_plotline` 全覆盖 | — |
| 场景 | Title/ChapterId/LocationId/TimeDescription/SceneType/ParticipantIds/PlotlineIds/ItemIds/Conflict/Goal/EmotionalArc/Notes | `manage_scene` 12 个 | ItemIds |
| 道具 | Name/Type/Description/OwnerId | `manage_item` 全覆盖 | — |
| 关系 | CharacterAId/CharacterBId/SinceChapterId/UntilChapterId/Type/Strength/Description | `manage_relationship` 5 个 | SinceChapterId、UntilChapterId |
| 批量修改 | 以上全部 | 无 | 全部 |

目标：新增一个 harness 工具 `edit_entities`，一次调用跨多种实体类型批量改任意可写字段；字段白名单直接复用编辑窗的 `EntityFieldRegistry`（下沉到 Core），杜绝再次漂移；顺带修掉 novel 四个字段不落盘的既有缺陷（否则写了也会被面板刷新回滚）。

## Approach

### 1. 字段注册表下沉到 `Core.Data`（唯一真源）

1. 把 `src/Desktop/Controls/EntityFieldRegistry.cs` 移动到 `src/Core/Data/EntityFieldRegistry.cs`（旧路径删除）。
2. 新文件只改两处：`namespace Core.Data;`；删除文件首行 `using Core.Data;`。其余内容（`FieldType` / `FieldDef` / `TabDef` / `ChapterEditScope` / `EntityFieldRegistry` 全表）原样保留。
3. 在 `EntityFieldRegistry` 末尾追加重载（放在 `MultiSelectSource` 之后、类内）：
   ```csharp
   /// <summary>可写字段（排除 ReadOnly 展示字段）。</summary>
   public static IReadOnlyList<FieldDef> WritableFields(EntityType type) =>
       [.. GetTabs(type).SelectMany(t => t.Fields).Where(f => f.Type is not FieldType.ReadOnly)];

   /// <summary>按字段名查找可写字段：Prop 或 kebab-case 均可，忽略大小写与 - / _ 分隔符。</summary>
   public static FieldDef? FindWritableField(EntityType type, string field)
   {
       static string Norm(string s) => s.Replace("-", "").Replace("_", "").ToLowerInvariant();
       var key = Norm(field);
       return WritableFields(type).FirstOrDefault(f => Norm(f.Prop) == key);
   }
   ```
4. `src/Desktop/Tests/EntityFieldRegistryTests.cs`：把 `using Desktop.Controls;` 换成 `using Core.Data;`。`src/Desktop/Tests/EntityEditorFieldCoverageTests.cs` 已 `using Core.Data;`，删除其不再使用的 `using Desktop.Controls;`。
5. 其余调用方无需改动（都已 `using Core.Data;`）：`Desktop/Controls/EntityEditWindow.axaml.cs`、`Desktop/Controls/EntityRelationsWindow.axaml.cs`、`Desktop/ViewModels/SetUpPanelViewModel.cs`、`Desktop/Views/SetUpPanelView.axaml.cs`。TUI 的 `EntityEditDialog` 用私有同名类型，不受影响。

### 2. `FileNovelStore` 落盘 4 个 novel 字段

`src/Core/Data/FileNovelStore.cs`，三处读 + 一处写（`novel.yaml` YAML 键一律 kebab-case）：

1. `SaveNovel` 的 `dict` 在 `["writing-style"]` 之后插入：
   ```csharp
   ["target-audience"] = novel.TargetAudience,
   ["enhancement-profile"] = novel.EnhancementProfile,
   ["auto-diagnose"] = novel.AutoDiagnose,
   ["auto-enhance"] = novel.AutoEnhance,
   ```
2. `GetNovel`（约 219 行）与 `GetNovelSummary`（约 263 行）与 `GetAllNovels`（约 298 行）的对象初始化器，在 `WritingStyle = ...` 之后各插入：
   ```csharp
   TargetAudience = GetString(dict, "target-audience") ?? "",
   EnhancementProfile = GetString(dict, "enhancement-profile") ?? "commercial",
   AutoDiagnose = GetBool(dict, "auto-diagnose", true),
   AutoEnhance = GetString(dict, "auto-enhance") ?? "semi",
   ```
   默认值与 `Novel` 模型属性默认值一致，旧文件缺键时行为不变。
3. 在 `private static int GetInt(...)`（约 3556 行）下方加：
   ```csharp
   private static bool GetBool(Dictionary<string, object?> dict, string key, bool fallback) =>
       bool.TryParse(GetString(dict, key), out var value) ? value : fallback;
   ```

### 3. 新增工具 `edit_entities`

新文件 `src/Plugins/NovelTools/Tools/EditEntitiesTool.cs`（含参数记录与内部写入器），不新增任何 Core 抽象。

#### 3.1 参数 / 结果记录

```csharp
public sealed record EditEntitiesParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("要修改的条目，可跨类型混排；按顺序逐条应用")]
    public List<EntityEditItem>? Items { get; init; }
}

public sealed record EntityEditItem
{
    [Description("实体类型：novel/volume/chapter/character/faction/location/plotline/scene/item/relationship")]
    public string EntityType { get; init; } = "";

    [Description("实体ID；character/faction/location/plotline/scene/item/volume 也接受名称")]
    public string EntityId { get; init; } = "";

    [Description("字段修改列表；字段名用 kebab-case（也可用模型属性名）")]
    public List<EntityFieldPatch> Fields { get; init; } = [];
}

public sealed record EntityFieldPatch
{
    [Description("字段名，如 personality / status / plotline-ids / volume-id")]
    public string Field { get; init; } = "";

    [Description("标量值（字符串/枚举/数字/布尔）。枚举可传中文标签或英文值")]
    public string? Value { get; init; }

    [Description("列表字段的新值（tags / members / participant-ids / plotline-ids / character-ids / item-ids）；空数组 = 清空")]
    public List<string>? Values { get; init; }
}

public sealed record EditEntitiesDetails
{
    public int UpdatedCount { get; init; }
    public int FailedCount { get; init; }
    public string[] UpdatedIds { get; init; } = [];
}
```

#### 3.2 工具类

```csharp
public sealed class EditEntitiesTool(INovelStore store, RetrievalKernel? kernel = null)
    : AgentTool<EditEntitiesParams, EditEntitiesDetails>
```
- `Name => "edit_entities"`；`Aliases => ["批量编辑", "批量修改", "编辑实体", "修改字段", "EditEntity"]`；`ExampleQueries => ["把所有反派的定位改成龙套", "给三个角色补上弱点"]`；`Label => "批量编辑实体"`。
- `Category => ToolCategory.EntityCrud`；`ExecutionMode => ToolExecutionMode.Sequential`。
- `Description`：说明「按字段批量修改设定面板条目，一次调用可跨类型；写入经 INovelStore（面板自动刷新 + 向量索引）；章节正文用 write_chapter/edit_text、章节序号用 reorder_chapters」，末尾追加 `BuildFieldTable()` 的结果。
- `BuildFieldTable()`（static，首次访问即算好）：对 `[Novel, Volume, Chapter, Character, Faction, Location, Plotline, Scene, Item, Relationship]` 每个类型输出一行 `- character（角色）: name(姓名), gender(性别), ...`；字段取 `EntityFieldRegistry.WritableFields(type)`，名字用 `JsonNamingPolicy.KebabCaseLower.ConvertName(f.Prop)`，并跳过 Chapter 的 `Order`/`Content`。需要 `using System.Text;`、`using System.Text.Json;`。

`ExecuteAsync` 流程（`ThrowIfCancelled` 每个 await 前后）：
1. `Items` 为空 → `Error("请提供至少一个条目")`。
2. 逐条 `try`：
   - `type = ParseSupportedType(item.EntityType)`：先 `EntityTypeExtensions.ParseEntityType`（kebab），再 `Enum.TryParse<EntityType>(..., true)`；不在 10 类白名单 → 抛 `"不支持的实体类型 'x'。支持: novel/volume/chapter/character/faction/location/plotline/scene/item/relationship"`。
   - 载入实体（`LoadEntityAsync`）：
     - Novel：`entity-id` 为空时用 `args.NovelId`；`store.GetNovel(id)`
     - Volume：`ManageHelper.ResolveByIdOrName(await store.GetVolumes(novelId), id, v => v.Id, v => v.Name)`
     - Chapter：`store.GetChapter(novelId, id)`（仅 ID）
     - Character：`ResolveByIdOrName(await store.GetCharacters(novelId), ...)`
     - Faction：`GetFactions`；Location：`GetLocations`；Plotline：`GetPlotlines`；Item：`GetItems`；Scene：`GetScenesByNovel`
     - Relationship：`ResolveByIdOrName(await store.GetRelationshipsByNovel(novelId), id, r => r.Id, _ => r.Id)`（只按 ID）
     - 未命中 → 抛 `"未找到{EntityFieldRegistry.Label(type)} '{id}'"`。
   - `before = EntityFieldWriter.CaptureMembership(entity)`。
   - `changes = EntityFieldWriter.Apply(entity, type, item.Fields)`（校验全部字段后才落库；任何字段非法即整条失败、不写）。
   - `await EntityFieldWriter.SaveAsync(store, entity, before, ct)`。
   - `await EntityFieldWriter.IndexAsync(kernel, entity, ct)`。
   - 记一行 `✅ {类型}「{名称}」(ID: {id}) 已更新: {changes}` 到输出；`UpdatedIds.Add(entity.Id)`。
   - `catch (Exception ex) when (ex is not OperationCanceledException)`：记 `❌ {类型} {id}: {ex.Message}`，`FailedCount++`，继续下一条。
3. 汇总文本 = 各行；`IsError = UpdatedCount == 0 && FailedCount > 0`；`Details` 填 `UpdatedCount/FailedCount/UpdatedIds`。

#### 3.3 写入器 `EntityFieldWriter`（同文件，`internal static`）

- `Apply(Entity entity, EntityType type, IReadOnlyList<EntityFieldPatch> patches) -> IReadOnlyList<string>`：
  - `patches` 为空 → 抛 `"条目未提供字段"`。
  - 每个 patch：
    - `def = EntityFieldRegistry.FindWritableField(type, patch.Field)`；null → 抛 `"字段 '{field}' 不可写。{type} 可用字段: {逗号分隔的 kebab 字段名}"`。
    - Chapter 且 `def.Prop is "Order"` → 抛 `"章节序号请用 reorder_chapters（全局序唯一，直接改会撞章节文件名）"`；`"Content"` → 抛 `"章节正文请用 write_chapter / edit_text"`。
    - `prop = entity.GetType().GetProperty(def.Prop, BindingFlags.Public | BindingFlags.Instance)`；null → 抛 `"模型缺少字段 {Prop}"`。
    - 按**属性类型**赋值（不是 FieldType）：
      - `List<string>`：`patch.Values ?? throw "字段 '{name}' 是列表字段，请用 values 传数组"`；赋 `new List<string>(patch.Values)`。
      - `string`：`raw = CoerceOption(def, patch.Value)`（有 `OptionValues` 时中文标签 → 对应 value；无则原样）；空值 → 可空引用类型赋 `null`、非空赋 `""`（用 `NullabilityInfoContext.WriteState` 判定）。
      - `bool`：接受 `true/false/1/0/是/否/on/off/开/关`，非法抛 `"字段 '{name}' 取值 '{raw}' 不是布尔值"`。
      - `int` / `double`：`TryParse` + `InvariantCulture`，非法抛 `"字段 '{name}' 取值 '{raw}' 不是数字"`。
      - `enum`：先 `CoerceOption`；`RelationshipType` 走 `RelationshipTypeExtensions.ParseRelationshipType(raw)`（支持 kebab），其余用 `Enum.TryParse(type, raw.Replace("-", "").Replace("_", ""), true, ...)`；失败抛 `"字段 '{name}' 取值 '{raw}' 无效。可选: {中文=英文 列表}"`。
      - 其它类型 → 抛 `"字段 '{name}' 类型不支持（{PropertyType.Name}）"`。
    - changes 追加 `"{kebab 字段名}: {ManageHelper.Diff(旧, 30)} → {ManageHelper.Diff(新, 30)}"`（列表用 `、` 拼接；旧值在赋值前捕获）。
- `CaptureMembership(Entity) -> MembershipBefore(string? FactionId, IReadOnlyList<string> Members)`：Character 取 `FactionId`，Faction 取 `Members` 拷贝，其它返回空记录。
- `SaveAsync(INovelStore store, Entity entity, MembershipBefore before, CancellationToken ct)`：
  - `Character`：先对账（`before.FactionId != c.FactionId` 时，旧组织 `Members.Remove(c.Id)` 且保存；新组织 `Members` 无则 `Add` 且保存），再 `store.SaveCharacter`。
  - `Faction`：先对账（`Members` 新增者：角色存在且 `FactionId != f.Id` → 从其旧组织 `Members` 移除并保存旧组织、`FactionId = f.Id`、保存角色；移除者：角色存在且 `FactionId == f.Id` → `FactionId = null`、保存角色），再 `store.SaveFaction`。逻辑与 `Desktop/Controls/RelationWriteThrough.cs` 的 `ReconcileCharacterAsync`/`ReconcileFactionAsync` 同义（那边在 UI 程序集，这里无法引用）。
  - 其余类型直接 `store.SavePlotline / SaveLocation / SaveScene / SaveItem / SaveRelationship / SaveChapter / SaveVolume / SaveNovel`。
- `IndexAsync(RetrievalKernel? kernel, Entity entity, CancellationToken ct)`：kernel 为 null 时直接返回；Character → `BuildCharacter`，Faction → `BuildFaction`，Location → `BuildLocation`，Plotline → `BuildPlotline`，Item → `BuildItem`，Chapter → `BuildChapterSummary`（正文没变，只同步标题/细纲）；其它类型不索引。统一 `await kernel.IndexDocumentAsync(doc, chunks, ct)`。

#### 3.4 注册

`src/Plugins/NovelTools/NovelToolsPlugin.cs` 的 4.2.1 段，`yield return new ManageNovelTool(_store);` 之后加：
```csharp
yield return new EditEntitiesTool(_store, _kernel);
```

### 4. `manage_*` 保持不变

`edit_entities` 是穷尽式更新入口；`manage_*` 继续负责 create/delete/get/list 与常用字段的单条 update（不复制缺口字段，避免三处维护同一字段）。工具 Description 已经指明编辑面板字段走 `edit_entities`。

## Critical files & anchors

- `src/Desktop/Controls/EntityFieldRegistry.cs` → `src/Core/Data/EntityFieldRegistry.cs`：唯一的字段白名单真源（含中文标签与枚举选项）。
- `src/Plugins/NovelTools/Tools/EditEntitiesTool.cs`（新）：工具 + `EntityFieldWriter`。
- `src/Core/Data/FileNovelStore.cs`：`SaveNovel` / `GetNovel` / `GetNovelSummary` / `GetAllNovels` / 辅助方法区（约 3556 行）。
- `src/Plugins/NovelTools/NovelToolsPlugin.cs`：`GetTools()` 的 4.2.1 段。
- `src/Plugins/NovelTools/Tests/EditEntitiesToolTests.cs`（新）：用现成 `FakeNovelStore`。

## Verification

前置：仓库根 `/Users/apple/Documents/Year_2026/Month_06/Mythology`。

1. `dotnet build src/Mythology.slnx` — 无错误（注册表换命名空间后 Desktop/测试都要过）。
2. `dotnet test src/Desktop/Tests/Desktop.Tests.csproj` — 既有 `EntityEditorFieldCoverageTests` / `EntityFieldRegistryTests` 全绿（证明下沉没破坏字段契约）。
3. `dotnet test src/Core/Tests/Core.Tests.csproj` — 新增 `FileNovelStore` 往返测试：临时目录 `SaveNovel(novel{Author,TargetWords,TargetAudience,EnhancementProfile,AutoDiagnose=false,AutoEnhance="full",CompletionStatus="completed"})` → 新建第二个 `FileNovelStore` 实例 `GetNovel/GetNovelSummary/GetAllNovels` → 八项值逐个相等。
4. `dotnet test src/Plugins/NovelTools/Tests/NovelTools.Tests.csproj` — 新增 `EditEntitiesToolTests`：
   - `BatchEdit_CoversMissingFields`：一次 `ExecuteAsync` 传 6 个条目（character `status/tags/abilities/notes`；relationship `since-chapter-id/until-chapter-id`；scene `item-ids`；chapter `summary/plotline-ids/character-ids/notes/volume-id/title/status`；novel `author/target-words/target-audience/enhancement-profile/auto-diagnose/auto-enhance/completion-status`；volume `notes`），断言 `Details.UpdatedCount == 6`，再逐个 `GetX` 断言字段值（中文枚举标签 → 英文枚举值）。
   - `UnknownField_FailsOnlyThatItem`：第 1 条含 `bogus-field`、第 2 条合法 → `FailedCount == 1 && UpdatedCount == 1`，第 1 条实体未被改动，错误文本包含可用字段列表。
   - `ChapterOrderAndContent_AreRejected_WithHint`：`order` → 错误含 `reorder_chapters`；`content` → 错误含 `write_chapter`；实体未变。
   - `NameResolution_And_MembershipReconcile`：用名称改角色 `faction-id` → 新旧组织的 `Members` 同步；用 `members` 改组织 → 角色 `FactionId` 同步。
   - `ReadOnlyField_IsRejected`：`char-count` → `FailedCount == 1`。
   - `PluginRegistersTool`：在 `NovelToolsPluginRegistrationTests` 断言工具名 `edit_entities` 出现在 `GetTools()`。
5. 手动冒烟（可选但推荐）：Desktop 启动后对 Agent 说「把所有角色的定位改成龙套」，观察角色 tab 刷新且落盘 `characters/*.yaml` 的 `role` 字段变化；TUI 走 `OnEntityChanged` 同一路径刷新。

## Assumptions

- 只补「修改」：create/delete 仍由 `manage_*` / `batch_create_*` 负责；不新增批量删除。
- 章节 `order` 与 `content` 不进 `edit_entities`（前者受 `ch{Order:D3}.md` 全局唯一文件名约束，后者已有 `write_chapter`/`edit_text` 且带索引/演化钩子），工具报错并指路。
- 地点邻接（`Location.AdjacentLocations`，本体关系窗里的 adjacent-to 距离）不在本次范围：它不是八个 tab 条目编辑窗的字段；如需改，后续单独加带结构化值的字段类型。
- TUI `EntityEditDialog` 的字段表不动（是 Desktop 注册表的子集；下沉注册表不改变其行为）。情节的「关联场景」在 TUI 独有，等价于从场景侧写 `plotline-ids`，已覆盖。
- `novel.yaml` 新增 4 个键属于本地数据文件格式的向后兼容扩展（旧文件缺键走默认值），不改线协议契约。
