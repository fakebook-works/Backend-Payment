using Fakebook.Payment.Configuration;
using Fakebook.Payment.Endpoints;
using Fakebook.Payment.GraphQL;
using Fakebook.Payment.Repositories;
using Fakebook.Payment.Security;
using Fakebook.Payment.Services;
using Fakebook.Payment.Workers;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<PaymentOptions>().BindConfiguration(PaymentOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<PayOSOptions>().BindConfiguration(PayOSOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<GatewayOptions>().BindConfiguration(GatewayOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<AuthenticationOptions>().BindConfiguration(AuthenticationOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<NpgsqlOptions>().Configure<IConfiguration>((options, configuration) =>
{
    options.ConnectionString = configuration.GetConnectionString("PaymentDatabase") ?? string.Empty;
}).Validate(static options => !string.IsNullOrWhiteSpace(options.ConnectionString), "PaymentDatabase connection string is required.").ValidateOnStart();

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<NpgsqlDataSource>(services =>
{
    var connectionString = services.GetRequiredService<IOptions<NpgsqlOptions>>().Value.ConnectionString;
    return NpgsqlDataSource.Create(connectionString);
});
builder.Services.AddSingleton<IIdGenerator>(services =>
    new SnowflakeIdGenerator(services.GetRequiredService<IOptions<PaymentOptions>>().Value.WorkerId));
builder.Services.AddScoped<IGatewayRequestContextAccessor, GatewayRequestContextAccessor>();
builder.Services.AddScoped<IPaymentRepository, PaymentRepository>();
builder.Services.AddSingleton<IPayOSPaymentProvider, PayOSPaymentProvider>();
builder.Services.AddHttpClient<IAuthenticationClient, AuthenticationClient>(client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddScoped<PremiumPaymentService>();
builder.Services.AddHostedService<DatabaseInitializer>();
builder.Services.AddHostedService<PremiumActivationWorker>();
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("payos-webhook", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.AddPolicy("graphql", context =>
    {
        var gatewaySecret = context.RequestServices.GetRequiredService<IOptions<GatewayOptions>>().Value.SharedSecret;
        var trusted = SecretComparer.FixedTimeEquals(context.Request.Headers["X-Gateway-Secret"], gatewaySecret);
        var userId = context.Request.Headers["X-User-Id"].ToString();
        var partitionKey = trusted && long.TryParse(userId, out var parsedUserId)
            ? $"user:{parsedUserId}"
            : $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
});
builder.Services.AddGraphQLServer("Payment").AddQueryType<Query>().AddMutationType<Mutation>();

var app = builder.Build();
app.UseExceptionHandler(exceptionHandlerApp => exceptionHandlerApp.Run(async context =>
{
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await context.Response.WriteAsJsonAsync(new { error = "internal_error" });
}));
app.UseRateLimiter();
app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
app.MapGet("/health/ready", async (NpgsqlDataSource database, CancellationToken cancellationToken) =>
{
    try
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        return Results.Ok(new { status = "ready" });
    }
    catch { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
});
app.MapGraphQL("/graphql", "Payment").RequireRateLimiting("graphql");
app.MapPayOSWebhook();
await app.RunWithGraphQLCommandsAsync(args);

public sealed class NpgsqlOptions { public string ConnectionString { get; set; } = string.Empty; }
public partial class Program;
