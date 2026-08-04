using Fakebook.Payment.Services;
using PayOS.Models.V2.PaymentRequests;

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

    [Fact]
    public void Paid_payment_link_maps_complete_signed_provider_evidence()
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
                    TransactionDateTime = "2026-07-13T19:02:00+07:00"
                }
            ]
        });

        Assert.Equal(ProviderPaymentLinkStatus.Paid, result.Status);
        Assert.Equal("reference-1", result.PaidEvidence?.Reference);
        Assert.Equal(new DateTimeOffset(2026, 7, 13, 12, 2, 0, TimeSpan.Zero), result.PaidEvidence?.PaidAt);
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
}
