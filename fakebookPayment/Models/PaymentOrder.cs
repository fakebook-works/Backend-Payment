using HotChocolate.Types.Relay;

namespace Fakebook.Payment.Models;

public enum PaymentOrderStatus
{
    Created, Pending, Paid, ActivationPending, Activated, Cancelled, Expired, Failed
}

public sealed class PaymentOrder
{
    public long Id { get; init; }
    public long OrderCode { get; init; }
    public long UserId { get; init; }
    public PremiumPlanCode Plan { get; init; }
    public long Amount { get; init; }
    public string Currency { get; init; } = "VND";
    public PaymentOrderStatus Status { get; init; }
    public string? ProviderPaymentLinkId { get; init; }
    public string? CheckoutUrl { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? PaidAt { get; init; }
    public DateTimeOffset? ActivatedAt { get; init; }
    public DateTimeOffset? TargetValidDate { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record CreatePremiumCheckoutInput(PremiumPlanCode Plan);
public sealed record PremiumCheckoutPayload([property: ID] string OrderCode, PaymentOrderStatus Status, string CheckoutUrl);
public sealed record VerifiedPayment(long OrderCode, long Amount, string Currency, string Reference, string PaymentLinkId, string ProviderCode, string ProviderDescription, DateTimeOffset PaidAt);
