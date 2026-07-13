using Xunit;

namespace fakebookPayment.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgreSqlIntegrationCollection
{
    public const string Name = "PostgreSQL integration";
}
