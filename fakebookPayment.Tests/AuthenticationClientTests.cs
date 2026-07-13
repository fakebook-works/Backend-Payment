using System.Net;
using System.Text;
using System.Text.Json;
using Fakebook.Payment.Configuration;
using Fakebook.Payment.Services;
using Microsoft.Extensions.Options;

namespace fakebookPayment.Tests;

public sealed class AuthenticationClientTests
{
    private const string PaymentSecret = "11234567890123456789012345678901";

    [Fact]
    public async Task Sends_large_user_id_as_exact_graphql_id_and_payment_secret()
    {
        const long userId = 9_007_199_254_740_990;
        var handler = new RecordingHandler("""{"data":{"paymentPremiumState":{"userId":"9007199254740990","validDate":null}}}""");
        var client = Create(handler);

        Assert.Null(await client.GetValidDateAsync(userId, CancellationToken.None));

        Assert.Equal(PaymentSecret, handler.PaymentSecret);
        using var request = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(userId.ToString(), request.RootElement.GetProperty("variables").GetProperty("userId").GetString());
    }

    [Fact]
    public async Task Valid_date_update_requires_authentication_to_return_at_least_the_target()
    {
        var target = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
        var handler = new RecordingHandler("""{"data":{"setPaymentValidDate":{"userId":"42","validDate":"2026-08-13T12:00:00Z"}}}""");
        var client = Create(handler);

        await client.SetValidDateAsync(42, target, CancellationToken.None);

        using var request = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("42", request.RootElement.GetProperty("variables").GetProperty("input").GetProperty("userId").GetString());
        Assert.Equal(target, request.RootElement.GetProperty("variables").GetProperty("input").GetProperty("validDate").GetDateTimeOffset());
    }

    private static AuthenticationClient Create(HttpMessageHandler handler) => new(
        new HttpClient(handler),
        Options.Create(new AuthenticationOptions
        {
            Endpoint = "http://authentication.internal/graphql",
            PaymentSecret = PaymentSecret
        }));

    private sealed class RecordingHandler(string responseJson) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }
        public string? PaymentSecret { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            PaymentSecret = request.Headers.GetValues("X-Payment-Secret").Single();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        }
    }
}
