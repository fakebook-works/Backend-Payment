using Fakebook.Payment.Configuration;
using Fakebook.Payment.Services;
using Microsoft.Extensions.Options;

namespace Fakebook.Payment.Workers;

public sealed class PayOSWebhookRegistrationWorker(
    IPayOSPaymentProvider payOS,
    IOptions<PaymentOptions> options,
    ILogger<PayOSWebhookRegistrationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2)
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var payment = options.Value;
        if (!payment.PaymentsEnabled || !payment.RegisterWebhookOnStartup)
            return;

        var webhookUrl = $"{payment.PublicBaseUrl.TrimEnd('/')}/api/webhooks/payos";
        for (var attempt = 0; attempt < RetryDelays.Length; attempt++)
        {
            if (RetryDelays[attempt] > TimeSpan.Zero)
                await Task.Delay(RetryDelays[attempt], stoppingToken);

            try
            {
                await payOS.ConfirmWebhookAsync(webhookUrl, stoppingToken);
                logger.LogInformation("PayOS webhook registration confirmed.");
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "PayOS webhook registration attempt {Attempt} failed: {ErrorType}.",
                    attempt + 1,
                    exception.GetType().Name);
            }
        }

        logger.LogError(
            "PayOS webhook registration was not confirmed. Successful browser returns cannot activate Premium until the public webhook route is reachable.");
    }
}
