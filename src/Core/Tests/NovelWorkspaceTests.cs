using Core.Data;
using Core.Shared;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Core.Tests;

/// <summary>
/// NovelWorkspace 测试：项目目录清单的登记/切换/持久化，与跨入口共用的根目录解析优先级。
/// 全部使用临时目录，不触碰真实 ~/.mythology。
/// </summary>
public sealed class NovelWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ws-{Guid.NewGuid():N}");

    public NovelWorkspaceTests() => Directory.CreateDirectory(_root);

    private string MakeDir(string name, bool withNovel = false)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        if (withNovel)
        {
            var novelDir = Path.Combine(path, "book-" + name);
            Directory.CreateDirectory(novelDir);
            File.WriteAllText(
                Path.Combine(novelDir, MythologyPaths.NovelMetaFileName),
                $"id: id-{name}\ntitle: 《{name}》\n"
            );
        }
        return path;
    }

    private string MythologyDir()
    {
        var dir = Path.Combine(_root, ".mythology");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Single_ExposesOnlyThatRoot_And_HasNoPersistence()
    {
        var path = MakeDir("solo");
        var ws = NovelWorkspace.Single(path);

        Assert.Equal(path, ws.ActiveRoot);
        Assert.Equal([path], ws.Roots);
        Assert.True(ws.HasSpecifiedRoot); // Single = 调用方显式指定
    }

    [Fact]
    public void Add_RejectsMissingDirectory_And_FiresChangedOnNewDirectory()
    {
        var ws = NovelWorkspace.Single(MakeDir("a"));
        var changes = 0;
        ws.Changed += () => changes++;

        Assert.False(ws.Add(Path.Combine(_root, "not-here")));
        Assert.Equal(0, changes);

        var b = MakeDir("b");
        Assert.True(ws.Add(b));
        Assert.Equal(1, changes);
        Assert.Contains(b, ws.Roots);

        Assert.False(ws.Add(b)); // 去重
        Assert.Equal(1, changes);
    }

    [Fact]
    public void SetActive_RegistersAndSwitchesWriteRoot()
    {
        var ws = NovelWorkspace.Single(MakeDir("a"));
        var b = MakeDir("b");

        ws.SetActive(b);

        Assert.Equal(b, ws.ActiveRoot);
        Assert.Equal([b, ws.Roots[1]], [.. ws.Roots]); // 两个目录都在读取范围
        Assert.Contains(Path.Combine(_root, "a"), ws.Roots);
    }

    [Fact]
    public void Remove_OnlyDropsFromList_And_ReelectsActive()
    {
        var a = MakeDir("a");
        var b = MakeDir("b");
        var ws = NovelWorkspace.Single(a);
        ws.Add(b);
        ws.SetActive(b);

        Assert.True(ws.Remove(b));

        Assert.Equal(a, ws.ActiveRoot);
        Assert.DoesNotContain(b, ws.Roots);
        Assert.True(Directory.Exists(b)); // 磁盘目录不动
    }

    [Fact]
    public void WorkspaceFile_RoundTripsAcrossProcessRestart()
    {
        var myth = MythologyDir();
        var a = MakeDir("a");
        var b = MakeDir("b");

        new NovelWorkspace(myth, [a], a).SetActive(b);

        var reopened = new NovelWorkspace(myth);
        Assert.Equal(b, reopened.ActiveRoot);
        Assert.Equal(2, reopened.Roots.Count);
        Assert.Contains(a, reopened.Roots);
    }

    [Fact]
    public void CorruptWorkspaceFile_FallsBackToSeeds()
    {
        var myth = MythologyDir();
        File.WriteAllText(Path.Combine(myth, "workspaces.json"), "{ not json");
        var a = MakeDir("a");

        var ws = new NovelWorkspace(myth, [a], a);

        Assert.Equal(a, ws.ActiveRoot);
    }

    [Fact]
    public void DeadPaths_AreDroppedOnLoad()
    {
        var myth = MythologyDir();
        var gone = MakeDir("gone");
        new NovelWorkspace(myth, [gone], gone);
        Directory.Delete(gone);

        var reopened = new NovelWorkspace(myth);

        Assert.Empty(reopened.Roots.Where(p => p == gone));
    }

    [Fact]
    public void FindRootForNovel_LocatesOwningDirectory()
    {
        var a = MakeDir("a", withNovel: true);
        var b = MakeDir("b", withNovel: true);
        var ws = NovelWorkspace.Single(a);
        ws.Add(b);

        Assert.Equal(b, ws.FindRootForNovel("id-b"));
        Assert.Equal(a, ws.FindRootForNovel("id-a"));
        Assert.Null(ws.FindRootForNovel("id-missing"));
    }

    // ── 同步守护语义：根是否「被指定过」──

    [Fact]
    public void ResolveNovelsRoot_ExplicitSourcesAreMarkedSpecified()
    {
        var resolved = NovelWorkspace.ResolveNovelsRoot(ConfigWithNovelsPath(MakeDir("marked")));
        Assert.True(resolved.Specified); // 配置指定
        Assert.True(NovelWorkspace.ResolveNovelsRoot(null, MakeDir("explicit")).Specified); // --novels 指定
    }

    [Fact]
    public void IsSpecifiedRoot_EmptyGuessIsNotSpecified_ButDirWithBooksIs()
    {
        // Desktop 打包后猜到空目录（~/.mythology/novels 空壳）→ 未指定，同步必须拒绝；
        // TUI 语义：cd 到仓库根，cwd/novels 里有书 → 视为已指定
        Assert.False(NovelWorkspace.IsSpecifiedRoot(MakeDir("guessed-empty")));
        Assert.True(NovelWorkspace.IsSpecifiedRoot(MakeDir("guessed-books", withNovel: true)));
    }

    // ── 解析优先级（TUI / Host / Desktop 共用）──

    [Fact]
    public void ResolveNovelsRoot_ExplicitPathWinsEvenIfMissing()
    {
        var missing = Path.Combine(_root, "chosen", "novels");

        Assert.Equal(
            Path.GetFullPath(missing),
            NovelWorkspace.ResolveNovelsRoot(ConfigWithNovelsPath(MakeDir("cfg")), missing).Path
        );
    }

    [Fact]
    public void ResolveNovelsRoot_EnvBeatsConfig()
    {
        var envDir = MakeDir("env");
        Environment.SetEnvironmentVariable(NovelWorkspace.EnvVar, envDir);
        try
        {
            Assert.Equal(
                Path.GetFullPath(envDir),
                NovelWorkspace.ResolveNovelsRoot(ConfigWithNovelsPath(MakeDir("cfg"))).Path
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable(NovelWorkspace.EnvVar, null);
        }
    }

    [Fact]
    public void ResolveNovelsRoot_ConfigBeatsCandidates()
    {
        var cfg = ConfigWithNovelsPath(MakeDir("cfg"));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(_root, "cfg")),
            NovelWorkspace.ResolveNovelsRoot(cfg).Path
        );
    }

    [Fact]
    public void SelectRoot_EmptyLeftoverDirectoriesNeverWin()
    {
        // 真实形态：cwd/novels 与 src/novels 是空壳，仓库根 novels/ 才有书
        var emptyShell = MakeDir("empty-shell");
        var real = MakeDir("real", withNovel: true);
        var missing = Path.Combine(_root, "not-here");

        Assert.Equal(real, NovelWorkspace.SelectRoot([emptyShell, missing, real]));
        Assert.Equal(emptyShell, NovelWorkspace.SelectRoot([emptyShell, missing])); // 全空 → 第一个存在
        Assert.Equal(missing, NovelWorkspace.SelectRoot([missing])); // 全不存在 → 第一个候选（供自动创建）
        Assert.Null(NovelWorkspace.SelectRoot([]));
    }

    [Fact]
    public void DiscoverCandidateRoots_ContainsCwdNovelsAndFallback()
    {
        var candidates = NovelWorkspace.DiscoverCandidateRoots();
        Assert.Contains(
            Path.Combine(Directory.GetCurrentDirectory(), MythologyPaths.NovelsSubDir),
            candidates
        );
        Assert.Contains(candidates, c => c.EndsWith(MythologyPaths.NovelsSubDir));
    }

    /// <summary>最小 IConfiguration 桩：只回答 Novels:Path（Core 仅引用 Configuration.Abstractions）。</summary>
    private sealed class StubConfig(string? novelsPath) : IConfiguration
    {
        public string? this[string key]
        {
            get => key == ConfigurationKeys.NovelsPath ? novelsPath : null;
            set { }
        }

        public IEnumerable<IConfigurationSection> GetChildren() => [];

        public IConfigurationSection GetSection(string key) => new StubSection(key, this[key]);

        public Microsoft.Extensions.Primitives.IChangeToken GetReloadToken() =>
            new Microsoft.Extensions.Primitives.CancellationChangeToken(
                System.Threading.CancellationToken.None
            );
    }

    private sealed class StubSection(string key, string? value) : IConfigurationSection
    {
        public string? this[string index]
        {
            get => null;
            set { }
        }

        public string Key => key;
        public string? Path => key;
        public string? Value
        {
            get => value;
            set { }
        }

        public IEnumerable<IConfigurationSection> GetChildren() => [];

        public IConfigurationSection GetSection(string key) => new StubSection(key, null);

        public Microsoft.Extensions.Primitives.IChangeToken GetReloadToken() =>
            new Microsoft.Extensions.Primitives.CancellationChangeToken(
                System.Threading.CancellationToken.None
            );
    }

    private static IConfiguration ConfigWithNovelsPath(string path) => new StubConfig(path);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (Exception)
        {
            // 临时目录清理失败不影响测试结论
        }
    }
}
