using Fakebook.Payment.Repositories;
using Fakebook.Payment.Services;
using Fakebook.Payment.Models;

namespace Fakebook.Payment.Workers;

public sealed class PremiumActivationWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<PremiumActivationWorker> logger,
    TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<IPaymentRepository>();
                var authentication = scope.ServiceProvider.GetRequiredService<IAuthenticationClient>();
                var message = await repository.LeaseNextOutboxAsync(stoppingToken);
                if (message is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }

                await using var userLock = await repository.TryAcquireUserLockAsync(message.UserId, stoppingToken);
                if (userLock is null) continue;

                try
                {
                    var targetValidDate = message.TargetValidDate;
                    if (targetValidDate is null)
                    {
                        var currentValidDate = await authentication.GetValidDateAsync(message.UserId, stoppingToken);
                        targetValidDate = await repository.SetActivationTargetAsync(message,
                            PremiumValidityCalculator.Calculate(timeProvider.GetUtcNow(), currentValidDate, message.Plan), stoppingToken);
                    }
                    await authentication.SetValidDateAsync(message.UserId, targetValidDate.Value, stoppingToken);
                    await repository.CompleteActivationAsync(message, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    var delaySeconds = Math.Min(300, Math.Pow(2, Math.Min(message.AttemptCount + 1, 8)));
                    await repository.RetryActivationAsync(message, exception.GetType().Name,
                        TimeSpan.FromSeconds(delaySeconds), stoppingToken);
                    logger.LogWarning("Premium activation delivery failed; it will be retried");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogError(exception, "Premium activation worker loop failed");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
