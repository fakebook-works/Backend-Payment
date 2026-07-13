using Dapper;
using Fakebook.Payment.Models;
using Fakebook.Payment.Services;
using Npgsql;

namespace Fakebook.Payment.Repositories;

public sealed class OutboxMessage
{
    public long Id { get; init; }
    public long OrderId { get; init; }
    public long UserId { get; init; }
    public PremiumPlanCode Plan { get; init; }
    public DateTimeOffset? TargetValidDate { get; init; }
    public int AttemptCount { get; init; }
}

public sealed class PaymentOrderNotReadyException : Exception;
public sealed class PaymentMismatchException : Exception;

public interface IPaymentRepository
{
    Task<PaymentOrder> CreateOrderAsync(long userId, PremiumPlan plan, DateTimeOffset expiresAt, CancellationToken cancellationToken);
    Task MarkCheckoutPendingAsync(long orderId, string paymentLinkId, string checkoutUrl, CancellationToken cancellationToken);
    Task MarkOrderFailedAsync(long orderId, CancellationToken cancellationToken);
    Task<PaymentOrder?> GetOwnedOrderAsync(long userId, long orderCode, CancellationToken cancellationToken);
    Task<PaymentOrder?> GetUnfinishedOrderAsync(long userId, CancellationToken cancellationToken);
    Task ExpireStaleOrdersAsync(long userId, CancellationToken cancellationToken);
    Task<bool> RecordVerifiedPaymentAsync(VerifiedPayment payment, CancellationToken cancellationToken);
    Task<OutboxMessage?> LeaseNextOutboxAsync(CancellationToken cancellationToken);
    Task<IAsyncDisposable?> TryAcquireUserLockAsync(long userId, CancellationToken cancellationToken);
    Task<DateTimeOffset> SetActivationTargetAsync(OutboxMessage message, DateTimeOffset targetValidDate, CancellationToken cancellationToken);
    Task CompleteActivationAsync(OutboxMessage message, CancellationToken cancellationToken);
    Task RetryActivationAsync(OutboxMessage message, string errorCode, TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class PaymentRepository(NpgsqlDataSource dataSource, IIdGenerator ids) : IPaymentRepository
{
    public async Task<PaymentOrder> CreateOrderAsync(long userId, PremiumPlan plan, DateTimeOffset expiresAt, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO payment.payment_order (id, user_id, plan, amount, currency, status, expires_at)
            VALUES (@Id, @UserId, @Plan, @Amount, @Currency, 'CREATED', @ExpiresAt)
            RETURNING id, order_code AS OrderCode, user_id AS UserId, plan, amount, currency, status,
                      provider_payment_link_id AS ProviderPaymentLinkId, checkout_url AS CheckoutUrl,
                      expires_at AS ExpiresAt, paid_at AS PaidAt, activated_at AS ActivatedAt,
                      target_valid_date AS TargetValidDate, created_at AS CreatedAt, updated_at AS UpdatedAt;
            """;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleAsync<OrderRow>(new CommandDefinition(sql, new
        {
            Id = ids.NextId(), UserId = userId, Plan = ToDb(plan.Code), plan.Amount, plan.Currency, ExpiresAt = expiresAt
        }, cancellationToken: ct));
        return Map(row);
    }

    public async Task MarkCheckoutPendingAsync(long orderId, string paymentLinkId, string checkoutUrl, CancellationToken ct)
    {
        const string sql = """
            UPDATE payment.payment_order SET status = 'PENDING', provider_payment_link_id = @PaymentLinkId,
                checkout_url = @CheckoutUrl, updated_at = now()
            WHERE id = @OrderId AND status = 'CREATED';
            """;
        await ExecuteAsync(sql, new { OrderId = orderId, PaymentLinkId = paymentLinkId, CheckoutUrl = checkoutUrl }, ct);
    }

    public Task MarkOrderFailedAsync(long orderId, CancellationToken ct) => ExecuteAsync(
        "UPDATE payment.payment_order SET status = 'FAILED', updated_at = now() WHERE id = @OrderId AND status = 'CREATED';",
        new { OrderId = orderId }, ct);

    public Task<PaymentOrder?> GetOwnedOrderAsync(long userId, long orderCode, CancellationToken ct) => QueryOrderAsync(
        "SELECT id, order_code AS OrderCode, user_id AS UserId, plan, amount, currency, status, provider_payment_link_id AS ProviderPaymentLinkId, checkout_url AS CheckoutUrl, expires_at AS ExpiresAt, paid_at AS PaidAt, activated_at AS ActivatedAt, target_valid_date AS TargetValidDate, created_at AS CreatedAt, updated_at AS UpdatedAt FROM payment.payment_order WHERE user_id = @UserId AND order_code = @OrderCode;",
        new { UserId = userId, OrderCode = orderCode }, ct);

    public Task<PaymentOrder?> GetUnfinishedOrderAsync(long userId, CancellationToken ct) => QueryOrderAsync(
        "SELECT id, order_code AS OrderCode, user_id AS UserId, plan, amount, currency, status, provider_payment_link_id AS ProviderPaymentLinkId, checkout_url AS CheckoutUrl, expires_at AS ExpiresAt, paid_at AS PaidAt, activated_at AS ActivatedAt, target_valid_date AS TargetValidDate, created_at AS CreatedAt, updated_at AS UpdatedAt FROM payment.payment_order WHERE user_id = @UserId AND status IN ('CREATED','PENDING','PAID','ACTIVATION_PENDING') ORDER BY created_at DESC LIMIT 1;",
        new { UserId = userId }, ct);

    public Task ExpireStaleOrdersAsync(long userId, CancellationToken ct) => ExecuteAsync(
        "UPDATE payment.payment_order SET status = 'EXPIRED', updated_at = now() WHERE user_id = @UserId AND status IN ('CREATED','PENDING') AND expires_at <= now();",
        new { UserId = userId }, ct);

    public async Task<bool> RecordVerifiedPaymentAsync(VerifiedPayment payment, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        const string orderSql = """
            SELECT id, order_code AS OrderCode, user_id AS UserId, plan, amount, currency, status,
                   provider_payment_link_id AS ProviderPaymentLinkId, checkout_url AS CheckoutUrl,
                   expires_at AS ExpiresAt, paid_at AS PaidAt, activated_at AS ActivatedAt,
                   target_valid_date AS TargetValidDate, created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM payment.payment_order WHERE order_code = @OrderCode FOR UPDATE;
            """;
        var row = await connection.QuerySingleOrDefaultAsync<OrderRow>(new CommandDefinition(orderSql,
            new { payment.OrderCode }, transaction, cancellationToken: ct));
        if (row is null) return false;
        if (row.ProviderPaymentLinkId is null && row.Status == "CREATED")
            throw new PaymentOrderNotReadyException();
        if (row.Amount != payment.Amount || row.Currency != payment.Currency ||
            !string.Equals(row.ProviderPaymentLinkId, payment.PaymentLinkId, StringComparison.Ordinal))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE payment.payment_order SET status = 'FAILED', updated_at = now() WHERE id = @Id;",
                new { row.Id }, transaction, cancellationToken: ct));
            await transaction.CommitAsync(ct);
            throw new PaymentMismatchException();
        }

        var inserted = await connection.ExecuteScalarAsync<long?>(new CommandDefinition("""
            INSERT INTO payment.payment_transaction
                (id, order_id, provider_reference, provider_payment_link_id, amount, currency,
                 provider_code, provider_description, paid_at)
            VALUES (@Id, @OrderId, @Reference, @PaymentLinkId, @Amount, @Currency,
                    @ProviderCode, @ProviderDescription, @PaidAt)
            ON CONFLICT DO NOTHING RETURNING id;
            """, new
            {
                Id = ids.NextId(), OrderId = row.Id, payment.Reference, payment.PaymentLinkId, payment.Amount,
                payment.Currency, ProviderCode = payment.ProviderCode[..Math.Min(payment.ProviderCode.Length, 32)],
                ProviderDescription = payment.ProviderDescription[..Math.Min(payment.ProviderDescription.Length, 255)], payment.PaidAt
            },
            transaction, cancellationToken: ct));
        if (inserted is null)
        {
            await transaction.CommitAsync(ct);
            return true;
        }

        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE payment.payment_order SET status = 'PAID', paid_at = @PaidAt, updated_at = now() WHERE id = @OrderId;
            INSERT INTO payment.outbox_message (id, order_id, event_key, event_type, user_id)
            VALUES (@OutboxId, @OrderId, @EventKey, 'ACTIVATE_PREMIUM', @UserId)
            ON CONFLICT (order_id) DO NOTHING;
            """, new { payment.PaidAt, OrderId = row.Id, OutboxId = ids.NextId(), EventKey = $"premium-activation:{row.OrderCode}", row.UserId },
            transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<IAsyncDisposable?> TryAcquireUserLockAsync(long userId, CancellationToken ct)
    {
        var connection = await dataSource.OpenConnectionAsync(ct);
        var acquired = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT pg_try_advisory_lock(@UserId);", new { UserId = userId }, cancellationToken: ct));
        if (acquired) return new AdvisoryLock(connection, userId);
        await connection.DisposeAsync();
        return null;
    }

    public async Task<OutboxMessage?> LeaseNextOutboxAsync(CancellationToken ct)
    {
        const string sql = """
            WITH candidate AS (
                SELECT m.id FROM payment.outbox_message m
                WHERE m.processed_at IS NULL AND m.next_attempt_at <= now()
                  AND NOT EXISTS (
                    SELECT 1 FROM payment.outbox_message earlier
                    WHERE earlier.user_id = m.user_id AND earlier.processed_at IS NULL
                      AND (earlier.created_at, earlier.id) < (m.created_at, m.id)
                  )
                ORDER BY m.created_at, m.id FOR UPDATE SKIP LOCKED LIMIT 1
            )
            UPDATE payment.outbox_message m SET next_attempt_at = now() + interval '1 minute', updated_at = now()
            FROM candidate WHERE m.id = candidate.id
            RETURNING m.id, m.order_id AS OrderId, m.user_id AS UserId,
                      (SELECT plan FROM payment.payment_order WHERE id = m.order_id) AS Plan,
                      m.target_valid_date AS TargetValidDate, m.attempt_count AS AttemptCount;
            """;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<OutboxRow>(new CommandDefinition(sql, cancellationToken: ct));
        return row is null ? null : new OutboxMessage
        {
            Id = row.Id, OrderId = row.OrderId, UserId = row.UserId, Plan = ParsePlan(row.Plan),
            TargetValidDate = row.TargetValidDate, AttemptCount = row.AttemptCount
        };
    }

    public async Task<DateTimeOffset> SetActivationTargetAsync(OutboxMessage message, DateTimeOffset targetValidDate, CancellationToken ct)
    {
        const string outboxSql = """
            UPDATE payment.outbox_message SET target_valid_date = COALESCE(target_valid_date, @TargetValidDate), updated_at = now()
            WHERE id = @Id RETURNING target_valid_date;
            """;
        const string orderSql = """
            UPDATE payment.payment_order SET status = 'ACTIVATION_PENDING',
                target_valid_date = COALESCE(target_valid_date, @TargetValidDate), updated_at = now()
            WHERE id = @OrderId AND status = 'PAID';
            """;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var storedUtc = await connection.ExecuteScalarAsync<DateTime>(new CommandDefinition(outboxSql,
            new { message.Id, message.OrderId, TargetValidDate = targetValidDate }, transaction, cancellationToken: ct));
        var stored = new DateTimeOffset(DateTime.SpecifyKind(storedUtc, DateTimeKind.Utc));
        await connection.ExecuteAsync(new CommandDefinition(orderSql,
            new { message.OrderId, TargetValidDate = stored }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return stored;
    }

    public async Task CompleteActivationAsync(OutboxMessage message, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE payment.outbox_message SET processed_at = now(), updated_at = now(), last_error_code = NULL WHERE id = @Id;
            UPDATE payment.payment_order SET status = 'ACTIVATED', activated_at = now(), updated_at = now() WHERE id = @OrderId;
            """, message, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
    }

    public Task RetryActivationAsync(OutboxMessage message, string errorCode, TimeSpan delay, CancellationToken ct) => ExecuteAsync("""
        UPDATE payment.outbox_message SET attempt_count = attempt_count + 1,
            next_attempt_at = now() + @Delay, last_error_code = @ErrorCode, updated_at = now() WHERE id = @Id;
        """, new { message.Id, Delay = delay, ErrorCode = errorCode[..Math.Min(errorCode.Length, 64)] }, ct);

    private async Task<PaymentOrder?> QueryOrderAsync(string sql, object param, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<OrderRow>(new CommandDefinition(sql, param, cancellationToken: ct));
        return row is null ? null : Map(row);
    }

    private async Task ExecuteAsync(string sql, object param, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(sql, param, cancellationToken: ct));
    }

    private static string ToDb(PremiumPlanCode plan) => plan == PremiumPlanCode.Monthly ? "MONTHLY" : "YEARLY";
    private static PremiumPlanCode ParsePlan(string plan) => plan == "MONTHLY" ? PremiumPlanCode.Monthly : PremiumPlanCode.Yearly;
    private static PaymentOrderStatus ParseStatus(string value) => Enum.Parse<PaymentOrderStatus>(
        string.Concat(value.ToLowerInvariant().Split('_').Select(static x => char.ToUpperInvariant(x[0]) + x[1..])));
    private static PaymentOrder Map(OrderRow row) => new()
    {
        Id = row.Id, OrderCode = row.OrderCode, UserId = row.UserId, Plan = ParsePlan(row.Plan), Amount = row.Amount,
        Currency = row.Currency, Status = ParseStatus(row.Status), ProviderPaymentLinkId = row.ProviderPaymentLinkId,
        CheckoutUrl = row.CheckoutUrl, ExpiresAt = row.ExpiresAt, PaidAt = row.PaidAt, ActivatedAt = row.ActivatedAt,
        TargetValidDate = row.TargetValidDate, CreatedAt = row.CreatedAt, UpdatedAt = row.UpdatedAt
    };

    private sealed class OrderRow
    {
        public long Id { get; init; }
        public long OrderCode { get; init; }
        public long UserId { get; init; }
        public string Plan { get; init; } = string.Empty;
        public long Amount { get; init; }
        public string Currency { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string? ProviderPaymentLinkId { get; init; }
        public string? CheckoutUrl { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
        public DateTimeOffset? PaidAt { get; init; }
        public DateTimeOffset? ActivatedAt { get; init; }
        public DateTimeOffset? TargetValidDate { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
    }

    private sealed class OutboxRow
    {
        public long Id { get; init; }
        public long OrderId { get; init; }
        public long UserId { get; init; }
        public string Plan { get; init; } = string.Empty;
        public DateTimeOffset? TargetValidDate { get; init; }
        public int AttemptCount { get; init; }
    }

    private sealed class AdvisoryLock(NpgsqlConnection connection, long userId) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await connection.ExecuteAsync("SELECT pg_advisory_unlock(@UserId);", new { UserId = userId });
            }
            finally { await connection.DisposeAsync(); }
        }
    }
}
