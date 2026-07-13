using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Fakebook.Payment.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using PayOS;
using PayOS.Models.Webhooks;
using Testcontainers.PostgreSql;

namespace fakebookPayment.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class WebhookEndpointTests : IAsyncLifetime
{
    private const string GatewaySecret = "01234567890123456789012345678901";
    private const string ChecksumKey = "test-checksum-key";
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("webhook_tests").WithUsername("postgres").WithPassword("postgres").Build();
    private WebApplicationFactory<global::Program> _factory = null!;
    private HttpClient _client = null!;
    private readonly FakeAuthenticationClient _authentication = new();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:PaymentDatabase"] = _postgres.GetConnectionString(),
                    ["PayOS:ClientId"] = "test-client",
                    ["PayOS:ApiKey"] = "test-api-key",
                    ["PayOS:ChecksumKey"] = ChecksumKey,
                    ["Gateway:SharedSecret"] = GatewaySecret,
                    ["Authentication:Endpoint"] = "http://localhost:59999/graphql",
                    ["Authentication:PaymentSecret"] = "11234567890123456789012345678901",
                    ["Payment:PublicBaseUrl"] = "http://localhost:3000",
                    ["Payment:FrontendPublicUrl"] = "http://localhost:3000"
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAuthenticationClient>();
                services.AddSingleton<IAuthenticationClient>(_authentication);
            });
        });
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task Webhook_requires_gateway_secret_before_reading_body()
    {
        using var response = await _client.PostAsync("/internal/webhooks/payos",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_rejects_non_json_content()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/webhooks/payos")
        {
            Content = new StringContent("not-json")
        };
        request.Headers.Add("X-Gateway-Secret", GatewaySecret);
        using var response = await _client.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_does_not_accept_jsonp_as_json()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/webhooks/payos")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/jsonp")
        };
        request.Headers.Add("X-Gateway-Secret", GatewaySecret);
        using var response = await _client.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Valid_sdk_signed_webhook_activates_once_and_replay_is_idempotent()
    {
        const long orderCode = 123456;
        await SeedPendingOrderAsync(orderCode, 77, "payment-link-1", 52_000, "MONTHLY");
        var webhook = await CreateSignedWebhookAsync(orderCode, 52_000, "payment-link-1", "reference-1");

        var responses = await Task.WhenAll(SendWebhookAsync(webhook), SendWebhookAsync(webhook));
        using var first = responses[0];
        using var replay = responses[1];

        Assert.Equal(System.Net.HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, replay.StatusCode);
        await WaitUntilAsync(async () =>
        {
            await using var pollingConnection = await _factory.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
            return await pollingConnection.ExecuteScalarAsync<string>("SELECT status FROM payment.payment_order WHERE id=100") == "ACTIVATED";
        });

        await using var connection = await _factory.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        Assert.Equal(1, _authentication.SetCalls);
        Assert.NotNull(_authentication.LastValidDate);
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM payment.payment_transaction WHERE provider_reference='reference-1'"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM payment.outbox_message WHERE order_id=100"));
        Assert.Equal("ACTIVATED", await connection.ExecuteScalarAsync<string>("SELECT status FROM payment.payment_order WHERE id=100"));
    }

    [Fact]
    public async Task Invalid_payos_signature_changes_no_payment_state()
    {
        const long orderCode = 654321;
        await SeedPendingOrderAsync(orderCode, 88, "payment-link-2", 500_000, "YEARLY");
        var webhook = await CreateSignedWebhookAsync(orderCode, 500_000, "payment-link-2", "reference-2");
        webhook.Signature = new string('0', 64);

        using var response = await SendWebhookAsync(webhook);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        await using var connection = await _factory.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM payment.payment_transaction WHERE provider_reference='reference-2'"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM payment.outbox_message WHERE order_id=100"));
        Assert.Equal("PENDING", await connection.ExecuteScalarAsync<string>("SELECT status FROM payment.payment_order WHERE id=100"));
        Assert.Equal(0, _authentication.SetCalls);
    }

    [Fact]
    public async Task Outbox_insert_failure_rolls_back_transaction_and_order_transition()
    {
        const long orderCode = 777777;
        await SeedPendingOrderAsync(orderCode, 99, "payment-link-3", 52_000, "MONTHLY");
        await using (var setup = await _factory.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync())
        {
            await setup.ExecuteAsync("""
                CREATE FUNCTION payment.reject_outbox_insert() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'injected outbox failure'; END $$;
                CREATE TRIGGER reject_outbox_insert BEFORE INSERT ON payment.outbox_message
                FOR EACH ROW EXECUTE FUNCTION payment.reject_outbox_insert();
                """);
        }
        var webhook = await CreateSignedWebhookAsync(orderCode, 52_000, "payment-link-3", "reference-3");

        using var response = await SendWebhookAsync(webhook);

        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await using var connection = await _factory.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM payment.payment_transaction WHERE provider_reference='reference-3'"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM payment.outbox_message WHERE order_id=100"));
        Assert.Equal("PENDING", await connection.ExecuteScalarAsync<string>("SELECT status FROM payment.payment_order WHERE id=100"));
        Assert.Equal(0, _authentication.SetCalls);
    }

    private async Task SeedPendingOrderAsync(long orderCode, long userId, string paymentLinkId, long amount, string plan)
    {
        await using var connection = await _factory.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        await connection.ExecuteAsync("""
            INSERT INTO payment.payment_order
              (id, order_code, user_id, plan, amount, currency, status, provider_payment_link_id, checkout_url, expires_at)
            VALUES (100, @OrderCode, @UserId, @Plan, @Amount, 'VND', 'PENDING', @PaymentLinkId,
                    'https://pay.payos.vn/test', now() + interval '30 minutes');
            """, new { OrderCode = orderCode, UserId = userId, Plan = plan, Amount = amount, PaymentLinkId = paymentLinkId });
    }

    private static Task<Webhook> CreateSignedWebhookAsync(long orderCode, long amount, string paymentLinkId, string reference)
    {
        var data = new WebhookData
        {
            OrderCode = orderCode,
            Amount = amount,
            Description = $"FB PRM {orderCode}",
            AccountNumber = "123456789",
            Reference = reference,
            TransactionDateTime = "2026-07-13 12:00:00",
            Currency = "VND",
            PaymentLinkId = paymentLinkId,
            Code = "00",
            Description2 = "Thành công",
            CounterAccountBankId = string.Empty,
            CounterAccountBankName = string.Empty,
            CounterAccountName = string.Empty,
            CounterAccountNumber = string.Empty,
            VirtualAccountName = string.Empty,
            VirtualAccountNumber = string.Empty
        };
        var client = new PayOSClient(new global::PayOS.PayOSOptions
        {
            ClientId = "test-client",
            ApiKey = "test-api-key",
            ChecksumKey = ChecksumKey
        });
        return Task.FromResult(new Webhook
        {
            Code = "00",
            Description = "success",
            Success = true,
            Data = data,
            Signature = client.Crypto.CreateSignatureFromObject(data, ChecksumKey) ??
                        throw new InvalidOperationException("PayOS SDK did not create a test signature.")
        });
    }

    private async Task<HttpResponseMessage> SendWebhookAsync(Webhook webhook)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/webhooks/payos")
        {
            Content = JsonContent.Create(webhook)
        };
        request.Headers.Add("X-Gateway-Secret", GatewaySecret);
        return await _client.SendAsync(request);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("Activation worker did not finish in time.");
            await Task.Delay(100);
        }
    }

    private sealed class FakeAuthenticationClient : IAuthenticationClient
    {
        private int _setCalls;
        public int SetCalls => Volatile.Read(ref _setCalls);
        public DateTimeOffset? LastValidDate { get; private set; }
        public Task<DateTimeOffset?> GetValidDateAsync(long userId, CancellationToken cancellationToken) => Task.FromResult<DateTimeOffset?>(null);
        public Task SetValidDateAsync(long userId, DateTimeOffset validDate, CancellationToken cancellationToken)
        {
            LastValidDate = validDate;
            Interlocked.Increment(ref _setCalls);
            return Task.CompletedTask;
        }
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
