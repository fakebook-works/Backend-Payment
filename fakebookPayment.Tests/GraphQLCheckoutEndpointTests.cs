using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Fakebook.Payment.Models;
using Fakebook.Payment.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace fakebookPayment.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class GraphQLCheckoutEndpointTests : IAsyncLifetime
{
    private const string GatewaySecret = "01234567890123456789012345678901";
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("graphql_checkout_tests").WithUsername("postgres").WithPassword("postgres").Build();
    private readonly FakeProvider _provider = new();
    private WebApplicationFactory<global::Program> _factory = null!;
    private HttpClient _client = null!;

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
                    ["PayOS:ChecksumKey"] = "test-checksum-key",
                    ["Gateway:SharedSecret"] = GatewaySecret,
                    ["Authentication:Endpoint"] = "http://localhost:59999/graphql",
                    ["Authentication:PaymentSecret"] = "11234567890123456789012345678901",
                    ["Payment:PublicBaseUrl"] = "http://localhost:5016",
                    ["Payment:FrontendPublicUrl"] = "http://localhost:3000",
                    ["Payment:PaymentsEnabled"] = "true"
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPayOSPaymentProvider>();
                services.RemoveAll<IAuthenticationClient>();
                services.AddSingleton<IPayOSPaymentProvider>(_provider);
                services.AddSingleton<IAuthenticationClient, InactiveAuthentication>();
            });
        });
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task Trusted_gateway_checkout_creates_server_authoritative_yearly_order()
    {
        using var request = CreateGraphQLRequest(GatewaySecret, "42");
        using var response = await _client.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var checkout = body.RootElement.GetProperty("data").GetProperty("createPremiumCheckout");
        Assert.Equal("PENDING", checkout.GetProperty("status").GetString());
        Assert.Equal("https://pay.payos.vn/checkout-test", checkout.GetProperty("checkoutUrl").GetString());
        Assert.Equal(500_000, _provider.ReceivedOrder?.Amount);
        Assert.Equal(42, _provider.ReceivedOrder?.UserId);

        await using var connection = await _factory.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        Assert.Equal(500_000, await connection.ExecuteScalarAsync<long>("SELECT amount FROM payment.payment_order WHERE user_id=42"));
        Assert.Equal("YEARLY", await connection.ExecuteScalarAsync<string>("SELECT plan FROM payment.payment_order WHERE user_id=42"));
        Assert.Equal("PENDING", await connection.ExecuteScalarAsync<string>("SELECT status FROM payment.payment_order WHERE user_id=42"));
    }

    [Fact]
    public async Task Spoofed_gateway_headers_cannot_create_an_order()
    {
        using var request = CreateGraphQLRequest("wrong-secret", "42");
        using var response = await _client.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());

        var errorCode = body.RootElement.GetProperty("errors")[0].GetProperty("extensions").GetProperty("code").GetString();
        Assert.Equal("UNAUTHENTICATED", errorCode);
        await using var connection = await _factory.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM payment.payment_order"));
    }

    [Fact]
    public async Task Order_code_round_trips_as_exact_graphql_id_at_javascript_limit()
    {
        const long orderCode = 9_007_199_254_740_991;
        const long userId = 9_007_199_254_740_990;
        await using (var connection = await _factory.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync())
        {
            await connection.ExecuteAsync("""
                INSERT INTO payment.payment_order
                  (id, order_code, user_id, plan, amount, currency, status, provider_payment_link_id, expires_at)
                VALUES (200, @OrderCode, @UserId, 'MONTHLY', 52000, 'VND', 'PENDING', 'precision-link', now() + interval '30 minutes');
                """, new { OrderCode = orderCode, UserId = userId });
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql")
        {
            Content = JsonContent.Create(new
            {
                query = "query($orderCode: ID!) { premiumOrder(orderCode: $orderCode) { orderCode amount status } }",
                variables = new { orderCode = orderCode.ToString() }
            })
        };
        request.Headers.Add("X-Gateway-Secret", GatewaySecret);
        request.Headers.Add("X-User-Id", userId.ToString());

        using var response = await _client.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());

        var order = body.RootElement.GetProperty("data").GetProperty("premiumOrder");
        Assert.Equal(orderCode.ToString(), order.GetProperty("orderCode").GetString());
        Assert.Equal(52_000, order.GetProperty("amount").GetInt32());
        Assert.Equal("PENDING", order.GetProperty("status").GetString());
    }

    private static HttpRequestMessage CreateGraphQLRequest(string secret, string userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/graphql")
        {
            Content = JsonContent.Create(new
            {
                query = "mutation { createPremiumCheckout(input: { plan: YEARLY }) { orderCode status checkoutUrl } }"
            })
        };
        request.Headers.Add("X-Gateway-Secret", secret);
        request.Headers.Add("X-User-Id", userId);
        return request;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private sealed class FakeProvider : IPayOSPaymentProvider
    {
        public PaymentOrder? ReceivedOrder { get; private set; }
        public Task<ProviderCheckout> CreateCheckoutAsync(PaymentOrder order, CancellationToken cancellationToken)
        {
            ReceivedOrder = order;
            return Task.FromResult(new ProviderCheckout("checkout-link", "https://pay.payos.vn/checkout-test"));
        }
        public Task<VerifiedPayment> VerifyWebhookAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class InactiveAuthentication : IAuthenticationClient
    {
        public Task<DateTimeOffset?> GetValidDateAsync(long userId, CancellationToken cancellationToken) => Task.FromResult<DateTimeOffset?>(null);
        public Task SetValidDateAsync(long userId, DateTimeOffset validDate, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
