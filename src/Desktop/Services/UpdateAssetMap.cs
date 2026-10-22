using System.Globalization;
using System.Runtime.InteropServices;

namespace Desktop.Services;

/// <summary>
/// 运行时 RID 与 GitHub Release 资产名的映射。
/// 映射表必须与 installer/build-*.{sh,ps1} 的产物命名保持一致：
///   win-x64   → Mythology-Desktop-&lt;版本&gt;-win-x64-setup.exe（Inno Setup）
///   linux-x64 → Mythology-Desktop-&lt;版本&gt;-linux-x64.deb
///   osx-arm64 → Mythology-Desktop-&lt;版本&gt;-osx-arm64.dmg
/// 表外 RID（win10-x64、linux-arm64 等）返回 null，调用方据此静默跳过、不提示。
/// 校验和资产名 = 安装包名 + ".sha256"（发布对每个产物附同名校验和，不单独出方法）。
/// </summary>
public static class UpdateAssetMap
{
    /// <summary>RID → 资产名格式模板（{0} = 归一化版本号）。</summary>
    private static readonly IReadOnlyDictionary<string, string> Templates = new Dictionary<
        string,
        string
    >
    {
        ["win-x64"] = "Mythology-Desktop-{0}-win-x64-setup.exe",
        ["linux-x64"] = "Mythology-Desktop-{0}-linux-x64.deb",
        ["osx-arm64"] = "Mythology-Desktop-{0}-osx-arm64.dmg",
    };

    /// <summary>按 RID 与版本生成安装包资产名；未知 RID / 空参数返回 null。</summary>
    public static string? InstallerNameFor(string rid, string version)
    {
        if (string.IsNullOrEmpty(rid) || string.IsNullOrEmpty(version))
            return null;

        return Templates.TryGetValue(rid, out var template)
            ? string.Format(
                CultureInfo.InvariantCulture,
                template,
                UpdateVersion.Normalize(version)
            )
            : null;
    }

    /// <summary>当前进程 RID（仅当其映射在支持表内时返回，否则 null）。</summary>
    public static string? CurrentRid()
    {
        var rid = RuntimeInformation.RuntimeIdentifier;
        return Templates.ContainsKey(rid) ? rid : null;
    }
}
