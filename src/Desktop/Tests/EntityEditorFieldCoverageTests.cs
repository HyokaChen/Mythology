using Core.Data;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 设定面板编辑窗的字段契约：每个 <see cref="FieldDef.Prop"/> 必须真实落在 Core 模型上，
/// 选项数组必须成对等长且能解析回目标枚举——防止再次出现「控件存在但回填/保存无效」的空弹窗。
/// </summary>
public sealed class EntityEditorFieldCoverageTests
{
    private static readonly Dictionary<EntityType, Type> ModelTypes = new()
    {
        [EntityType.Character] = typeof(Character),
        [EntityType.Faction] = typeof(Faction),
        [EntityType.Location] = typeof(Core.Data.Location),
        [EntityType.Plotline] = typeof(Plotline),
        [EntityType.Scene] = typeof(SceneDefinition),
        [EntityType.Item] = typeof(Item),
        [EntityType.Relationship] = typeof(Relationship),
        [EntityType.Chapter] = typeof(Chapter),
        [EntityType.Volume] = typeof(Volume),
        [EntityType.Novel] = typeof(Novel),
        [EntityType.Note] = typeof(AuthorNote),
    };

    public static TheoryData<EntityType> EditableTypes()
    {
        var data = new TheoryData<EntityType>();
        foreach (var type in ModelTypes.Keys)
            data.Add(type);
        return data;
    }

    [Theory]
    [MemberData(nameof(EditableTypes))]
    public void EveryFieldProp_ExistsOnModel(EntityType type)
    {
        var model = ModelTypes[type];

        foreach (var def in EntityFieldRegistry.GetTabs(type).SelectMany(t => t.Fields))
        {
            // 只读展示字段（章节号 / 字数 / 更新时间）不在模型上：由编辑窗按章预计算回填
            // （EntityEditWindow.PopulateReadOnlyFieldsAsync），也不参与保存。
            if (def.Type is FieldType.ReadOnly)
                continue;

            Assert.True(
                model.GetProperty(def.Prop) is not null,
                $"{type}.{def.Prop} 不是 {model.Name} 的属性（字段名拼写或模型不符）"
            );
        }
    }

    [Theory]
    [MemberData(nameof(EditableTypes))]
    public void SelectOptions_ArePairedAndAligned(EntityType type)
    {
        foreach (var def in EntityFieldRegistry.GetTabs(type).SelectMany(t => t.Fields))
        {
            var hasOptions = def.Options is not null;
            var hasValues = def.OptionValues is not null;

            Assert.True(hasOptions == hasValues, $"{type}.{def.Prop} 的选项显示名与值必须同时给出");
            if (!hasOptions)
                continue;

            Assert.Equal(def.Options!.Length, def.OptionValues!.Length);
            Assert.All(def.Options, o => Assert.False(string.IsNullOrEmpty(o)));
        }
    }

    [Theory]
    [MemberData(nameof(EditableTypes))]
    public void SelectOptionValues_ParseForEnumTargets(EntityType type)
    {
        var model = ModelTypes[type];

        foreach (var def in EntityFieldRegistry.GetTabs(type).SelectMany(t => t.Fields))
        {
            if (def.OptionValues is null)
                continue;
            var prop = model.GetProperty(def.Prop);
            if (prop is null || !prop.PropertyType.IsEnum)
                continue;

            foreach (var value in def.OptionValues)
                Assert.True(
                    Enum.TryParse(prop.PropertyType, value, ignoreCase: true, out _),
                    $"{type}.{def.Prop} 的选项值「{value}」无法解析为 {prop.PropertyType.Name}"
                );
        }
    }

    [Theory]
    [MemberData(nameof(EditableTypes))]
    public void ReferenceFields_HaveCandidateSource(EntityType type)
    {
        foreach (var def in EntityFieldRegistry.GetTabs(type).SelectMany(t => t.Fields))
        {
            switch (def.Type)
            {
                case FieldType.Select when def.OptionValues is null:
                    Assert.True(
                        EntityFieldRegistry.SelectSource(def.Prop) is not null,
                        $"{type}.{def.Prop} 是 ID 引用下拉但没有候选来源"
                    );
                    break;
                case FieldType.MultiSelect:
                    Assert.True(
                        EntityFieldRegistry.MultiSelectSource(def.Prop) is not null,
                        $"{type}.{def.Prop} 是多选但没有候选来源"
                    );
                    break;
            }
        }
    }

    [Theory]
    [MemberData(nameof(EditableTypes))]
    public void EveryType_HasAtLeastOneNamedTab(EntityType type)
    {
        var tabs = EntityFieldRegistry.GetTabs(type);

        Assert.NotEmpty(tabs);
        Assert.All(tabs, t => Assert.NotEmpty(t.Fields));
        Assert.All(tabs, t => Assert.False(string.IsNullOrWhiteSpace(t.Name)));
    }

    [Fact]
    public void UnsupportedType_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EntityFieldRegistry.GetTabs(EntityType.TimelineEvent)
        );
    }
}
