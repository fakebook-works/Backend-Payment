using Dapper;
using Npgsql;

namespace Fakebook.Payment.Workers;

public sealed class DatabaseInitializer(NpgsqlDataSource dataSource, IWebHostEnvironment environment, ILogger<DatabaseInitializer> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var path = System.IO.Path.Combine(environment.ContentRootPath, "schema.sql");
        var sql = await File.ReadAllTextAsync(path, cancellationToken);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));
        logger.LogInformation("Payment database schema is ready");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
