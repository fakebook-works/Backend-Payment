using Fakebook.Payment.Configuration;
using Fakebook.Payment.Models;
using Fakebook.Payment.Repositories;
using Microsoft.Extensions.Options;

namespace Fakebook.Payment.Services;

public sealed class PremiumPaymentService(
    IPaymentRepository repository,
    IPayOSPaymentProvider payOS,
    IAuthenticationClient authentication,
    IOptions<PaymentOptions> options,
    TimeProvider timeProvider)
{
    public async Task<PremiumCheckoutPayload> CreateCheckoutAsync(long userId, PremiumPlanCode planCode, CancellationToken ct)
    {
        if (!options.Value.PaymentsEnabled)
            throw new PremiumPaymentException("PAYMENT_CONFIGURATION_ERROR", "Thanh toán Premium hiện chưa được bật.");
        var now = timeProvider.GetUtcNow();
        var validDate = await authentication.GetValidDateAsync(userId, ct);
        if (validDate > now) throw new PremiumPaymentException("PREMIUM_ALREADY_ACTIVE", "Tài khoản Premium vẫn còn hiệu lực.");

        await repository.ExpireStaleOrdersAsync(userId, ct);
        var existing = await repository.GetUnfinishedOrderAsync(userId, ct);
        if (existing is not null)
        {
            if (existing.Status == PaymentOrderStatus.Pending && existing.CheckoutUrl is not null && existing.ExpiresAt > now)
                return ToPayload(existing);
            throw new PremiumPaymentException("PREMIUM_ORDER_PENDING", "Một giao dịch Premium đang được xử lý.");
        }

        var plan = PremiumPlanCatalogue.Get(planCode);
        PaymentOrder order;
        try
        {
            order = await repository.CreateOrderAsync(userId, plan, now.AddMinutes(options.Value.CheckoutTtlMinutes), ct);
        }
        catch (Npgsql.PostgresException exception) when (exception.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation)
        {
            throw new PremiumPaymentException("PREMIUM_ORDER_PENDING", "Một giao dịch Premium đang được xử lý.");
        }

        try
        {
            var checkout = await payOS.CreateCheckoutAsync(order, ct);
            await repository.MarkCheckoutPendingAsync(order.Id, checkout.PaymentLinkId, checkout.CheckoutUrl, ct);
            return new(order.OrderCode.ToString(), PaymentOrderStatus.Pending, checkout.CheckoutUrl);
        }
        catch (OperationCanceledException)
        {
            await repository.MarkOrderFailedAsync(order.Id, CancellationToken.None);
            throw;
        }
        catch
        {
            await repository.MarkOrderFailedAsync(order.Id, CancellationToken.None);
            throw new PremiumPaymentException("PAYMENT_PROVIDER_UNAVAILABLE", "Không thể khởi tạo thanh toán lúc này.");
        }
    }

    public async Task<PaymentOrder> GetOrderAsync(long userId, string orderCode, CancellationToken ct)
    {
        if (!long.TryParse(orderCode, out var parsed) || parsed is < 1 or > 9_007_199_254_740_991)
            throw new PremiumPaymentException("PREMIUM_ORDER_NOT_FOUND", "Không tìm thấy giao dịch Premium.");
        return await repository.GetOwnedOrderAsync(userId, parsed, ct) ??
            throw new PremiumPaymentException("PREMIUM_ORDER_NOT_FOUND", "Không tìm thấy giao dịch Premium.");
    }

    public async Task<PaymentOrder> ReconcileCheckoutAsync(long userId, string orderCode, CancellationToken ct)
    {
        var order = await GetOrderAsync(userId, orderCode, ct);
        if (order.Status is PaymentOrderStatus.Cancelled or PaymentOrderStatus.Expired or PaymentOrderStatus.Failed or
            PaymentOrderStatus.Paid or PaymentOrderStatus.ActivationPending or PaymentOrderStatus.Activated)
            return order;
        if (string.IsNullOrWhiteSpace(order.ProviderPaymentLinkId))
            throw new PremiumPaymentException("PAYMENT_PROVIDER_UNAVAILABLE", "Chưa thể đối soát giao dịch lúc này.");

        ProviderPaymentLink paymentLink;
        try
        {
            paymentLink = await payOS.GetPaymentLinkAsync(order.OrderCode, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            throw new PremiumPaymentException("PAYMENT_PROVIDER_UNAVAILABLE", "Chưa thể đối soát giao dịch lúc này.");
        }

        if (paymentLink.OrderCode != order.OrderCode || paymentLink.Amount != order.Amount ||
            !string.Equals(paymentLink.PaymentLinkId, order.ProviderPaymentLinkId, StringComparison.Ordinal))
            throw new PremiumPaymentException("PAYMENT_PROVIDER_INVALID_RESPONSE", "Thông tin giao dịch không khớp.");

        if (paymentLink.Status == ProviderPaymentLinkStatus.Cancelled)
        {
            await repository.MarkOrderCancelledAsync(order.Id, ct);
            return await repository.GetOwnedOrderAsync(userId, order.OrderCode, ct) ??
                throw new PremiumPaymentException("PREMIUM_ORDER_NOT_FOUND", "Không tìm thấy giao dịch Premium.");
        }

        return order;
    }

    private static PremiumCheckoutPayload ToPayload(PaymentOrder order) =>
        new(order.OrderCode.ToString(), order.Status, order.CheckoutUrl!);
}

public sealed class PremiumPaymentException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
