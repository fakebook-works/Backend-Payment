using Fakebook.Payment.Models;

namespace Fakebook.Payment.Services;

public static class PremiumValidityCalculator
{
    public static DateTimeOffset Calculate(DateTimeOffset now, DateTimeOffset? currentValidDate, PremiumPlanCode plan)
    {
        var baseDate = currentValidDate > now ? currentValidDate.Value : now;
        return baseDate.AddMonths(PremiumPlanCatalogue.Get(plan).Months);
    }
}
