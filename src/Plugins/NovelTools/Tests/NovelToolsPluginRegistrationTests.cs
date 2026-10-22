using Core.Data;
using Core.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace NovelTools.Tests;

/// <summary>
/// 插件工具注册冒烟：GetTools() 枚举全部工具必须无异常（构造即生成 JSON Schema，
/// 含空参数 record，如 knowledge_categories），且工具名唯一（新增云端工具后防重名）。
/// </summary>
public sealed class NovelToolsPluginRegistrationTests
{
    [Fact]
    public void GetTools_AllConstructible_NamesUnique()
    {
        var services = new ServiceCollection();
        services.AddSingleton<INovelStore>(new FakeNovelStore());
        using var provider = services.BuildServiceProvider();

        var plugin = new Plugins.NovelTools.NovelToolsPlugin();
        plugin.Initialize(provider);

        var tools = plugin.GetTools().ToList();
        Assert.NotEmpty(tools);
        var names = tools.Select(tool => tool.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());

        // 云端能力工具已注册（检索 ×4 + 爬取 ×3 + 素材库 ×4）
        // 设定面板批量编辑工具（跨类型字段修改）也必须注册
        Assert.Contains("edit_entities", names);
        foreach (
            var expected in new[]
            {
                "web_scrape",
                "web_search",
                "web_map",
                "web_discover",
                "web_crawl",
                "web_crawl_status",
                "web_crawl_cancel",
                "knowledge_search",
                "knowledge_document",
                "knowledge_browse",
                "knowledge_categories",
            }
        )
        {
            Assert.Contains(expected, names);
        }

        // 空参数工具（无属性 record）也必须能生成参数 schema；Parameters=null 会让
        // ConversationLoop 静默丢弃该工具（工具对模型不存在）。
        var categoryTool = tools.Single(tool => tool.Name == "knowledge_categories");
        Assert.NotNull(categoryTool.Parameters);

        var hidden = tools
            .Where(tool => tool.Parameters is null)
            .Select(tool => tool.Name)
            .ToList();
        Assert.Empty(hidden);
    }
}
