using Dapper;
using Fakebook.Payment.Models;
using Fakebook.Payment.Repositories;
using Fakebook.Payment.Services;
using Npgsql;
using Testcontainers.PostgreSql;

namespace fakebookPayment.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class PaymentRepositoryIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("payment_tests")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();
    private NpgsqlDataSource _dataSource = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _dataSource = NpgsqlDataSource.Create(_postgres.GetConnectionString());
        var schemaPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fakebookPayment", "schema.sql"));
        var schema = await File.ReadAllTextAsync(schemaPath);
        await using var connection = await _dataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(schema);
    }

    [Fact]
    public async Task Verified_payment_is_idempotent_and_creates_one_activation_message()
    {
        var repository = new PaymentRepository(_dataSource, new SnowflakeIdGenerator(3));
        var order = await repository.CreateOrderAsync(1, PremiumPlanCatalogue.Get(PremiumPlanCode.Monthly),
            DateTimeOffset.UtcNow.AddMinutes(30), CancellationToken.None);
        await repository.MarkCheckoutPendingAsync(order.Id, "link-1", "https://pay.payos.vn/example", CancellationToken.None);
        var payment = new VerifiedPayment(order.OrderCode, 52_000, "VND", "reference-1", "link-1", "00", "Thành công", DateTimeOffset.UtcNow);

        Assert.True(await repository.RecordVerifiedPaymentAsync(payment, CancellationToken.None));
        Assert.True(await repository.RecordVerifiedPaymentAsync(payment, CancellationToken.None));
        Assert.True(await repository.RecordVerifiedPaymentAsync(payment with { Reference = "reference-2" }, CancellationToken.None));
        var message = await repository.LeaseNextOutboxAsync(CancellationToken.None);
        Assert.NotNull(message);
        Assert.Equal(1, message.UserId);
        Assert.Null(message.TargetValidDate);
        await using var userLock = await repository.TryAcquireUserLockAsync(message.UserId, CancellationToken.None);
        Assert.NotNull(userLock);
        Assert.Null(await repository.TryAcquireUserLockAsync(message.UserId, CancellationToken.None));
        var target = DateTimeOffset.UtcNow.AddMonths(1);
        var storedTarget = await repository.SetActivationTargetAsync(message, target, CancellationToken.None);
        Assert.InRange((storedTarget - target).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(1));

        await using var connection = await _dataSource.OpenConnectionAsync();
        await connection.ExecuteAsync("UPDATE payment.outbox_message SET next_attempt_at = now() WHERE id = @Id", new { message.Id });
        var retryMessage = await repository.LeaseNextOutboxAsync(CancellationToken.None);
        Assert.NotNull(retryMessage?.TargetValidDate);
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM payment.payment_transaction"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM payment.outbox_message"));
    }

    [Fact]
    public async Task Late_payment_can_activate_after_a_new_checkout_was_created()
    {
        var repository = new PaymentRepository(_dataSource, new SnowflakeIdGenerator(4));
        var oldOrder = await repository.CreateOrderAsync(2, PremiumPlanCatalogue.Get(PremiumPlanCode.Monthly),
            DateTimeOffset.UtcNow.AddMinutes(-1), CancellationToken.None);
        await repository.MarkCheckoutPendingAsync(oldOrder.Id, "old-link", "https://pay.payos.vn/old", CancellationToken.None);
        await repository.ExpireStaleOrdersAsync(2, CancellationToken.None);
        _ = await repository.CreateOrderAsync(2, PremiumPlanCatalogue.Get(PremiumPlanCode.Yearly),
            DateTimeOffset.UtcNow.AddMinutes(30), CancellationToken.None);

        var recorded = await repository.RecordVerifiedPaymentAsync(
            new VerifiedPayment(oldOrder.OrderCode, 52_000, "VND", "late-reference", "old-link", "00", "Thành công", DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.True(recorded);
    }

    [Fact]
    public async Task Webhook_before_checkout_metadata_is_saved_is_retryable()
    {
        var repository = new PaymentRepository(_dataSource, new SnowflakeIdGenerator(5));
        var order = await repository.CreateOrderAsync(3, PremiumPlanCatalogue.Get(PremiumPlanCode.Monthly),
            DateTimeOffset.UtcNow.AddMinutes(30), CancellationToken.None);

        await Assert.ThrowsAsync<PaymentOrderNotReadyException>(() => repository.RecordVerifiedPaymentAsync(
            new VerifiedPayment(order.OrderCode, 52_000, "VND", "early-reference", "early-link", "00", "Thành công", DateTimeOffset.UtcNow),
            CancellationToken.None));

        var stored = await repository.GetOwnedOrderAsync(3, order.OrderCode, CancellationToken.None);
        Assert.Equal(PaymentOrderStatus.Created, stored?.Status);
    }

    [Fact]
    public async Task Signed_payment_mismatch_is_durably_quarantined()
    {
        var repository = new PaymentRepository(_dataSource, new SnowflakeIdGenerator(6));
        var order = await repository.CreateOrderAsync(4, PremiumPlanCatalogue.Get(PremiumPlanCode.Monthly),
            DateTimeOffset.UtcNow.AddMinutes(30), CancellationToken.None);
        await repository.MarkCheckoutPendingAsync(order.Id, "expected-link", "https://pay.payos.vn/example", CancellationToken.None);

        await Assert.ThrowsAsync<PaymentMismatchException>(() => repository.RecordVerifiedPaymentAsync(
            new VerifiedPayment(order.OrderCode, 52_000, "VND", "mismatch-reference", "wrong-link", "00", "Thành công", DateTimeOffset.UtcNow),
            CancellationToken.None));

        var stored = await repository.GetOwnedOrderAsync(4, order.OrderCode, CancellationToken.None);
        Assert.Equal(PaymentOrderStatus.Failed, stored?.Status);
    }

    [Fact]
    public async Task Reconciliation_can_cancel_only_a_created_or_pending_order()
    {
        var repository = new PaymentRepository(_dataSource, new SnowflakeIdGenerator(7));
        var order = await repository.CreateOrderAsync(5, PremiumPlanCatalogue.Get(PremiumPlanCode.Monthly),
            DateTimeOffset.UtcNow.AddMinutes(30), CancellationToken.None);
        await repository.MarkCheckoutPendingAsync(order.Id, "cancel-link", "https://pay.payos.vn/example", CancellationToken.None);

        await repository.MarkOrderCancelledAsync(order.Id, CancellationToken.None);
        await repository.MarkOrderCancelledAsync(order.Id, CancellationToken.None);

        var stored = await repository.GetOwnedOrderAsync(5, order.OrderCode, CancellationToken.None);
        Assert.Equal(PaymentOrderStatus.Cancelled, stored?.Status);
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
