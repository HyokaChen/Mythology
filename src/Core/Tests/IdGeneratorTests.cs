using Core.Infrastructure;
using Xunit;

namespace Core.Tests;

public class IdGeneratorTests
{
    [Fact]
    public void NewId_DefaultLength_Returns12Chars()
    {
        var id = IdGenerator.NewId();
        Assert.Equal(12, id.Length);
    }

    [Fact]
    public void NewId_Default_ReturnsHexString()
    {
        var id = IdGenerator.NewId();
        Assert.Matches("^[0-9a-f]{12}$", id);
    }

    [Fact]
    public void NewId_CustomLength_ReturnsCorrectLength()
    {
        var id = IdGenerator.NewId(8);
        Assert.Equal(8, id.Length);
    }

    [Fact]
    public void NewId_MaxLength32_Returns32Chars()
    {
        var id = IdGenerator.NewId(32);
        Assert.Equal(32, id.Length);
    }

    [Fact]
    public void NewId_ConsecutiveCalls_ReturnsDifferentIds()
    {
        var id1 = IdGenerator.NewId();
        var id2 = IdGenerator.NewId();
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void NewId_MultipleCalls_AllValidHex()
    {
        for (var i = 0; i < 100; i++)
        {
            var id = IdGenerator.NewId();
            Assert.Matches("^[0-9a-f]{12}$", id);
        }
    }

    [Fact]
    public void NewId_Length1_Returns1Char()
    {
        var id = IdGenerator.NewId(1);
        Assert.Equal(1, id.Length);
    }
}
