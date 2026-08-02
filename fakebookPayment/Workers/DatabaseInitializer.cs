using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Fakebook.Payment.Workers;

public sealed class DatabaseInitializer(
    IConfiguration configuration,
    ILogger<DatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var migrationConnectionString = configuration.GetConnectionString("PaymentMigrationDatabase");
        var usesDedicatedMigrationRole = !string.IsNullOrWhiteSpace(migrationConnectionString);
        if (!usesDedicatedMigrationRole)
        {
            migrationConnectionString = configuration.GetConnectionString("PaymentDatabase");
        }

        if (string.IsNullOrWhiteSpace(migrationConnectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:PaymentMigrationDatabase or ConnectionStrings:PaymentDatabase must be configured.");
        }

        var commandTimeoutSeconds = configuration.GetValue(
            "Database:MigrationCommandTimeoutSeconds",
            300);
        if (commandTimeoutSeconds is < 1 or > 3_600)
        {
            throw new InvalidOperationException(
                "Database:MigrationCommandTimeoutSeconds must be between 1 and 3600.");
        }

        var connectionOptions = new NpgsqlConnectionStringBuilder(migrationConnectionString)
        {
            CommandTimeout = commandTimeoutSeconds,
            Enlist = false,
            Multiplexing = false,
            Pooling = false
        };
        await using var connection = new NpgsqlConnection(connectionOptions.ConnectionString);

        try
        {
            await connection.OpenAsync(cancellationToken);

            logger.LogInformation(
                "Applying Payment database migrations with {MigrationRoleMode} credentials.",
                usesDedicatedMigrationRole ? "dedicated" : "runtime fallback");
            await PaymentDatabaseMigrator.MigrateAsync(
                connection,
                logger,
                commandTimeoutSeconds,
                cancellationToken);
            logger.LogInformation("Payment database migrations are current.");
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "Payment database migration failed; startup is aborted.");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static class PaymentDatabaseMigrator
{
    private const long MigrationLockId = 4_609_001_007_001;

    internal static async Task MigrateAsync(
        NpgsqlConnection connection,
        ILogger logger,
        int commandTimeoutSeconds = 300,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(logger);

        if (connection.State != System.Data.ConnectionState.Open)
        {
            throw new InvalidOperationException("The Payment migration connection must already be open.");
        }
        if (commandTimeoutSeconds is < 1 or > 3_600)
        {
            throw new ArgumentOutOfRangeException(
                nameof(commandTimeoutSeconds),
                "Migration command timeout must be between 1 and 3600 seconds.");
        }

        var lockAcquired = false;
        try
        {
            await SetMigrationLockAsync(
                connection,
                acquire: true,
                commandTimeoutSeconds,
                cancellationToken);
            lockAcquired = true;
            await EnsureMigrationLedgerAsync(connection, commandTimeoutSeconds, cancellationToken);

            var migrations = PaymentSqlMigrationCatalog.Load(typeof(DatabaseInitializer).Assembly);
            var appliedMigrations = await LoadAppliedMigrationsAsync(
                connection,
                commandTimeoutSeconds,
                cancellationToken);
            ValidateLedger(migrations, appliedMigrations);

            foreach (var migration in migrations)
            {
                if (appliedMigrations.ContainsKey(migration.Version))
                {
                    continue;
                }

                await ApplyMigrationAsync(
                    connection,
                    migration,
                    commandTimeoutSeconds,
                    cancellationToken);
                logger.LogInformation(
                    "Applied Payment database migration {MigrationVersion}_{MigrationName}.",
                    migration.Version,
                    migration.Name);
            }
        }
        finally
        {
            if (lockAcquired)
            {
                try
                {
                    await SetMigrationLockAsync(
                        connection,
                        acquire: false,
                        commandTimeoutSeconds,
                        CancellationToken.None);
                }
                catch
                {
                    // Disposing the PostgreSQL session also releases a session lock.
                }
            }
        }
    }

    private static async Task EnsureMigrationLedgerAsync(
        NpgsqlConnection connection,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = commandTimeoutSeconds;
        command.CommandText =
            """
            CREATE SCHEMA IF NOT EXISTS payment;
            CREATE TABLE IF NOT EXISTS payment.schema_migrations (
                version bigint PRIMARY KEY,
                name text NOT NULL,
                checksum text NOT NULL CHECK (length(checksum) = 64),
                applied_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Dictionary<long, AppliedPaymentMigration>> LoadAppliedMigrationsAsync(
        NpgsqlConnection connection,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = commandTimeoutSeconds;
        command.CommandText =
            "SELECT version, name, checksum FROM payment.schema_migrations ORDER BY version;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var migrations = new Dictionary<long, AppliedPaymentMigration>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var migration = new AppliedPaymentMigration(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2));
            migrations.Add(migration.Version, migration);
        }

        return migrations;
    }

    private static void ValidateLedger(
        IReadOnlyList<PaymentSqlMigration> knownMigrations,
        IReadOnlyDictionary<long, AppliedPaymentMigration> appliedMigrations)
    {
        var knownByVersion = knownMigrations.ToDictionary(migration => migration.Version);
        foreach (var applied in appliedMigrations.Values)
        {
            if (!knownByVersion.TryGetValue(applied.Version, out var known))
            {
                throw new InvalidOperationException(
                    $"Payment migration {applied.Version}_{applied.Name} is recorded in PostgreSQL but is missing from this build.");
            }

            if (!string.Equals(applied.Name, known.Name, StringComparison.Ordinal) ||
                !string.Equals(applied.Checksum, known.Checksum, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Payment migration {applied.Version}_{applied.Name} no longer matches its immutable migration resource.");
            }
        }

        if (appliedMigrations.Count != 0)
        {
            var highestAppliedVersion = appliedMigrations.Keys.Max();
            var missingEarlierMigration = knownMigrations.FirstOrDefault(migration =>
                migration.Version < highestAppliedVersion &&
                !appliedMigrations.ContainsKey(migration.Version));
            if (missingEarlierMigration is not null)
            {
                throw new InvalidOperationException(
                    $"Payment migration ledger has an out-of-order gap at {missingEarlierMigration.Version}_{missingEarlierMigration.Name}.");
            }
        }
    }

    private static async Task ApplyMigrationAsync(
        NpgsqlConnection connection,
        PaymentSqlMigration migration,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var migrationCommand = connection.CreateCommand())
        {
            migrationCommand.CommandTimeout = commandTimeoutSeconds;
            migrationCommand.Transaction = transaction;
            migrationCommand.CommandText = migration.Sql;
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var ledgerCommand = connection.CreateCommand())
        {
            ledgerCommand.CommandTimeout = commandTimeoutSeconds;
            ledgerCommand.Transaction = transaction;
            ledgerCommand.CommandText =
                """
                INSERT INTO payment.schema_migrations (version, name, checksum)
                VALUES (@version, @name, @checksum);
                """;
            ledgerCommand.Parameters.AddWithValue("version", migration.Version);
            ledgerCommand.Parameters.AddWithValue("name", migration.Name);
            ledgerCommand.Parameters.AddWithValue("checksum", migration.Checksum);
            await ledgerCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task SetMigrationLockAsync(
        NpgsqlConnection connection,
        bool acquire,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = commandTimeoutSeconds;
        command.CommandText = acquire
            ? $"SELECT pg_advisory_lock({MigrationLockId});"
            : $"SELECT pg_advisory_unlock({MigrationLockId});";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record AppliedPaymentMigration(long Version, string Name, string Checksum);
}

internal sealed record PaymentSqlMigration(
    long Version,
    string Name,
    string ResourceName,
    string Sql,
    string Checksum);

internal static class PaymentSqlMigrationCatalog
{
    private const string ResourcePrefix = "Fakebook.Payment.Database.Migrations.";
    private const string SqlSuffix = ".sql";

    internal static IReadOnlyList<PaymentSqlMigration> Load(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var migrations = assembly.GetManifestResourceNames()
            .Where(resourceName =>
                resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal) &&
                resourceName.EndsWith(SqlSuffix, StringComparison.Ordinal))
            .Select(resourceName => LoadMigration(assembly, resourceName))
            .OrderBy(migration => migration.Version)
            .ToArray();

        if (migrations.Length == 0)
        {
            throw new InvalidOperationException("No embedded Payment SQL migrations were found.");
        }

        var duplicateVersion = migrations
            .GroupBy(migration => migration.Version)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateVersion is not null)
        {
            throw new InvalidOperationException(
                $"Payment migration version {duplicateVersion.Key} is duplicated.");
        }

        return migrations;
    }

    private static PaymentSqlMigration LoadMigration(Assembly assembly, string resourceName)
    {
        var fileName = resourceName[ResourcePrefix.Length..];
        fileName = fileName[..^SqlSuffix.Length];
        var separator = fileName.IndexOf('_');
        if (separator <= 0 ||
            !long.TryParse(fileName[..separator], out var version) ||
            version <= 0 ||
            separator == fileName.Length - 1)
        {
            throw new InvalidOperationException(
                $"Embedded Payment migration '{resourceName}' must use '<positive-version>_<name>.sql'.");
        }

        var name = fileName[(separator + 1)..];
        if (name.Any(character => !char.IsLetterOrDigit(character) && character != '_'))
        {
            throw new InvalidOperationException(
                $"Embedded Payment migration '{resourceName}' contains an invalid name.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Could not open Payment migration resource '{resourceName}'.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var sql = NormalizeLineEndings(reader.ReadToEnd());
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new InvalidOperationException(
                $"Embedded Payment migration '{resourceName}' is empty.");
        }
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)))
            .ToLowerInvariant();
        return new PaymentSqlMigration(version, name, resourceName, sql, checksum);
    }

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
