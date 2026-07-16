using System.Net.Http.Json;
using System.Text.Json;
using Fakebook.Payment.Configuration;
using Microsoft.Extensions.Options;

namespace Fakebook.Payment.Services;

public interface IAuthenticationClient
{
    Task<DateTimeOffset?> GetValidDateAsync(long userId, CancellationToken cancellationToken);
    Task<DateTimeOffset> SetValidDateAsync(long userId, DateTimeOffset validDate, CancellationToken cancellationToken);
}

public sealed class AuthenticationClient(HttpClient httpClient, IOptions<AuthenticationOptions> options) : IAuthenticationClient
{
    private const string StateQuery = """
        query PaymentPremiumState($userId: ID!) {
          paymentPremiumState(userId: $userId) { userId validDate }
        }
        """;
    private const string SetMutation = """
        mutation SetPaymentValidDate($input: SetPaymentValidDateInput!) {
          setPaymentValidDate(input: $input) { userId validDate }
        }
        """;

    public async Task<DateTimeOffset?> GetValidDateAsync(long userId, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(StateQuery, new { userId = userId.ToString() }, cancellationToken);
        var state = document.RootElement.GetProperty("data").GetProperty("paymentPremiumState");
        EnsureMatchingUser(state, userId);
        return state.GetProperty("validDate").ValueKind == JsonValueKind.Null
            ? null
            : state.GetProperty("validDate").GetDateTimeOffset();
    }

    public async Task<DateTimeOffset> SetValidDateAsync(
        long userId,
        DateTimeOffset validDate,
        CancellationToken cancellationToken)
    {
        using var document = await SendAsync(SetMutation, new { input = new { userId = userId.ToString(), validDate } }, cancellationToken);
        var state = document.RootElement.GetProperty("data").GetProperty("setPaymentValidDate");
        EnsureMatchingUser(state, userId);
        var storedValidDate = state.GetProperty("validDate").GetDateTimeOffset();
        if (storedValidDate < validDate)
            throw new InvalidOperationException("Authentication did not persist the requested Premium validity.");
        return storedValidDate;
    }

    private async Task<JsonDocument> SendAsync(string query, object variables, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Value.Endpoint)
        {
            Content = JsonContent.Create(new { query, variables })
        };
        request.Headers.Add("X-Payment-Secret", options.Value.PaymentSecret);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(64 * 1024);
        var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (document.RootElement.TryGetProperty("errors", out _))
        {
            document.Dispose();
            throw new InvalidOperationException("Authentication rejected the internal payment operation.");
        }
        return document;
    }

    private static void EnsureMatchingUser(JsonElement state, long expectedUserId)
    {
        var returned = state.GetProperty("userId");
        var returnedUserId = returned.ValueKind == JsonValueKind.String ? returned.GetString() : returned.GetRawText();
        if (!string.Equals(returnedUserId, expectedUserId.ToString(), StringComparison.Ordinal))
            throw new InvalidOperationException("Authentication returned a different user identity.");
    }
}
