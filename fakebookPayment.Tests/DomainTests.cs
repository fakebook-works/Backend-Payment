using Fakebook.Payment.Models;
using Fakebook.Payment.Security;
using Fakebook.Payment.Services;

namespace fakebookPayment.Tests;

public sealed class DomainTests
{
    [Fact]
    public void Premium_plan_catalogue_has_server_owned_prices()
    {
        var monthly = PremiumPlanCatalogue.Get(PremiumPlanCode.Monthly);
        var yearly = PremiumPlanCatalogue.Get(PremiumPlanCode.Yearly);
        Assert.Equal(52_000, monthly.Amount);
        Assert.Equal(1, monthly.Months);
        Assert.Equal(500_000, yearly.Amount);
        Assert.Equal(12, yearly.Months);
        Assert.All(PremiumPlanCatalogue.All, plan => Assert.Equal("VND", plan.Currency));
    }

    [Fact]
    public void Shared_secret_comparison_rejects_missing_wrong_and_different_length_values()
    {
        const string expected = "01234567890123456789012345678901";
        Assert.False(SecretComparer.FixedTimeEquals(null, expected));
        Assert.False(SecretComparer.FixedTimeEquals("wrong", expected));
        Assert.False(SecretComparer.FixedTimeEquals("11234567890123456789012345678901", expected));
        Assert.True(SecretComparer.FixedTimeEquals(expected, expected));
    }

    [Fact]
    public void Snowflake_generator_returns_unique_positive_ids()
    {
        var generator = new SnowflakeIdGenerator(7);
        var ids = Enumerable.Range(0, 10_000).Select(_ => generator.NextId()).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.All(ids, id => Assert.True(id > 0));
    }

    [Fact]
    public void Validity_starts_from_now_when_expired_and_stacks_when_still_active()
    {
        var now = new DateTimeOffset(2026, 1, 31, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(now.AddMonths(1), PremiumValidityCalculator.Calculate(now, now.AddDays(-1), PremiumPlanCode.Monthly));
        Assert.Equal(now.AddMonths(13), PremiumValidityCalculator.Calculate(now, now.AddMonths(1), PremiumPlanCode.Yearly));
    }
}
