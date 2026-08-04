using Fakebook.Payment.Configuration;
using Fakebook.Payment.Models;
using Fakebook.Payment.Services;
using Fakebook.Payment.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace fakebookPayment.Tests;

public sealed class PayOSWebhookRegistrationWorkerTests
{
    [Fact]
    public async Task EnabledRegistration_ConfirmsThePublicGatewayWebhookRoute()
    {
        var provider = new RecordingProvider();
        var worker = new PayOSWebhookRegistrationWorker(
            provider,
            Options.Create(new PaymentOptions
            {
                PaymentsEnabled = true,
                RegisterWebhookOnStartup = true,
                PublicBaseUrl = "https://fakebook.example",
                FrontendPublicUrl = "https://fakebook.example"
            }),
            NullLogger<PayOSWebhookRegistrationWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        var url = await provider.ConfirmedUrl.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal("https://fakebook.example/api/webhooks/payos", url);
    }

    private sealed class RecordingProvider : IPayOSPaymentProvider
    {
        public TaskCompletionSource<string> ConfirmedUrl { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ConfirmWebhookAsync(string webhookUrl, CancellationToken cancellationToken)
        {
            ConfirmedUrl.TrySetResult(webhookUrl);
            return Task.CompletedTask;
        }

        public Task<ProviderCheckout> CreateCheckoutAsync(PaymentOrder order, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProviderPaymentLink> GetPaymentLinkAsync(long orderCode, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<VerifiedPayment> VerifyWebhookAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
