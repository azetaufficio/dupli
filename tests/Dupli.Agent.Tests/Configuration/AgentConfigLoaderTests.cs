using Dupli.Agent.Configuration;
using Dupli.Contracts.Policies;

namespace Dupli.Agent.Tests.Configuration;

public sealed class AgentConfigLoaderTests
{
    private const string SampleJson = """
        {
          "agentName": "vm-01",
          "host": "vm-01.internal",
          "repository": {
            "endpoint": "https://s3.eu-central-1.wasabisys.com",
            "bucket": "dupli-vm-01",
            "prefix": "restic",
            "region": "eu-central-1",
            "passwordSecret": "restic-password",
            "accessKeySecret": "s3-access-key",
            "secretKeySecret": "s3-secret-key"
          },
          "resticManifest": {
            "version": "0.19.1",
            "downloadUrl": "https://example.invalid/restic.zip",
            "sha256": "0000000000000000000000000000000000000000000000000000000000000000"
          },
          "policies": [
            {
              "cron": "0 3 * * *",
              "timeZone": "Europe/Rome",
              "policy": {
                "policyId": "p-files",
                "name": "Files",
                "sources": [
                  { "type": "directory", "sourceId": "s-docs", "paths": [ "C:\\Data" ], "excludes": [ "*.tmp" ] },
                  { "type": "postgres", "sourceId": "s-db", "username": "postgres", "passwordSecret": "pg-password" }
                ]
              }
            }
          ]
        }
        """;

    [Fact]
    public void Parses_repository_manifest_and_polymorphic_sources()
    {
        var config = AgentConfigLoader.Parse(SampleJson);

        Assert.Equal("vm-01", config.AgentName);
        Assert.Equal("vm-01.internal", config.Host);
        Assert.Equal("dupli-vm-01", config.Repository.Bucket);
        Assert.Equal("restic-password", config.Repository.PasswordSecret);
        Assert.Equal("0.19.1", config.ResticManifest.Version);

        var policy = Assert.Single(config.Policies);
        Assert.Equal("0 3 * * *", policy.Cron);
        Assert.Equal("Europe/Rome", policy.TimeZone);
        Assert.Equal(2, policy.Policy.Sources.Count);

        var dir = Assert.IsType<DirectorySourceDto>(policy.Policy.Sources[0]);
        Assert.Equal(["C:\\Data"], dir.Paths);
        Assert.Equal(["*.tmp"], dir.Excludes);

        var pg = Assert.IsType<PostgresSourceDto>(policy.Policy.Sources[1]);
        Assert.Equal("postgres", pg.Username);
        Assert.Equal("pg-password", pg.PasswordSecret);
    }

    [Fact]
    public void Round_trips_through_serialize_and_parse()
    {
        var config = AgentConfigLoader.Parse(SampleJson);
        var roundTripped = AgentConfigLoader.Parse(AgentConfigLoader.Serialize(config));

        Assert.Equal(config.AgentName, roundTripped.AgentName);
        Assert.Equal(config.Policies.Count, roundTripped.Policies.Count);
        Assert.Equal(config.Policies[0].Policy.Sources.Count, roundTripped.Policies[0].Policy.Sources.Count);
    }

    [Fact]
    public void Rejects_invalid_json()
    {
        Assert.Throws<InvalidDataException>(() => AgentConfigLoader.Parse("{ not json"));
    }

    [Fact]
    public void Load_throws_when_file_missing()
    {
        Assert.Throws<FileNotFoundException>(() => AgentConfigLoader.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())));
    }
}
