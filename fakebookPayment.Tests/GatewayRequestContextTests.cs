using Fakebook.Payment.Configuration;
using Fakebook.Payment.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace fakebookPayment.Tests;

public sealed class GatewayRequestContextTests
{
    private const string Secret = "01234567890123456789012345678901";

    [Fact]
    public void Does_not_trust_user_header_without_gateway_secret()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers["X-User-Id"] = "1";
        Assert.Throws<UnauthorizedAccessException>(() => Create(http).GetRequired());
    }

    [Fact]
    public void Returns_identity_only_after_gateway_authentication()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Gateway-Secret"] = Secret;
        http.Request.Headers["X-User-Id"] = "1";
        http.Request.Headers["X-Session-Id"] = "session-1";
        var result = Create(http).GetRequired();
        Assert.Equal(1, result.UserId);
        Assert.Equal("session-1", result.SessionId);
    }

    [Fact]
    public void Public_plan_queries_can_validate_gateway_without_a_user_session()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Gateway-Secret"] = Secret;
        Create(http).EnsureTrustedGateway();
    }

    [Fact]
    public void Duplicate_identity_headers_are_rejected_and_metadata_is_sanitized()
    {
        var duplicate = new DefaultHttpContext();
        duplicate.Request.Headers["X-Gateway-Secret"] = Secret;
        duplicate.Request.Headers.Append("X-User-Id", "1");
        duplicate.Request.Headers.Append("X-User-Id", "2");
        Assert.Throws<UnauthorizedAccessException>(() => Create(duplicate).GetRequired());

        var unsafeMetadata = new DefaultHttpContext { TraceIdentifier = "safe-trace" };
        unsafeMetadata.Request.Headers["X-Gateway-Secret"] = Secret;
        unsafeMetadata.Request.Headers["X-User-Id"] = "1";
        unsafeMetadata.Request.Headers["X-Correlation-Id"] = "bad\u202Etrace";
        unsafeMetadata.Request.Headers["X-Session-Id"] = new string('x', 129);

        var result = Create(unsafeMetadata).GetRequired();
        Assert.Equal("safe-trace", result.CorrelationId);
        Assert.Null(result.SessionId);
    }

    private static GatewayRequestContextAccessor Create(HttpContext context) => new(
        new HttpContextAccessor { HttpContext = context },
        Options.Create(new GatewayOptions { SharedSecret = Secret }));
}
