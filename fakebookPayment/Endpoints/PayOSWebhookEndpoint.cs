using Fakebook.Payment.Configuration;
using Fakebook.Payment.Repositories;
using Fakebook.Payment.Security;
using Fakebook.Payment.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Fakebook.Payment.Endpoints;

public static class PayOSWebhookEndpoint
{
    private const int MaxBodyBytes = 64 * 1024;

    public static IEndpointRouteBuilder MapPayOSWebhook(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/internal/webhooks/payos", HandleAsync)
            .DisableAntiforgery()
            .RequireRateLimiting("payos-webhook");
        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext context,
        IOptions<GatewayOptions> gateway,
        IPayOSPaymentProvider payOS,
        IPaymentRepository repository,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!SecretComparer.FixedTimeEquals(context.Request.Headers["X-Gateway-Secret"], gateway.Value.SharedSecret))
            return Results.Unauthorized();
        var mediaType = context.Request.ContentType?.Split(';', 2)[0].Trim();
        if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        if (context.Request.ContentLength is > MaxBodyBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = MaxBodyBytes;

        byte[] body;
        try
        {
            body = await ReadLimitedAsync(context.Request.Body, MaxBodyBytes, cancellationToken);
            if (body.Length == 0) return Results.BadRequest();
        }
        catch (PayloadTooLargeException)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        try
        {
            var verified = await payOS.VerifyWebhookAsync(body, cancellationToken);
            var found = await repository.RecordVerifiedPaymentAsync(verified, cancellationToken);
            if (!found)
                loggerFactory.CreateLogger("PayOSWebhook").LogWarning("Verified webhook referenced an unknown payment order");
            return Results.Ok(new { success = true });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (PaymentOrderNotReadyException)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (NpgsqlException exception)
        {
            loggerFactory.CreateLogger("PayOSWebhook").LogError("Payment persistence failed: {ErrorType}", exception.GetType().Name);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception exception)
        {
            loggerFactory.CreateLogger("PayOSWebhook").LogWarning("PayOS webhook was rejected: {ErrorType}", exception.GetType().Name);
            return Results.BadRequest(new { success = false });
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream stream, int limit, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream(Math.Min(limit, 4096));
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > limit) throw new PayloadTooLargeException();
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
    }

    private sealed class PayloadTooLargeException : Exception;
}
