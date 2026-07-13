using Fakebook.Payment.Configuration;

namespace fakebookPayment.Tests;

public sealed class ConfigurationDefaultsTests
{
    [Fact]
    public void Payments_are_disabled_by_default_for_safe_rollout()
    {
        Assert.False(new PaymentOptions().PaymentsEnabled);
    }
}
