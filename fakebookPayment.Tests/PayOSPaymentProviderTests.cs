using Fakebook.Payment.Services;
using PayOS.Models.V2.PaymentRequests;

namespace fakebookPayment.Tests;

public sealed class PayOSPaymentProviderTests
{
    [Theory]
    [InlineData(PaymentLinkStatus.Pending, ProviderPaymentLinkStatus.Pending)]
    [InlineData(PaymentLinkStatus.Processing, ProviderPaymentLinkStatus.Processing)]
    [InlineData(PaymentLinkStatus.Paid, ProviderPaymentLinkStatus.Paid)]
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
