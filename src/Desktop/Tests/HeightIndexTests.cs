using Desktop.Controls;
using Xunit;

namespace Desktop.Tests;

public sealed class HeightIndexTests
{
    private sealed class Item(string name)
    {
        public string Name { get; } = name;
    }

    [Fact]
    public void Rebuild_OffsetAndIndexAtOffset_AreConsistent()
    {
        var items = Enumerable.Range(0, 5).Select(i => new Item($"i{i}")).ToArray();
        var index = new HeightIndex<Item>();
        index.Rebuild(items, _ => 20);

        Assert.Equal(100, index.Total);
        Assert.Equal(0, index.IndexAtOffset(0));
        Assert.Equal(0, index.IndexAtOffset(20));
        Assert.Equal(1, index.IndexAtOffset(20.1));
        Assert.Equal(4, index.IndexAtOffset(99));
        Assert.Equal(4, index.IndexAtOffset(100));
        Assert.Equal(40, index.OffsetOf(2));
        Assert.Equal(40, index.Range(0, 2));
    }

    [Fact]
    public void Update_ChangesPrefixAndIndex()
    {
        var a = new Item("a");
        var b = new Item("b");
        var index = new HeightIndex<Item>();
        index.Rebuild([a, b], _ => 10);

        Assert.True(index.Update(a, 50));
        Assert.Equal(60, index.Total);
        Assert.Equal(0, index.IndexAtOffset(50));
        Assert.Equal(1, index.IndexAtOffset(50.1));
        Assert.Equal(50, index.OffsetOf(1));

        Assert.False(index.Update(new Item("missing"), 1));
    }
}
