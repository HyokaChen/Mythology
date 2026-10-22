using Core.Shared;
using Xunit;

namespace Core.Tests;

public class StoreHelperTests
{
    [Fact]
    public void GetString_ExistingKey_ReturnsValue()
    {
        var dict = new Dictionary<string, object?> { ["name"] = "Alice" };
        var result = StoreHelper.GetString(dict, "name");
        Assert.Equal("Alice", result);
    }

    [Fact]
    public void GetString_MissingKey_ReturnsNull()
    {
        var dict = new Dictionary<string, object?>();
        var result = StoreHelper.GetString(dict, "missing");
        Assert.Null(result);
    }

    [Fact]
    public void GetString_NullValue_ReturnsNull()
    {
        var dict = new Dictionary<string, object?> { ["key"] = null };
        var result = StoreHelper.GetString(dict, "key");
        Assert.Null(result);
    }

    [Fact]
    public void GetString_IntValue_ReturnsString()
    {
        var dict = new Dictionary<string, object?> { ["count"] = 42 };
        var result = StoreHelper.GetString(dict, "count");
        Assert.Equal("42", result);
    }

    [Fact]
    public void ParseDateTime_ValidString_ReturnsParsedDate()
    {
        var result = StoreHelper.ParseDateTime("2024-01-15T10:30:00");
        Assert.Equal(new DateTime(2024, 1, 15, 10, 30, 0), result);
    }

    [Fact]
    public void ParseDateTime_Null_ReturnsUtcNow()
    {
        var result = StoreHelper.ParseDateTime(null);
        Assert.Equal(DateTime.UtcNow, result, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ParseDateTime_InvalidString_ReturnsUtcNow()
    {
        var result = StoreHelper.ParseDateTime("not-a-date");
        Assert.Equal(DateTime.UtcNow, result, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ParseDateTime_EmptyString_ReturnsUtcNow()
    {
        var result = StoreHelper.ParseDateTime("");
        Assert.Equal(DateTime.UtcNow, result, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void GetDateTime_ExistingValidKey_ReturnsParsedDate()
    {
        var dict = new Dictionary<string, object?> { ["created"] = "2024-06-01T12:00:00" };
        var result = StoreHelper.GetDateTime(dict, "created");
        Assert.Equal(new DateTime(2024, 6, 1, 12, 0, 0), result);
    }

    [Fact]
    public void GetDateTime_MissingKey_ReturnsUtcNow()
    {
        var dict = new Dictionary<string, object?>();
        var result = StoreHelper.GetDateTime(dict, "missing");
        Assert.Equal(DateTime.UtcNow, result, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void SanitizeFileName_NoInvalidChars_ReturnsSame()
    {
        var result = StoreHelper.SanitizeFileName("normal-file.txt");
        Assert.Equal("normal-file.txt", result);
    }

    [Fact]
    public void SanitizeFileName_PathSeparator_ReplacedWithUnderscore()
    {
        var input = "file/name.txt";
        var result = StoreHelper.SanitizeFileName(input);
        Assert.Equal("file_name.txt", result);
    }

    [Fact]
    public void SanitizeFileName_AllOsInvalidChars_ReplacedWithUnderscores()
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var input = new string(invalidChars);
        var result = StoreHelper.SanitizeFileName(input);
        Assert.All(result, c => Assert.Equal('_', c));
        Assert.Equal(invalidChars.Length, result.Length);
    }

    [Fact]
    public void SanitizeFileName_AlphanumericValidChars_Unchanged()
    {
        var input = "abcdefghijklmnopqrstuvwxyz0123456789-._";
        var result = StoreHelper.SanitizeFileName(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void SanitizeFileName_EmptyString_ReturnsEmpty()
    {
        var result = StoreHelper.SanitizeFileName("");
        Assert.Equal("", result);
    }

    [Fact]
    public void SanitizeFileName_SpacesOnly_ReturnsSpaces()
    {
        var result = StoreHelper.SanitizeFileName("   ");
        Assert.Equal("   ", result);
    }
}
