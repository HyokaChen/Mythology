using Plugins.WritingTools;
using Xunit;

namespace WritingTools.Tests;

public sealed class EnhancementSnapshotTests
{
    [Fact]
    public void Save_AndRollback_RestoresContent()
    {
        var snapshot = new EnhancementSnapshot();
        snapshot.Save("novel1", "ch1", "原始内容");

        var restored = snapshot.Rollback("novel1", "ch1");
        Assert.Equal("原始内容", restored);
    }

    [Fact]
    public void Save_TryAdd_FirstVersionPreserved()
    {
        var snapshot = new EnhancementSnapshot();
        snapshot.Save("novel1", "ch1", "第一版");
        snapshot.Save("novel1", "ch1", "第二版");

        var restored = snapshot.Rollback("novel1", "ch1");
        Assert.Equal("第一版", restored);
    }

    [Fact]
    public void Rollback_NoSnapshot_ReturnsNull()
    {
        var snapshot = new EnhancementSnapshot();
        var result = snapshot.Rollback("novel1", "ch1");
        Assert.Null(result);
    }
}
