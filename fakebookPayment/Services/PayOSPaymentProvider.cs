using System.Globalization;
using System.Text.Json;
using Fakebook.Payment.Configuration;
using Fakebook.Payment.Models;
using Microsoft.Extensions.Options;
using PayOS;
using PayOS.Models;
using PayOS.Models.V2.PaymentRequests;
using PayOS.Models.Webhooks;

namespace Fakebook.Payment.Services;

public sealed record ProviderCheckout(string PaymentLinkId, string CheckoutUrl);
public enum ProviderPaymentLinkStatus { Pending, Processing, Paid, Cancelled, Expired, Failed, Underpaid }
public sealed record ProviderPaymentLink(long OrderCode, long Amount, string PaymentLinkId, ProviderPaymentLinkStatus Status);

public interface IPayOSPaymentProvider
{
    Task<ProviderCheckout> CreateCheckoutAsync(PaymentOrder order, CancellationToken cancellationToken);
    Task<ProviderPaymentLink> GetPaymentLinkAsync(long orderCode, CancellationToken cancellationToken);
    Task<VerifiedPayment> VerifyWebhookAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken);
}

public sealed class PayOSPaymentProvider : IPayOSPaymentProvider
{
    private readonly PayOSClient _client;
    private readonly PaymentOptions _paymentOptions;

    public PayOSPaymentProvider(IOptions<Fakebook.Payment.Configuration.PayOSOptions> payOS, IOptions<PaymentOptions> payment)
    {
        var options = payOS.Value;
        _paymentOptions = payment.Value;
        _client = new PayOSClient(new global::PayOS.PayOSOptions
        {
            ClientId = options.ClientId,
            ApiKey = options.ApiKey,
            ChecksumKey = options.ChecksumKey,
            TimeoutMs = 15_000,
            MaxRetries = 2
        });
    }

    public async Task<ProviderCheckout> CreateCheckoutAsync(PaymentOrder order, CancellationToken cancellationToken)
    {
        var baseUrl = _paymentOptions.FrontendPublicUrl.TrimEnd('/');
        var request = new CreatePaymentLinkRequest
        {
            OrderCode = order.OrderCode,
            Amount = order.Amount,
            Description = $"FB PRM {order.OrderCode}",
            ReturnUrl = $"{baseUrl}/premium/payment",
            CancelUrl = $"{baseUrl}/premium/payment",
            ExpiredAt = order.ExpiresAt.ToUnixTimeSeconds()
        };
        var response = await _client.PaymentRequests.CreateAsync(request, new RequestOptions<CreatePaymentLinkRequest>
        {
            CancellationToken = cancellationToken
        });
        if (response.OrderCode != order.OrderCode || response.Amount != order.Amount ||
            string.IsNullOrWhiteSpace(response.PaymentLinkId) || string.IsNullOrWhiteSpace(response.CheckoutUrl) ||
            !Uri.TryCreate(response.CheckoutUrl, UriKind.Absolute, out var checkoutUri) || checkoutUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("PayOS returned an incomplete checkout response.");
        return new(response.PaymentLinkId, response.CheckoutUrl);
    }

    public async Task<ProviderPaymentLink> GetPaymentLinkAsync(long orderCode, CancellationToken cancellationToken)
    {
        var response = await _client.PaymentRequests.GetAsync(orderCode, new RequestOptions
        {
            CancellationToken = cancellationToken
        });
        return MapPaymentLink(orderCode, response);
    }

    internal static ProviderPaymentLink MapPaymentLink(long expectedOrderCode, PaymentLink response)
    {
        if (response.OrderCode != expectedOrderCode || response.Amount <= 0 || string.IsNullOrWhiteSpace(response.Id))
            throw new InvalidOperationException("PayOS returned an incomplete payment-link response.");
        return new(response.OrderCode, response.Amount, response.Id, response.Status switch
        {
            PaymentLinkStatus.Pending => ProviderPaymentLinkStatus.Pending,
            PaymentLinkStatus.Processing => ProviderPaymentLinkStatus.Processing,
            PaymentLinkStatus.Paid => ProviderPaymentLinkStatus.Paid,
            PaymentLinkStatus.Cancelled => ProviderPaymentLinkStatus.Cancelled,
            PaymentLinkStatus.Expired => ProviderPaymentLinkStatus.Expired,
            PaymentLinkStatus.Failed => ProviderPaymentLinkStatus.Failed,
            PaymentLinkStatus.Underpaid => ProviderPaymentLinkStatus.Underpaid,
            _ => throw new InvalidOperationException("PayOS returned an unsupported payment-link status.")
        });
    }

    public async Task<VerifiedPayment> VerifyWebhookAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var webhook = JsonSerializer.Deserialize<Webhook>(body.Span, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Invalid PayOS webhook JSON.");
        var data = await _client.Webhooks.VerifyAsync(webhook);
        if (!webhook.Success || webhook.Code != "00" || data.Code != "00")
            throw new InvalidOperationException("PayOS webhook is signed but does not represent a successful payment.");
        var paidAt = ParsePayOSTimestamp(data.TransactionDateTime);
        return new(data.OrderCode, data.Amount, data.Currency, data.Reference, data.PaymentLinkId,
            data.Code, data.Description2, paidAt);
    }

    private static DateTimeOffset ParsePayOSTimestamp(string value)
    {
        if (!DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var timestamp))
            throw new InvalidOperationException("PayOS returned an invalid transaction timestamp.");
        return new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Unspecified), TimeSpan.FromHours(7)).ToUniversalTime();
    }
}
