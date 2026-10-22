using ClientPlatform;
using Xunit;

namespace ClientPlatform.Tests;

/// <summary>
/// CloudEndpoints 共享收口测试：
/// 无环境变量时返回 DefaultCloudBaseUrl（生产地址）；设 MYTHOLOGY_CLOUD_URL 时环境变量覆盖（开发/测试）。
/// 客户端生产 URL 嵌死在二进制中，不通过配置文件覆盖。
/// </summary>
public sealed class CloudEndpointsTests
{
    private const string EnvVar = "MYTHOLOGY_CLOUD_URL";

    [Fact]
    public void ResolveCloudBaseUrl_WithoutEnvVar_ReturnsDocumentedDefault()
    {
        var previous = Environment.GetEnvironmentVariable(EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(EnvVar, null);

            Assert.Equal(CloudEndpoints.DefaultCloudBaseUrl, CloudEndpoints.ResolveCloudBaseUrl());
            Assert.Equal("https://api.mythology.work", CloudEndpoints.ResolveCloudBaseUrl());
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVar, previous);
        }
    }

    [Fact]
    public void ResolveCloudBaseUrl_WithEnvVar_EnvironmentWins()
    {
        var previous = Environment.GetEnvironmentVariable(EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(EnvVar, "https://cloud.mythology.example");

            Assert.Equal("https://cloud.mythology.example", CloudEndpoints.ResolveCloudBaseUrl());
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVar, previous);
        }
    }

    [Fact]
    public void ResolveCloudBaseUrl_WithTrailingSlashEnvVar_ReturnsAsIs()
    {
        var previous = Environment.GetEnvironmentVariable(EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(EnvVar, "https://api.mythology.work/");

            Assert.Equal("https://api.mythology.work/", CloudEndpoints.ResolveCloudBaseUrl());
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVar, previous);
        }
    }
}
