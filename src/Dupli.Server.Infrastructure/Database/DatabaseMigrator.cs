using DbUp;
using Microsoft.Extensions.Logging;

namespace Dupli.Server.Infrastructure.Database;

/// <summary>Applies the embedded <c>Database/Scripts/*.sql</c> in name order, once each (DbUp journal).</summary>
public static class DatabaseMigrator
{
    public static void Migrate(string connectionString, ILogger logger)
    {
        EnsureDatabase.For.PostgresqlDatabase(connectionString);

        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(typeof(DatabaseMigrator).Assembly, name => name.EndsWith(".sql", StringComparison.Ordinal))
            .WithTransactionPerScript()
            .LogToNowhere()
            .Build();

        var result = upgrader.PerformUpgrade();
        if (!result.Successful)
            throw new InvalidOperationException($"Database migration failed at script {result.ErrorScript?.Name}", result.Error);

        foreach (var script in result.Scripts)
            logger.LogInformation("Applied database script {Script}", script.Name);
    }
}
