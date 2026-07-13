using Fakebook.Payment.Configuration;
using Microsoft.Extensions.Options;

namespace Fakebook.Payment.Security;

public sealed record GatewayRequestContext(long UserId, string? SessionId, string CorrelationId);
public interface IGatewayRequestContextAccessor
{
    void EnsureTrustedGateway();
    GatewayRequestContext GetRequired();
}

public sealed class GatewayRequestContextAccessor(IHttpContextAccessor accessor, IOptions<GatewayOptions> options)
    : IGatewayRequestContextAccessor
{
    public void EnsureTrustedGateway()
    {
        var context = accessor.HttpContext ?? throw new UnauthorizedAccessException("Missing HTTP context.");
        if (!SecretComparer.FixedTimeEquals(context.Request.Headers["X-Gateway-Secret"], options.Value.SharedSecret))
            throw new UnauthorizedAccessException("Untrusted gateway request.");
    }

    public GatewayRequestContext GetRequired()
    {
        EnsureTrustedGateway();
        var context = accessor.HttpContext!;

        var rawUserId = context.Request.Headers["X-User-Id"].ToString().Trim();
        if (!long.TryParse(rawUserId, out var userId) || userId <= 0)
            throw new UnauthorizedAccessException("Missing trusted user identity.");

        var correlationId = context.Request.Headers["X-Correlation-Id"].ToString().Trim();
        if (string.IsNullOrEmpty(correlationId) || correlationId.Length > 128) correlationId = context.TraceIdentifier;
        var sessionId = context.Request.Headers["X-Session-Id"].ToString().Trim();
        return new(userId, string.IsNullOrEmpty(sessionId) ? null : sessionId, correlationId);
    }
}
