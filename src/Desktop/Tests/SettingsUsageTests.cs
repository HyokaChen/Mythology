using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClientPlatform;
using Desktop.ViewModels;
using Desktop.Views;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 设置页「本月积分」投影回归锁：余额/已用/在途/百分比与按能力明细的排序、
/// 空态标记，以及未知能力 ID 的可见降级。
/// </summary>
public sealed class SettingsUsageTests
{
    [Fact]
    public void ApplyUsage_ProjectsTotalsAndSortedBreakdown()
    {
        var vm = new SettingsViewModel();
        var snapshot = new UsageSnapshot(
            "2026-11",
            "free",
            IncludedUnits: 5_000,
            UsedUnits: 1_200,
            ReservedUnits: 50,
            RemainingUnits: 3_750,
            Breakdown: new Dictionary<string, int>
            {
                ["retrieval.crawl"] = 34,
                ["fiction.story.check"] = 1_166,
            }
        );

        vm.ApplyUsage(snapshot);

        Assert.Equal(3_750.ToString("N0"), vm.UsageRemaining);
        Assert.Equal(5_000.ToString("N0"), vm.UsageIncluded);
        Assert.Equal(1_200.ToString("N0"), vm.UsageUsed);
        Assert.Equal(50.ToString("N0"), vm.UsageReserved);
        Assert.Equal("2026-11", vm.UsagePeriod);
        Assert.Equal(25d, vm.UsageUsedPercent, 3);
        Assert.True(vm.HasUsageSnapshot);
        Assert.True(vm.HasUsageBreakdown);
        Assert.False(vm.IsUsageEmptyHintVisible);

        Assert.Equal(
            ["正文审校", "资料抓取"],
            vm.UsageBreakdown.Select(row => row.Label).ToArray()
        );
        Assert.Equal(
            [1_166.ToString("N0"), 34.ToString("N0")],
            vm.UsageBreakdown.Select(row => row.UnitsText).ToArray()
        );
    }

    [Fact]
    public void ApplyUsage_NoConsumption_ShowsEmptyHint()
    {
        var vm = new SettingsViewModel();
        vm.ApplyUsage(
            new UsageSnapshot("2026-11", "free", 5_000, 0, 0, 5_000, new Dictionary<string, int>())
        );

        Assert.True(vm.HasUsageSnapshot);
        Assert.False(vm.HasUsageBreakdown);
        Assert.True(vm.IsUsageEmptyHintVisible);
        Assert.Equal(0d, vm.UsageUsedPercent);
    }

    [Fact]
    public void ApplyUsage_UnknownCapability_KeepsRawIdVisible()
    {
        var vm = new SettingsViewModel();
        vm.ApplyUsage(
            new UsageSnapshot(
                "2026-11",
                "free",
                5_000,
                3,
                0,
                4_997,
                new Dictionary<string, int> { ["custom.future.capability"] = 3 }
            )
        );

        var row = Assert.Single(vm.UsageBreakdown);
        Assert.Equal("custom.future.capability", row.Label);
        Assert.Equal("custom.future.capability", row.Capability);
    }

    /// <summary>真实 SettingsWindow + 账号 Tab：积分卡必须渲染出余额、计量条和明细行。</summary>
    [Fact]
    public void UsageCard_RendersInAccountTab()
    {
        var vm = new SettingsViewModel();
        vm.IsCloudLoggedIn = true;
        vm.IsUsageVisible = true;
        vm.ApplyUsage(
            new UsageSnapshot(
                "2026-11",
                "free",
                5_000,
                1_200,
                50,
                3_750,
                new Dictionary<string, int>
                {
                    ["fiction.story.check"] = 1_166,
                    ["retrieval.crawl"] = 34,
                }
            )
        );

        HeadlessUiHost.Instance.Run(() =>
        {
            var window = new SettingsWindow(initialTab: 6)
            {
                DataContext = vm,
                Width = 700,
                Height = 820,
            };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                var view = Assert.IsType<SettingsView>(window.Content);
                var card = view.FindControl<Border>("UsageCard");
                Assert.NotNull(card);
                Assert.True(card!.IsVisible);
                Assert.True(card.Bounds.Width > 400, $"积分卡宽度 {card.Bounds.Width:F1}");
                Assert.True(card.Bounds.Height > 200, $"积分卡高度 {card.Bounds.Height:F1}");

                var texts = card.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Select(t => t.Text ?? "")
                    .ToList();
                Assert.Contains("本月积分", texts);
                Assert.Contains("正文审校", texts);
                Assert.Contains("资料抓取", texts);

                var bar = Assert.Single(card.GetVisualDescendants().OfType<ProgressBar>());
                Assert.Equal(25d, bar.Value, 3);

                var breakdown = card.GetVisualDescendants()
                    .OfType<ItemsControl>()
                    .Single(c => ReferenceEquals(c.ItemsSource, vm.UsageBreakdown));
                Assert.Equal(2, breakdown.Items.Count);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
