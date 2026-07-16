using Fakebook.Payment.Services;

internal sealed class TestSocialGraphPremiumClient : ISocialGraphPremiumClient
{
    public List<(long UserId, DateTimeOffset ValidDate)> Calls { get; } = [];

    public Task SetVerifyUntilAsync(
        long userId,
        DateTimeOffset validDate,
        CancellationToken cancellationToken)
    {
        Calls.Add((userId, validDate));
        return Task.CompletedTask;
    }
}
