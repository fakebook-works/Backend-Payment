using Fakebook.Payment.GraphQL;
using Microsoft.Extensions.DependencyInjection;

namespace fakebookPayment.Tests;

public sealed class PaymentSchemaTests
{
    [Fact]
    public async Task Public_schema_contains_expected_operations_without_secrets()
    {
        var services = new ServiceCollection();
        services.AddGraphQLServer("Payment").AddQueryType<Query>().AddMutationType<Mutation>();
        await using var provider = services.BuildServiceProvider();
        var resolver = provider.GetRequiredService<HotChocolate.Execution.IRequestExecutorProvider>();
        var executor = await resolver.GetExecutorAsync("Payment");
        var schema = executor.Schema.ToString();
        Assert.Contains("premiumPlans", schema);
        Assert.Contains("premiumOrder", schema);
        Assert.Contains("createPremiumCheckout", schema);
        Assert.Contains("durationMonths: Int!", schema);
        Assert.Contains("orderCode: ID!", schema);
        Assert.Contains("enum PremiumPlan", schema);
        Assert.DoesNotContain("enum PremiumPlanCode", schema);
        Assert.DoesNotContain("checkoutUrl: String\n", schema);
        Assert.DoesNotContain("currency: String!", schema);
        Assert.DoesNotContain("clientId", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("checksumKey", schema, StringComparison.OrdinalIgnoreCase);
    }
}
