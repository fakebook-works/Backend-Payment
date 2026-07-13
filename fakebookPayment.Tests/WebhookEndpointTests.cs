using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;

namespace fakebookPayment.Tests;

public sealed class WebhookEndpointTests : IAsyncLifetime
{
    private const string GatewaySecret = "01234567890123456789012345678901";
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("webhook_tests").WithUsername("postgres").WithPassword("postgres").Build();
    private WebApplicationFactory<global::Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
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
                    ["Payment:PublicBaseUrl"] = "http://localhost:3000",
                    ["Payment:FrontendPublicUrl"] = "http://localhost:3000"
                })));
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

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
