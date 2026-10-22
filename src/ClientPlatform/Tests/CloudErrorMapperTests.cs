using System.Net;
using ClientPlatform;
using Core.Cloud;
using Xunit;

namespace ClientPlatform.Tests;

/// <summary>
/// CloudErrorMapper 对双错误形状的解析（S7）：
/// - RFC 7807（CloudExceptionHandler）：code/detail；
/// - RFC 6749（OAuth 端点）：error/error_description。
/// 两种形状都必须得到稳定 code 与可读 message，且 OAuth 拒绝码映射到 Authenticating。
/// </summary>
public sealed class CloudErrorMapperTests
{
    [Fact]
    public void Map_ProblemDetailsShape_ExtractsCodeAndDetail()
    {
        var body =
            """{"type":"about:blank","title":"not_found","status":404,"detail":"The requested resource was not found.","code":"not_found"}""";

        var exception = CloudErrorMapper.Map(HttpStatusCode.NotFound, body);

        Assert.Equal("not_found", exception.Code);
        Assert.Equal("The requested resource was not found.", exception.Message);
    }

    [Fact]
    public void Map_OAuthShape_ExtractsErrorCodeAndDescription()
    {
        var body =
            """{"error":"invalid_grant","error_description":"授权码已被使用。","title":"invalid_grant","detail":"授权码已被使用。","code":"invalid_grant"}""";

        var exception = CloudErrorMapper.Map(HttpStatusCode.BadRequest, body);

        Assert.Equal("invalid_grant", exception.Code);
        Assert.Equal("授权码已被使用。", exception.Message);
    }

    [Fact]
    public void Map_OAuthShapeWithoutProblemFields_FallsBackToErrorFields()
    {
        // 旧服务端只有 RFC 6749 字段：code 取 error，message 取 error_description。
        var body =
            """{"error":"expired_code","error_description":"授权码已过期，请在客户端重新发起登录。"}""";

        var exception = CloudErrorMapper.Map(HttpStatusCode.BadRequest, body);

        Assert.Equal("expired_code", exception.Code);
        Assert.Equal("授权码已过期，请在客户端重新发起登录。", exception.Message);
    }

    [Theory]
    [InlineData("invalid_grant")]
    [InlineData("expired_code")]
    [InlineData("unauthorized_client")]
    [InlineData("invalid_api_key")]
    [InlineData("invalid_credentials")]
    public void ToConnectionState_OAuthRejectionCodes_MapToAuthenticating(string code)
    {
        Assert.Equal(CloudConnectionState.Authenticating, CloudErrorMapper.ToConnectionState(code));
    }
}
