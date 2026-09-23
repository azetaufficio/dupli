using Cronos;
using Dupli.Contracts.Policies;

namespace Dupli.Server.Api;

public static class PolicyValidator
{
    public static void Validate(PolicyRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw ApiException.BadRequest("Policy name is required");

        try
        {
            CronExpression.Parse(request.Cron);
        }
        catch (CronFormatException ex)
        {
            throw ApiException.BadRequest($"Invalid cron '{request.Cron}': {ex.Message}");
        }

        if (!TimeZoneInfo.TryFindSystemTimeZoneById(request.TimeZone, out _))
            throw ApiException.BadRequest($"Unknown time zone '{request.TimeZone}'");

        var r = request.Retention;
        if (r.KeepDaily < 0 || r.KeepWeekly < 0 || r.KeepMonthly < 0 || r.KeepDaily + r.KeepWeekly + r.KeepMonthly == 0)
            throw ApiException.BadRequest("Retention must keep at least one snapshot and cannot be negative");

        if (request.Sources.Count == 0)
            throw ApiException.BadRequest("A policy needs at least one source");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in request.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.SourceId) || source.SourceId.Contains(',') || source.SourceId.Contains('='))
                throw ApiException.BadRequest($"Invalid source id '{source.SourceId}' (required, no ',' or '=')");
            if (!ids.Add(source.SourceId))
                throw ApiException.BadRequest($"Duplicate source id '{source.SourceId}'");

            switch (source)
            {
                case DirectorySourceDto dir when dir.Paths.Count == 0 || dir.Paths.Any(string.IsNullOrWhiteSpace):
                    throw ApiException.BadRequest($"Source '{source.SourceId}' needs at least one non-empty path");
                case PostgresSourceDto pg when string.IsNullOrWhiteSpace(pg.Username) || string.IsNullOrWhiteSpace(pg.PasswordSecret):
                    throw ApiException.BadRequest($"Source '{source.SourceId}' needs username and passwordSecret");
                case PostgresSourceDto pg when pg.Port is <= 0 or > 65535:
                    throw ApiException.BadRequest($"Source '{source.SourceId}' has an invalid port");
                case PostgresSourceDto { DatabaseSelection: DatabaseSelection.Only } pg
                    when pg.IncludeDatabases.Count == 0 || pg.IncludeDatabases.Any(string.IsNullOrWhiteSpace):
                    throw ApiException.BadRequest($"Source '{source.SourceId}' selects only listed databases but the list is empty");
            }
        }
    }
}
