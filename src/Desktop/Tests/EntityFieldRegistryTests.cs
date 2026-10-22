using Core.Data;
using Xunit;

namespace Desktop.Tests;

public sealed class EntityFieldRegistryTests
{
    private static FieldDef GenderField() =>
        EntityFieldRegistry
            .CharacterTabs.SelectMany(tab => tab.Fields)
            .Single(field => field.Prop == "Gender");

    /// <summary>
    /// 性别是跨仓线协议值（与 TUI EntityEditDialog 同约定）：存储必须是英文枚举，
    /// 不能落中文展示名（HarnessContextBuilder / DocumentBuilder 消费的就是存储值）。
    /// </summary>
    [Fact]
    public void CharacterGender_StoredValues_AreWireEnum()
    {
        var field = GenderField();
        Assert.Equal(["Male", "Female", "Other"], field.OptionValues);
        Assert.Equal(["男", "女", "其他"], field.Options);
    }

    /// <summary>英文枚举存储值 → 正确落在对应显示名上（编辑窗回填按 OptionValues 匹配）。</summary>
    [Theory]
    [InlineData("Male", "男")]
    [InlineData("Female", "女")]
    [InlineData("Other", "其他")]
    public void CharacterGender_StoredValue_SelectsMatchingOption(
        string storedValue,
        string expectedLabel
    )
    {
        var field = GenderField();
        var options = Assert.IsType<string[]>(field.Options);
        var optionValues = Assert.IsType<string[]>(field.OptionValues);

        var selectedIndex = Array.IndexOf(optionValues, storedValue);

        Assert.InRange(selectedIndex, 0, options.Length - 1);
        Assert.Equal(expectedLabel, options[selectedIndex]);
    }

    /// <summary>
    /// 旧数据兜底的前置条件：早期版本存中文展示名（"男"/"女"/"其他"），
    /// 回填侧按显示名再匹配一次（EntityEditWindow.PopulateFields 的同款回落）。
    /// </summary>
    [Theory]
    [InlineData("男", 0)]
    [InlineData("女", 1)]
    [InlineData("其他", 2)]
    public void CharacterGender_LegacyChineseStoredValue_FallsBackToDisplayOption(
        string legacyValue,
        int expectedIndex
    )
    {
        var field = GenderField();
        var options = Assert.IsType<string[]>(field.Options);

        Assert.Equal(expectedIndex, Array.IndexOf(options, legacyValue));
    }
}
