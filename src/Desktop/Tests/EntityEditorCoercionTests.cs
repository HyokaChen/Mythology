using System.Reflection;
using Core.Data;
using Desktop.Controls;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 编辑窗文本 → 属性值的空值契约：非空 string 属性把空输入保存为空串，
/// 只有可空引用类型（如 Chapter.VolumeId）才允许转成 null。
/// </summary>
public sealed class EntityEditorCoercionTests
{
    [Fact]
    public void EmptyText_OnNonNullableStringProperty_StaysEmpty()
    {
        var prop = typeof(Chapter).GetProperty(nameof(Chapter.Notes))!;

        Assert.Equal("", EntityEditWindow.CoerceValue(prop, ""));
    }

    [Fact]
    public void EmptyText_OnNullableStringProperty_BecomesNull()
    {
        var prop = typeof(Chapter).GetProperty(nameof(Chapter.VolumeId))!;

        Assert.Null(EntityEditWindow.CoerceValue(prop, ""));
    }

    [Fact]
    public async Task EmptyNotes_AfterCoercion_RoundTripsThroughChapterSave()
    {
        var root = Path.Combine(Path.GetTempPath(), $"entity-coercion-{Guid.NewGuid():N}");
        try
        {
            var store = new FileNovelStore(
                Path.Combine(root, "novels"),
                Path.Combine(root, "mythology")
            );
            await store.SaveNovel(new Novel { Id = "n1", Title = "测试" });
            await store.SaveChapter(
                new Chapter
                {
                    Id = "c1",
                    NovelId = "n1",
                    Order = 1,
                    Title = "第一章",
                    Notes = "",
                }
            );

            var chapter = await store.GetChapter("n1", "c1");
            Assert.NotNull(chapter);
            var prop = typeof(Chapter).GetProperty(nameof(Chapter.Notes))!;
            chapter!.Notes = (string)EntityEditWindow.CoerceValue(prop, "")!;

            await store.SaveChapter(chapter);

            var reloaded = await store.GetChapter("n1", "c1");
            Assert.Equal("", reloaded!.Notes);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不影响测试结论
            }
        }
    }
}
