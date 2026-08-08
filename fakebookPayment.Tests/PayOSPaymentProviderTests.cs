using System.Text.Json;
using Fakebook.Payment.Configuration;
using Fakebook.Payment.Services;
using Microsoft.Extensions.Options;
using PayOS;
using PayOS.Models;
using PayOS.Models.V2.PaymentRequests;
using PayOS.Models.Webhooks;

namespace fakebookPayment.Tests;

public sealed class PayOSPaymentProviderTests
{
    [Theory]
    [InlineData(PaymentLinkStatus.Pending, ProviderPaymentLinkStatus.Pending)]
    [InlineData(PaymentLinkStatus.Processing, ProviderPaymentLinkStatus.Processing)]
    [InlineData(PaymentLinkStatus.Cancelled, ProviderPaymentLinkStatus.Cancelled)]
    [InlineData(PaymentLinkStatus.Expired, ProviderPaymentLinkStatus.Expired)]
    [InlineData(PaymentLinkStatus.Failed, ProviderPaymentLinkStatus.Failed)]
    [InlineData(PaymentLinkStatus.Underpaid, ProviderPaymentLinkStatus.Underpaid)]
    public void Payment_link_response_maps_all_sdk_statuses(PaymentLinkStatus sdkStatus, ProviderPaymentLinkStatus expected)
    {
        var result = PayOSPaymentProvider.MapPaymentLink(123, new PaymentLink
        {
            OrderCode = 123,
            Amount = 52_000,
            Id = "link-1",
            Status = sdkStatus
        });

        Assert.Equal(expected, result.Status);
        Assert.Null(result.PaidEvidence);
    }

    [Theory]
    [InlineData("2026-07-13T19:02:00+07:00")]
    [InlineData("2026-07-13 19:02:00")]
    public void Paid_payment_link_maps_complete_signed_provider_evidence(string timestamp)
    {
        var result = PayOSPaymentProvider.MapPaymentLink(123, new PaymentLink
        {
            OrderCode = 123,
            Amount = 52_000,
            AmountPaid = 52_000,
            AmountRemaining = 0,
            Id = "link-1",
            Status = PaymentLinkStatus.Paid,
            Transactions =
            [
                new PaymentTransaction
                {
                    Amount = 52_000,
                    Reference = "reference-1",
                    TransactionDateTime = timestamp
                }
            ]
        });

        Assert.Equal(ProviderPaymentLinkStatus.Paid, result.Status);
        Assert.Equal("reference-1", result.PaidEvidence?.Reference);
        Assert.Equal(new DateTimeOffset(2026, 7, 13, 12, 2, 0, TimeSpan.Zero), result.PaidEvidence?.PaidAt);
    }

    [Fact]
    public void Provider_description_preserves_normal_unicode_and_rejects_rendering_abuse()
    {
        Assert.Equal("Thành công", PayOSPaymentProvider.NormalizeProviderDescription("  Thành công  "));
        Assert.Null(PayOSPaymentProvider.NormalizeProviderDescription("A" + new string('\u0301', 20)));
        Assert.Null(PayOSPaymentProvider.NormalizeProviderDescription("safe\u202Eevil"));
    }

