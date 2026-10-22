using Plugins.SemanticRetrievalTool;
using Xunit;

namespace SemanticRetrievalTool.Tests;

public class SemanticRetrievalToolPluginTests
{
    [Fact]
    public void GetTools_ReturnsTools()
    {
        var plugin = new SemanticRetrievalToolPlugin();
        var tools = plugin.GetTools().ToList();
        Assert.NotEmpty(tools);
    }
}
