using System.Net;
using System.Text.Json;
using Fakebook.Payment.Configuration;
using Fakebook.Payment.Services;
using Microsoft.Extensions.Options;

namespace fakebookPayment.Tests;

public sealed class SocialGraphPremiumClientTests
{
    private const string InternalSecret = "21234567890123456789012345678901";

    [Fact]
    public async Task Sends_exact_user_id_expiry_and_target_service_secret()
    {
        const long userId = 9_007_199_254_740_990;
        var target = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
        var handler = new RecordingHandler();
        var client = new SocialGraphPremiumClient(
            new HttpClient(handler),
            Options.Create(new SocialGraphOptions
            {
                BaseUrl = "http://social-graph.internal",
                InternalSecret = InternalSecret
            }));

        await client.SetVerifyUntilAsync(userId, target, CancellationToken.None);

        Assert.Equal(
            $"http://social-graph.internal/internal/users/{userId}/verify",
            handler.Uri?.ToString());
        Assert.Equal(InternalSecret, handler.InternalSecret);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(target, body.RootElement.GetProperty("expiresAt").GetDateTimeOffset());
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? InternalSecret { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            InternalSecret = request.Headers
                .GetValues("X-Internal-SocialGraphService-Secret")
                .Single();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