    [Fact]
    public async Task Signed_webhook_accepts_the_providers_unicode_success_description()
    {
        const string checksumKey = "test-checksum-key-at-least-thirty-two-bytes";
        var data = new WebhookData
        {
            OrderCode = 123,
            Amount = 52_000,
            Description = "FB PRM 123",
            AccountNumber = "123456789",
            Reference = "reference-1",
            TransactionDateTime = "2026-07-13 12:00:00",
            Currency = "VND",
            PaymentLinkId = "payment-link-1",
            Code = "00",
            Description2 = "Thành công"
        };
        var cryptoClient = new PayOSClient(new global::PayOS.PayOSOptions
        {
            ClientId = "test-client",
            ApiKey = "test-api-key",
            ChecksumKey = checksumKey
        });
        var webhook = new Webhook
        {
            Code = "00",
            Description = "success",
            Success = true,
            Data = data,
            Signature = cryptoClient.Crypto.CreateSignatureFromObject(data, checksumKey)!
        };
        var provider = new PayOSPaymentProvider(
            Options.Create(new Fakebook.Payment.Configuration.PayOSOptions
            {
                ClientId = "test-client",
                ApiKey = "test-api-key",
                ChecksumKey = checksumKey
            }),
            Options.Create(new PaymentOptions
            {
                FrontendPublicUrl = "https://fakebook.example",
                PublicBaseUrl = "https://api.fakebook.example"
            }));

        var result = await provider.VerifyWebhookAsync(
            JsonSerializer.SerializeToUtf8Bytes(webhook),
            CancellationToken.None);

        Assert.Equal("Thành công", result.ProviderDescription);
        Assert.Equal(new DateTimeOffset(2026, 7, 13, 5, 0, 0, TimeSpan.Zero), result.PaidAt);
    }

    [Theory]
    [InlineData(51_999, 1, 52_000, "reference-1", "2026-07-13T19:02:00+07:00")]
    [InlineData(52_000, 0, 51_999, "reference-1", "2026-07-13T19:02:00+07:00")]
    [InlineData(52_000, 0, 52_000, "", "2026-07-13T19:02:00+07:00")]
    [InlineData(52_000, 0, 52_000, "reference-1", "invalid")]
    public void Paid_payment_link_rejects_incomplete_or_inconsistent_evidence(
        long amountPaid, long amountRemaining, long transactionAmount, string reference, string timestamp)
    {
        Assert.Throws<InvalidOperationException>(() => PayOSPaymentProvider.MapPaymentLink(123, new PaymentLink
        {
            OrderCode = 123,
            Amount = 52_000,
            AmountPaid = amountPaid,
            AmountRemaining = amountRemaining,
            Id = "link-1",
            Status = PaymentLinkStatus.Paid,
            Transactions =
            [
                new PaymentTransaction
                {
                    Amount = transactionAmount,
                    Reference = reference,
                    TransactionDateTime = timestamp
                }
            ]
        }));
    }

    [Theory]
    [InlineData(124, 52_000, "link-1")]
    [InlineData(123, 0, "link-1")]
    [InlineData(123, 52_000, "")]
    public void Incomplete_or_mismatched_provider_response_is_rejected(long orderCode, long amount, string paymentLinkId)
    {
        Assert.Throws<InvalidOperationException>(() => PayOSPaymentProvider.MapPaymentLink(123, new PaymentLink
        {
            OrderCode = orderCode,
            Amount = amount,
            Id = paymentLinkId,
            Status = PaymentLinkStatus.Pending
        }));
    }

    [Fact]
    public void Payment_link_response_rejects_unbounded_provider_identifiers()
    {
        Assert.Throws<InvalidOperationException>(() => PayOSPaymentProvider.MapPaymentLink(123, new PaymentLink
        {
            OrderCode = 123,
            Amount = 52_000,
            Id = new string('x', 257),
            Status = PaymentLinkStatus.Pending
        }));
    }

    [Fact]
    public void Paid_payment_link_rejects_an_unbounded_transaction_list()
    {
        var transactions = Enumerable.Range(0, 101)
            .Select(index => new PaymentTransaction
            {
                Amount = 520,
                Reference = $"reference-{index}",
                TransactionDateTime = "2026-07-13T19:02:00+07:00"
            })
            .ToList();

        Assert.Throws<InvalidOperationException>(() => PayOSPaymentProvider.MapPaymentLink(123, new PaymentLink
        {
            OrderCode = 123,
            Amount = 52_520,
            AmountPaid = 52_520,
            AmountRemaining = 0,
            Id = "link-1",
            Status = PaymentLinkStatus.Paid,
            Transactions = transactions
        }));
    }
}
