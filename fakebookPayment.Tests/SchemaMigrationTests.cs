using Dapper;
using Npgsql;
using Testcontainers.PostgreSql;

namespace fakebookPayment.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class SchemaMigrationTests
{
    [Fact]
    public async Task Current_schema_reconciles_pre_release_draft_tables()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("migration_tests").WithUsername("postgres").WithPassword("postgres").Build();
        await postgres.StartAsync();
        await using var dataSource = NpgsqlDataSource.Create(postgres.GetConnectionString());
        await using var connection = await dataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(LegacySchema);
        await connection.ExecuteAsync("""
            INSERT INTO payment.payment_order
              (id, user_id, plan, amount, currency, status, expires_at)
            VALUES (1, '42', 'MONTHLY', 52000, 'VND', 'PAID', now());
            INSERT INTO payment.payment_transaction
              (id, order_id, provider_reference, provider_payment_link_id, amount, currency, paid_at)
            VALUES
              (10, 1, 'legacy-reference-1', 'legacy-link-1', 52000, 'VND', now() - interval '1 second'),
              (11, 1, 'legacy-reference-2', 'legacy-link-2', 52000, 'VND', now());
            """);

        var schemaPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fakebookPayment", "schema.sql"));
        await connection.ExecuteAsync(await File.ReadAllTextAsync(schemaPath));

        var userType = await connection.ExecuteScalarAsync<string>("""
            SELECT data_type FROM information_schema.columns
            WHERE table_schema = 'payment' AND table_name = 'payment_order' AND column_name = 'user_id';
            """);
        var targetNullable = await connection.ExecuteScalarAsync<string>("""
            SELECT is_nullable FROM information_schema.columns
            WHERE table_schema = 'payment' AND table_name = 'outbox_message' AND column_name = 'target_valid_date';
            """);
        Assert.Equal("bigint", userType);
        Assert.Equal("YES", targetNullable);
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM pg_indexes WHERE schemaname='payment' AND indexname='ux_payment_order_user_unfinished'"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM information_schema.columns WHERE table_schema='payment' AND table_name='payment_transaction' AND column_name='provider_code'"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM information_schema.columns WHERE table_schema='payment' AND table_name='outbox_message' AND column_name='event_key'"));
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM payment.payment_transaction WHERE order_id = 1"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM payment.payment_transaction WHERE order_id = 1 AND is_canonical"));
        Assert.Equal(4, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM pg_constraint WHERE conrelid='payment.payment_order'::regclass AND conname IN ('ck_payment_order_plan','ck_payment_order_amount','ck_payment_order_currency','ck_payment_order_status')"));
    }

    private const string LegacySchema = """
        CREATE SCHEMA payment;
        CREATE SEQUENCE payment.order_code_seq AS bigint;
        CREATE TABLE payment.payment_order (
          id bigint PRIMARY KEY, order_code bigint NOT NULL DEFAULT nextval('payment.order_code_seq') UNIQUE,
          user_id text NOT NULL, plan text NOT NULL, amount bigint NOT NULL, currency varchar(3) NOT NULL,
          status text NOT NULL, provider_payment_link_id text, checkout_url text, expires_at timestamptz NOT NULL,
          paid_at timestamptz, activated_at timestamptz, target_valid_date timestamptz,
          created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now());
        CREATE UNIQUE INDEX ux_payment_order_user_unfinished ON payment.payment_order(user_id)
          WHERE status IN ('CREATED','PENDING','PAID','ACTIVATION_PENDING');
        CREATE TABLE payment.payment_transaction (
          id bigint PRIMARY KEY, order_id bigint NOT NULL REFERENCES payment.payment_order(id),
          provider_reference text NOT NULL UNIQUE, provider_payment_link_id text NOT NULL,
          amount bigint NOT NULL, currency varchar(3) NOT NULL, paid_at timestamptz NOT NULL,
          created_at timestamptz NOT NULL DEFAULT now());
        CREATE TABLE payment.outbox_message (
          id bigint PRIMARY KEY, order_id bigint NOT NULL UNIQUE REFERENCES payment.payment_order(id),
          user_id text NOT NULL, target_valid_date timestamptz NOT NULL, attempt_count integer NOT NULL DEFAULT 0,
          next_attempt_at timestamptz NOT NULL DEFAULT now(), processed_at timestamptz, last_error_code text,
          created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now());
        """;
}
