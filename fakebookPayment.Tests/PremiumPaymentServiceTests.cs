using Fakebook.Payment.Configuration;
using Fakebook.Payment.Models;
using Fakebook.Payment.Repositories;
using Fakebook.Payment.Services;
using Microsoft.Extensions.Options;

namespace fakebookPayment.Tests;

public sealed class PremiumPaymentServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Active_premium_prevents_checkout()
    {
        var repository = new FakeRepository();
        var service = Create(repository, new FakeProvider(), new FakeAuthentication { ValidDate = Now.AddDays(1) });

        var error = await Assert.ThrowsAsync<PremiumPaymentException>(() =>
            service.CreateCheckoutAsync(42, PremiumPlanCode.Monthly, CancellationToken.None));

        Assert.Equal("PREMIUM_ALREADY_ACTIVE", error.Code);
        Assert.Null(repository.CreatedPlan);
    }

    [Fact]
    public async Task Checkout_uses_server_price_and_thirty_minute_ttl()
    {
        var repository = new FakeRepository();
        var provider = new FakeProvider();
        var service = Create(repository, provider, new FakeAuthentication());

        var result = await service.CreateCheckoutAsync(42, PremiumPlanCode.Yearly, CancellationToken.None);

        Assert.Equal(500_000, repository.CreatedPlan?.Amount);
        Assert.Equal(Now.AddMinutes(30), repository.CreatedExpiresAt);
        Assert.Equal(500_000, provider.ReceivedOrder?.Amount);
        Assert.Equal("1", result.OrderCode);
    }

    [Fact]
    public async Task Provider_failure_is_sanitized_and_order_is_failed()
    {
        var repository = new FakeRepository();
        var service = Create(repository, new FakeProvider { Failure = new Exception("raw provider secret") }, new FakeAuthentication());

        var error = await Assert.ThrowsAsync<PremiumPaymentException>(() =>
            service.CreateCheckoutAsync(42, PremiumPlanCode.Monthly, CancellationToken.None));

        Assert.Equal("PAYMENT_PROVIDER_UNAVAILABLE", error.Code);
        Assert.DoesNotContain("raw provider", error.Message);
        Assert.True(repository.Failed);
    }

    [Fact]
    public async Task Cancelled_provider_status_marks_the_owned_order_cancelled()
    {
        var repository = new FakeRepository
        {
            OwnedOrder = PendingOrder(42)
        };
        var provider = new FakeProvider
        {
            PaymentLink = new ProviderPaymentLink(1, 52_000, "link-1", ProviderPaymentLinkStatus.Cancelled)
        };
        var service = Create(repository, provider, new FakeAuthentication());

        var result = await service.ReconcileCheckoutAsync(42, "1", CancellationToken.None);

        Assert.True(repository.Cancelled);
        Assert.Equal(PaymentOrderStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task Paid_provider_status_stays_pending_until_a_verified_webhook_arrives()
    {
        var repository = new FakeRepository
        {
            OwnedOrder = PendingOrder(42)
        };
        var provider = new FakeProvider
        {
            PaymentLink = new ProviderPaymentLink(1, 52_000, "link-1", ProviderPaymentLinkStatus.Paid)
        };
        var service = Create(repository, provider, new FakeAuthentication());

        var result = await service.ReconcileCheckoutAsync(42, "1", CancellationToken.None);

        Assert.False(repository.Cancelled);
        Assert.Equal(PaymentOrderStatus.Pending, result.Status);
    }

    [Theory]
    [InlineData(ProviderPaymentLinkStatus.Pending)]
    [InlineData(ProviderPaymentLinkStatus.Processing)]
    public async Task Non_terminal_provider_status_stays_pending(ProviderPaymentLinkStatus status)
    {
        var repository = new FakeRepository { OwnedOrder = PendingOrder(42) };
        var provider = new FakeProvider
        {
            PaymentLink = new ProviderPaymentLink(1, 52_000, "link-1", status)
        };
        var service = Create(repository, provider, new FakeAuthentication());

        var result = await service.ReconcileCheckoutAsync(42, "1", CancellationToken.None);

        Assert.False(repository.Cancelled);
        Assert.Equal(PaymentOrderStatus.Pending, result.Status);
    }

    [Theory]
    [InlineData(2, 52_000, "link-1")]
    [InlineData(1, 500_000, "link-1")]
    [InlineData(1, 52_000, "wrong-link")]
    public async Task Provider_identity_or_amount_mismatch_changes_no_state(long orderCode, long amount, string paymentLinkId)
    {
        var repository = new FakeRepository { OwnedOrder = PendingOrder(42) };
        var provider = new FakeProvider
        {
            PaymentLink = new ProviderPaymentLink(orderCode, amount, paymentLinkId, ProviderPaymentLinkStatus.Cancelled)
        };
        var service = Create(repository, provider, new FakeAuthentication());

        var error = await Assert.ThrowsAsync<PremiumPaymentException>(() =>
            service.ReconcileCheckoutAsync(42, "1", CancellationToken.None));

        Assert.Equal("PAYMENT_PROVIDER_INVALID_RESPONSE", error.Code);
        Assert.False(repository.Cancelled);
        Assert.Equal(PaymentOrderStatus.Pending, repository.OwnedOrder?.Status);
    }

    private static PaymentOrder PendingOrder(long userId) => new()
    {
        Id = 10,
        OrderCode = 1,
        UserId = userId,
        Plan = PremiumPlanCode.Monthly,
        Amount = 52_000,
        Currency = "VND",
        Status = PaymentOrderStatus.Pending,
        ProviderPaymentLinkId = "link-1",
        CheckoutUrl = "https://pay.payos.vn/example",
        ExpiresAt = Now.AddMinutes(30),
        CreatedAt = Now,
        UpdatedAt = Now
    };

    private static PremiumPaymentService Create(FakeRepository repository, FakeProvider provider, FakeAuthentication authentication) =>
        new(repository, provider, authentication,
            Options.Create(new PaymentOptions { PaymentsEnabled = true, CheckoutTtlMinutes = 30 }),
            new FixedTimeProvider(Now));

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class FakeAuthentication : IAuthenticationClient
    {
        public DateTimeOffset? ValidDate { get; init; }
        public Task<DateTimeOffset?> GetValidDateAsync(long userId, CancellationToken cancellationToken) => Task.FromResult(ValidDate);
        public Task SetValidDateAsync(long userId, DateTimeOffset validDate, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeProvider : IPayOSPaymentProvider
    {
        public Exception? Failure { get; init; }
        public ProviderPaymentLink? PaymentLink { get; init; }
        public PaymentOrder? ReceivedOrder { get; private set; }
        public Task<ProviderCheckout> CreateCheckoutAsync(PaymentOrder order, CancellationToken cancellationToken)
        {
            ReceivedOrder = order;
            return Failure is null
                ? Task.FromResult(new ProviderCheckout("link-1", "https://pay.payos.vn/example"))
                : Task.FromException<ProviderCheckout>(Failure);
        }
        public Task<ProviderPaymentLink> GetPaymentLinkAsync(long orderCode, CancellationToken cancellationToken) =>
            Task.FromResult(PaymentLink ?? throw new NotSupportedException());
        public Task<VerifiedPayment> VerifyWebhookAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeRepository : IPaymentRepository
    {
        public PremiumPlan? CreatedPlan { get; private set; }
        public DateTimeOffset? CreatedExpiresAt { get; private set; }
        public bool Failed { get; private set; }
        public bool Cancelled { get; private set; }
        public PaymentOrder? OwnedOrder { get; set; }
        public Task<PaymentOrder> CreateOrderAsync(long userId, PremiumPlan plan, DateTimeOffset expiresAt, CancellationToken cancellationToken)
        {
            CreatedPlan = plan;
            CreatedExpiresAt = expiresAt;
            return Task.FromResult(new PaymentOrder
            {
                Id = 10,
                OrderCode = 1,
                UserId = userId,
                Plan = plan.Code,
                Amount = plan.Amount,
                Status = PaymentOrderStatus.Created,
                ExpiresAt = expiresAt,
                CreatedAt = Now,
                UpdatedAt = Now
            });
        }
        public Task MarkCheckoutPendingAsync(long orderId, string paymentLinkId, string checkoutUrl, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task MarkOrderFailedAsync(long orderId, CancellationToken cancellationToken) { Failed = true; return Task.CompletedTask; }
        public Task<PaymentOrder?> GetOwnedOrderAsync(long userId, long orderCode, CancellationToken cancellationToken) => Task.FromResult(OwnedOrder);
        public Task MarkOrderCancelledAsync(long orderId, CancellationToken cancellationToken)
        {
            Cancelled = true;
            if (OwnedOrder is not null)
            {
                OwnedOrder = new PaymentOrder
                {
                    Id = OwnedOrder.Id,
                    OrderCode = OwnedOrder.OrderCode,
                    UserId = OwnedOrder.UserId,
                    Plan = OwnedOrder.Plan,
                    Amount = OwnedOrder.Amount,
                    Currency = OwnedOrder.Currency,
                    Status = PaymentOrderStatus.Cancelled,
                    ProviderPaymentLinkId = OwnedOrder.ProviderPaymentLinkId,
                    CheckoutUrl = OwnedOrder.CheckoutUrl,
                    ExpiresAt = OwnedOrder.ExpiresAt,
                    CreatedAt = OwnedOrder.CreatedAt,
                    UpdatedAt = Now
                };
            }
            return Task.CompletedTask;
        }
        public Task<PaymentOrder?> GetUnfinishedOrderAsync(long userId, CancellationToken cancellationToken) => Task.FromResult<PaymentOrder?>(null);
        public Task ExpireStaleOrdersAsync(long userId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> RecordVerifiedPaymentAsync(VerifiedPayment payment, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<OutboxMessage?> LeaseNextOutboxAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IAsyncDisposable?> TryAcquireUserLockAsync(long userId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DateTimeOffset> SetActivationTargetAsync(OutboxMessage message, DateTimeOffset targetValidDate, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteActivationAsync(OutboxMessage message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RetryActivationAsync(OutboxMessage message, string errorCode, TimeSpan delay, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
