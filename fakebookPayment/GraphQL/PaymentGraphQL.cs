using Fakebook.Payment.Models;
using Fakebook.Payment.Security;
using Fakebook.Payment.Services;
using HotChocolate;

namespace Fakebook.Payment.GraphQL;

public sealed record PremiumOrderView(
    [property: ID] string OrderCode,
    PremiumPlanCode Plan,
    int Amount,
    PaymentOrderStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? PaidAt,
    DateTimeOffset? TargetValidDate);

public sealed record PremiumPlanOffer(PremiumPlanCode Code, int Amount, int DurationMonths);

public sealed class Query
{
    public IReadOnlyCollection<PremiumPlanOffer> PremiumPlans([Service] IGatewayRequestContextAccessor gateway)
    {
        try
        {
            gateway.EnsureTrustedGateway();
            return PremiumPlanCatalogue.All.Select(static plan =>
                new PremiumPlanOffer(plan.Code, plan.Amount, plan.Months)).ToArray();
        }
        catch (UnauthorizedAccessException) { throw Unauthorized(); }
    }

    public async Task<PremiumOrderView> PremiumOrder(
        [ID] string orderCode,
        [Service] IGatewayRequestContextAccessor gateway,
        [Service] PremiumPaymentService payments,
        CancellationToken cancellationToken)
    {
        try
        {
            var order = await payments.GetOrderAsync(gateway.GetRequired().UserId, orderCode, cancellationToken);
            return Map(order);
        }
        catch (PremiumPaymentException exception) { throw ToGraphQL(exception); }
        catch (UnauthorizedAccessException) { throw Unauthorized(); }
    }

    private static PremiumOrderView Map(PaymentOrder order) => new(
        order.OrderCode.ToString(), order.Plan, checked((int)order.Amount), order.Status,
        order.CreatedAt, order.ExpiresAt, order.PaidAt, order.TargetValidDate);
    private static GraphQLException ToGraphQL(PremiumPaymentException exception) => new(
        ErrorBuilder.New().SetMessage(exception.Message).SetCode(exception.Code).Build());
    private static GraphQLException Unauthorized() => new(
        ErrorBuilder.New().SetMessage("Unauthorized.").SetCode("UNAUTHENTICATED").Build());
}

public sealed class Mutation
{
    public async Task<PremiumOrderView> ReconcilePremiumCheckout(
        [ID] string orderCode,
        [Service] IGatewayRequestContextAccessor gateway,
        [Service] PremiumPaymentService payments,
        CancellationToken cancellationToken)
    {
        try
        {
            var order = await payments.ReconcileCheckoutAsync(gateway.GetRequired().UserId, orderCode, cancellationToken);
            return new PremiumOrderView(order.OrderCode.ToString(), order.Plan, checked((int)order.Amount), order.Status,
                order.CreatedAt, order.ExpiresAt, order.PaidAt, order.TargetValidDate);
        }
        catch (PremiumPaymentException exception)
        {
            throw new GraphQLException(ErrorBuilder.New().SetMessage(exception.Message).SetCode(exception.Code).Build());
        }
        catch (UnauthorizedAccessException)
        {
            throw new GraphQLException(ErrorBuilder.New().SetMessage("Unauthorized.").SetCode("UNAUTHENTICATED").Build());
        }
    }

    public async Task<PremiumCheckoutPayload> CreatePremiumCheckout(
        CreatePremiumCheckoutInput input,
        [Service] IGatewayRequestContextAccessor gateway,
        [Service] PremiumPaymentService payments,
        CancellationToken cancellationToken)
    {
        try
        {
            return await payments.CreateCheckoutAsync(gateway.GetRequired().UserId, input.Plan, cancellationToken);
        }
        catch (PremiumPaymentException exception)
        {
            throw new GraphQLException(ErrorBuilder.New().SetMessage(exception.Message).SetCode(exception.Code).Build());
        }
        catch (UnauthorizedAccessException)
        {
            throw new GraphQLException(ErrorBuilder.New().SetMessage("Unauthorized.").SetCode("UNAUTHENTICATED").Build());
        }
    }
}
