namespace ClientPlatform;

/// <summary>
/// 云端地址唯一收口（全仓唯一默认值来源，Desktop / TUI / Host 共用）。
///
/// <para><b>默认值即生产地址</b>：客户端应用的生产云端地址嵌入在二进制中，
/// 开发/测试/部署环境如需切换，通过环境变量 <c>MYTHOLOGY_CLOUD_URL</c> 覆盖。
/// 不通过 appsettings 或配置文件——客户端不需要这个能力。</para>
///
/// <para>前后端约定：后端 Cloud:WebPublicUrl 须指向返回 <c>"/callback"</c> 的前端地址，
/// 此处 WebOrigin 同步约定（OAuth 前端中转使用）。</para>
/// </summary>
public static class CloudEndpoints
{
    /// <summary>生产云端地址（嵌入客户端二进制，开发/测试可通过 MYTHOLOGY_CLOUD_URL 环境变量覆盖）。</summary>
    public const string DefaultCloudBaseUrl = "https://api.mythology.work";

    /// <summary>生产前端站点地址（OAuth 后端校验 redirect_uri 的 origin，与后端 Cloud:WebPublicUrl 约定一致）。</summary>
    private const string DefaultWebOrigin = "https://app.mythology.work";

    /// <summary>
    /// 解析云端基地址：MYTHOLOGY_CLOUD_URL 环境变量优先（开发/测试/自定义部署），
    /// 未配置时返回生产默认值。
    /// </summary>
    public static string ResolveCloudBaseUrl() =>
        Environment.GetEnvironmentVariable("MYTHOLOGY_CLOUD_URL") ?? DefaultCloudBaseUrl;

    /// <summary>
    /// 解析前端站点 origin：MYTHOLOGY_WEB_URL 环境变量优先（覆盖默认值），
    /// 未配置时返回生产默认值。OAuth 前端中转回调地址为 <c>{Origin}/callback</c>。
    /// </summary>
    public static string ResolveWebOrigin() =>
        Environment.GetEnvironmentVariable("MYTHOLOGY_WEB_URL") ?? DefaultWebOrigin;
}
