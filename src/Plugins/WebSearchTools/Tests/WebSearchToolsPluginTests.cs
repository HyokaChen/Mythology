using Plugins.WebSearchTools;
using Xunit;

namespace WebSearchTools.Tests;

public class WebSearchToolsPluginTests
{
    [Fact]
    public void GetTools_ReturnsTools()
    {
        var plugin = new WebSearchToolsPlugin();
        var tools = plugin.GetTools().ToList();
        Assert.NotEmpty(tools);
    }
}
