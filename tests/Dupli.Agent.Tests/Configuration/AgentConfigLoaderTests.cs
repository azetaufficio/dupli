using Dupli.Agent.Configuration;

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
            "region": "eu-central-1"
          },
          "resticManifest": {
            "version": "0.19.1",
            "downloadUrl": "https://example.invalid/restic.zip",
            "sha256": "0000000000000000000000000000000000000000000000000000000000000000"
          },
          "server": {
            "url": "https://dupli.example",
            "agentId": "11111111-1111-1111-1111-111111111111"
          }
        }
        """;

    [Fact]
    public void Parses_repository_and_manifest()
    {
        var config = AgentConfigLoader.Parse(SampleJson);

        Assert.Equal("vm-01", config.AgentName);
        Assert.Equal("vm-01.internal", config.Host);
        Assert.Equal("dupli-vm-01", config.Repository.Bucket);
        Assert.Equal("0.19.1", config.ResticManifest.Version);
        Assert.Equal("11111111-1111-1111-1111-111111111111", config.Server?.AgentId);
    }

    [Fact]
    public void Round_trips_through_serialize_and_parse()
    {
        var config = AgentConfigLoader.Parse(SampleJson);
        var roundTripped = AgentConfigLoader.Parse(AgentConfigLoader.Serialize(config));

        Assert.Equal(config.AgentName, roundTripped.AgentName);
        Assert.Equal(config.Repository.Bucket, roundTripped.Repository.Bucket);
        Assert.Equal(config.Server?.AgentId, roundTripped.Server?.AgentId);
    }

    /// <summary>An older <c>agent.json</c> could carry secret-name fields no longer part of the model
    /// (<c>passwordSecret</c>, <c>policies</c>, ...): they must be silently ignored, never rejected.</summary>
    [Fact]
    public void Unknown_fields_from_an_older_agent_json_are_ignored()
    {
        const string legacy = """
            {
              "agentName": "vm-01",
              "repository": {
                "endpoint": "https://s3.eu-central-1.wasabisys.com",
                "bucket": "dupli-vm-01",
                "passwordSecret": "repo-password",
                "accessKeySecret": "s3-access-key",
                "secretKeySecret": "s3-secret-key"
              },
              "resticManifest": {
                "version": "0.19.1",
                "downloadUrl": "https://example.invalid/restic.zip",
                "sha256": "0000000000000000000000000000000000000000000000000000000000000000"
              },
              "policies": [
                { "cron": "0 3 * * *", "policy": { "policyId": "p", "name": "n", "sources": [] } }
              ]
            }
            """;

        var config = AgentConfigLoader.Parse(legacy);

        Assert.Equal("vm-01", config.AgentName);
        Assert.Equal("dupli-vm-01", config.Repository.Bucket);
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
