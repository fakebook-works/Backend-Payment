using System.Net.Http.Json;
using Fakebook.Payment.Configuration;
using Microsoft.Extensions.Options;

namespace Fakebook.Payment.Services;

public interface ISocialGraphPremiumClient
{
    Task SetVerifyUntilAsync(long userId, DateTimeOffset validDate, CancellationToken cancellationToken);
}

public sealed class SocialGraphPremiumClient(
    HttpClient httpClient,
    IOptions<SocialGraphOptions> options) : ISocialGraphPremiumClient
{
    public async Task SetVerifyUntilAsync(
        long userId,
        DateTimeOffset validDate,
        CancellationToken cancellationToken)
    {
        if (userId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userId));
        }

        var configured = options.Value;
        var endpoint = $"{configured.BaseUrl.TrimEnd('/')}/internal/users/{userId}/verify";
        using var request = new HttpRequestMessage(HttpMethod.Put, endpoint)
        {
            Content = JsonContent.Create(new { expiresAt = validDate.ToUniversalTime() })
        };
        request.Headers.TryAddWithoutValidation(
            "X-Internal-SocialGraphService-Secret",
            configured.InternalSecret);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
