using System.Globalization;
using System.Text;
using System.Text.Json;
using Fakebook.Payment.Configuration;
using Fakebook.Payment.Models;
using Fakebook.Payment.Security;
using Microsoft.Extensions.Options;
using PayOS;
using PayOS.Models;
using PayOS.Models.V2.PaymentRequests;
using PayOS.Models.Webhooks;

namespace Fakebook.Payment.Services;

public sealed record ProviderCheckout(string PaymentLinkId, string CheckoutUrl);
public enum ProviderPaymentLinkStatus { Pending, Processing, Paid, Cancelled, Expired, Failed, Underpaid }
public sealed record ProviderPaidEvidence(string Reference, DateTimeOffset PaidAt);
public sealed record ProviderPaymentLink(
    long OrderCode,
    long Amount,
    string PaymentLinkId,
    ProviderPaymentLinkStatus Status,
    ProviderPaidEvidence? PaidEvidence = null);

public interface IPayOSPaymentProvider
{
    Task ConfirmWebhookAsync(string webhookUrl, CancellationToken cancellationToken);
    Task<ProviderCheckout> CreateCheckoutAsync(PaymentOrder order, CancellationToken cancellationToken);
    Task<ProviderPaymentLink> GetPaymentLinkAsync(long orderCode, CancellationToken cancellationToken);
    Task<VerifiedPayment> VerifyWebhookAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken);
}

public sealed class PayOSPaymentProvider : IPayOSPaymentProvider
{
    private const int MaxProviderIdentifierBytes = 256;
    private const int MaxProviderDescriptionBytes = 255;
    private const int MaxProviderTransactions = 100;
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

    public async Task ConfirmWebhookAsync(string webhookUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("The public PayOS webhook URL must use HTTPS.");
        await _client.Webhooks.ConfirmAsync(webhookUrl, new RequestOptions<ConfirmWebhookRequest>
        {
            CancellationToken = cancellationToken
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
            !IsSafeProviderText(response.PaymentLinkId, MaxProviderIdentifierBytes) ||
            !IsSafeProviderText(response.CheckoutUrl, 2_048) ||
            !Uri.TryCreate(response.CheckoutUrl, UriKind.Absolute, out var checkoutUri) || checkoutUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("PayOS returned an incomplete checkout response.");
        return new(response.PaymentLinkId, response.CheckoutUrl);
    }

    public async Task<ProviderPaymentLink> GetPaymentLinkAsync(long orderCode, CancellationToken cancellationToken)
    {
        if (orderCode is < 1 or > OrderCodeValidator.MaximumOrderCode)
            throw new ArgumentOutOfRangeException(nameof(orderCode));
        var response = await _client.PaymentRequests.GetAsync(orderCode, new RequestOptions
        {
            CancellationToken = cancellationToken
        });
        return MapPaymentLink(orderCode, response);
    }

    internal static ProviderPaymentLink MapPaymentLink(long expectedOrderCode, PaymentLink response)
    {
        if (expectedOrderCode is < 1 or > OrderCodeValidator.MaximumOrderCode ||
            response.OrderCode != expectedOrderCode || response.Amount <= 0 ||
            !IsSafeProviderText(response.Id, MaxProviderIdentifierBytes))
            throw new InvalidOperationException("PayOS returned an incomplete payment-link response.");
        var status = response.Status switch
        {
            PaymentLinkStatus.Pending => ProviderPaymentLinkStatus.Pending,
            PaymentLinkStatus.Processing => ProviderPaymentLinkStatus.Processing,
            PaymentLinkStatus.Paid => ProviderPaymentLinkStatus.Paid,
            PaymentLinkStatus.Cancelled => ProviderPaymentLinkStatus.Cancelled,
            PaymentLinkStatus.Expired => ProviderPaymentLinkStatus.Expired,
            PaymentLinkStatus.Failed => ProviderPaymentLinkStatus.Failed,
            PaymentLinkStatus.Underpaid => ProviderPaymentLinkStatus.Underpaid,
            _ => throw new InvalidOperationException("PayOS returned an unsupported payment-link status.")
        };

        ProviderPaidEvidence? paidEvidence = null;
        if (status == ProviderPaymentLinkStatus.Paid)
        {
            if (response.AmountPaid != response.Amount || response.AmountRemaining != 0 ||
                response.Transactions is not { Count: > 0 and <= MaxProviderTransactions })
                throw new InvalidOperationException("PayOS returned incomplete paid-payment evidence.");

            long transactionTotal = 0;
            DateTimeOffset? paidAt = null;
            foreach (var transaction in response.Transactions)
            {
                if (transaction.Amount <= 0 ||
                    !IsSafeProviderText(transaction.Reference, MaxProviderIdentifierBytes) ||
                    !IsSafeProviderText(transaction.TransactionDateTime, 64, allowSpaces: true))
                    throw new InvalidOperationException("PayOS returned an invalid paid transaction.");
                try
                {
                    transactionTotal = checked(transactionTotal + transaction.Amount);
                }
                catch (OverflowException)
                {
                    throw new InvalidOperationException("PayOS returned an invalid paid transaction total.");
                }

                var transactionPaidAt = ParsePaymentLinkTimestamp(transaction.TransactionDateTime);
                if (paidAt is null || transactionPaidAt > paidAt)
                    paidAt = transactionPaidAt;
            }

            if (transactionTotal != response.AmountPaid || paidAt is null)
                throw new InvalidOperationException("PayOS returned inconsistent paid-payment evidence.");

            var reference = response.Transactions.Count == 1
                ? response.Transactions[0].Reference
                : $"payos-link:{response.Id}";
            paidEvidence = new ProviderPaidEvidence(reference, paidAt.Value);
        }

        return new(response.OrderCode, response.Amount, response.Id, status, paidEvidence);
    }

    public async Task<VerifiedPayment> VerifyWebhookAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var webhook = JsonSerializer.Deserialize<Webhook>(body.Span, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            MaxDepth = 16
        }) ?? throw new InvalidOperationException("Invalid PayOS webhook JSON.");
        var data = await _client.Webhooks.VerifyAsync(webhook);
        var providerDescription = NormalizeProviderDescription(data.Description2);
        if (!webhook.Success || webhook.Code != "00" || data.Code != "00" ||
            data.OrderCode is < 1 or > OrderCodeValidator.MaximumOrderCode ||
            data.Amount <= 0 ||
            !string.Equals(data.Currency, "VND", StringComparison.Ordinal) ||
            !IsSafeProviderText(data.Reference, MaxProviderIdentifierBytes) ||
            !IsSafeProviderText(data.PaymentLinkId, MaxProviderIdentifierBytes) ||
            providerDescription is null)
            throw new InvalidOperationException("PayOS webhook is signed but does not represent a successful payment.");
        var paidAt = ParsePayOSTimestamp(data.TransactionDateTime);
        return new(data.OrderCode, data.Amount, data.Currency, data.Reference, data.PaymentLinkId,
            data.Code, providerDescription, paidAt);
    }

