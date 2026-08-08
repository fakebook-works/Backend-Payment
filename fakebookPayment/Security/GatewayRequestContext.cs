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
        if (!SecretComparer.FixedTimeEqualsHeader(context.Request.Headers["X-Gateway-Secret"], options.Value.SharedSecret))
            throw new UnauthorizedAccessException("Untrusted gateway request.");
    }

    public GatewayRequestContext GetRequired()
    {
        EnsureTrustedGateway();
        var context = accessor.HttpContext!;

        var userValues = context.Request.Headers["X-User-Id"];
        var rawUserId = userValues.Count == 1 ? userValues[0] : null;
        if (string.IsNullOrEmpty(rawUserId) || rawUserId.Length > 19 ||
            rawUserId.Any(character => character is < '0' or > '9') ||
            !long.TryParse(rawUserId, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var userId) || userId <= 0)
            throw new UnauthorizedAccessException("Missing trusted user identity.");

        var correlationValues = context.Request.Headers["X-Correlation-Id"];
        var correlationId = correlationValues.Count == 1 ? correlationValues[0] : null;
        if (string.IsNullOrEmpty(correlationId) || correlationId.Length > 128 ||
            correlationId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '-' and not '_' and not '.' and not ':' and not '/'))
        {
            correlationId = context.TraceIdentifier;
        }

        var sessionValues = context.Request.Headers["X-Session-Id"];
        var sessionId = sessionValues.Count == 1 ? sessionValues[0] : null;
        if (string.IsNullOrEmpty(sessionId) || sessionId.Length > 128 ||
            sessionId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            sessionId = null;
        }

        return new(userId, sessionId, correlationId!);
    }
}
