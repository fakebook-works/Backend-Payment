using Fakebook.Payment.Workers;

namespace fakebookPayment.Tests;

public sealed class DatabaseMigrationCatalogTests
{
    [Fact]
    public void Catalog_embeds_the_existing_schema_as_an_immutable_version()
    {
        var migration = PaymentSqlMigrationCatalog
            .Load(typeof(DatabaseInitializer).Assembly)
            .Single(candidate => candidate.Version == 2026071501);

        Assert.Equal(2026071501, migration.Version);
        Assert.Equal("InitialAndLegacyReconciliation", migration.Name);
        Assert.Contains("CREATE SCHEMA IF NOT EXISTS payment", migration.Sql);
        Assert.Contains("CREATE TABLE IF NOT EXISTS payment.payment_order", migration.Sql);
        Assert.Equal(64, migration.Checksum.Length);
    }
}