    private static DateTimeOffset ParsePayOSTimestamp(string value)
    {
        if (!DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var timestamp))
            throw new InvalidOperationException("PayOS returned an invalid transaction timestamp.");
        return new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Unspecified), TimeSpan.FromHours(7)).ToUniversalTime();
    }

    private static DateTimeOffset ParsePaymentLinkTimestamp(string value)
    {
        if (DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var payOSTimestamp))
            return new DateTimeOffset(DateTime.SpecifyKind(payOSTimestamp, DateTimeKind.Unspecified),
                TimeSpan.FromHours(7)).ToUniversalTime();
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var timestamp))
            return timestamp.ToUniversalTime();
        throw new InvalidOperationException("PayOS returned an invalid payment-link transaction timestamp.");
    }

    internal static string? NormalizeProviderDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxProviderDescriptionBytes * 2)
            return null;

        string normalized;
        try
        {
            normalized = value.Normalize(NormalizationForm.FormC).Trim();
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (normalized.Length == 0 || Encoding.UTF8.GetByteCount(normalized) > MaxProviderDescriptionBytes)
            return null;

        var combiningTotal = 0;
        var combiningRun = 0;
        foreach (var rune in normalized.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or
                UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or
                UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator or
                UnicodeCategory.ParagraphSeparator)
                return null;

            if (category is UnicodeCategory.NonSpacingMark or
                UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                combiningTotal++;
                combiningRun++;
                if (combiningRun > 3 || combiningTotal > 32)
                    return null;
            }
            else
            {
                combiningRun = 0;
            }
        }

        return normalized;
    }

    private static bool IsSafeProviderText(
        string? value,
        int maximumUtf8Bytes,
        bool allowSpaces = false)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumUtf8Bytes ||
            Encoding.UTF8.GetByteCount(value) > maximumUtf8Bytes)
        {
            return false;
        }

        // Provider identifiers, URLs and references are protocol metadata, not user
        // prose. Restrict them to printable ASCII so malformed UTF-16, bidi controls,
        // private-use glyphs and combining-heavy values can never reach persistence or
        // a browser redirect.
        var minimum = allowSpaces ? '\x20' : '\x21';
        return value.All(character => character >= minimum && character <= '\x7e');
    }
}
