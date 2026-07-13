namespace Fakebook.Payment.Models;

[HotChocolate.GraphQLName("PremiumPlan")]
public enum PremiumPlanCode { Monthly, Yearly }

public sealed record PremiumPlan(PremiumPlanCode Code, string Name, int Amount, int Months, string Currency = "VND");

public static class PremiumPlanCatalogue
{
    private static readonly IReadOnlyDictionary<PremiumPlanCode, PremiumPlan> Plans =
        new Dictionary<PremiumPlanCode, PremiumPlan>
        {
            [PremiumPlanCode.Monthly] = new(PremiumPlanCode.Monthly, "Premium tháng", 52_000, 1),
            [PremiumPlanCode.Yearly] = new(PremiumPlanCode.Yearly, "Premium năm", 500_000, 12)
        };

    public static IReadOnlyCollection<PremiumPlan> All { get; } = Plans.Values.ToArray();
    public static PremiumPlan Get(PremiumPlanCode code) => Plans.TryGetValue(code, out var plan)
        ? plan
        : throw new ArgumentOutOfRangeException(nameof(code), "Gói Premium không hợp lệ.");
}
