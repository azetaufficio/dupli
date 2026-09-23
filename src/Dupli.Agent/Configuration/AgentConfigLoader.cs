using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dupli.Agent.Configuration;

public static class AgentConfigLoader
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AgentConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Agent configuration not found: {path}", path);

        var json = File.ReadAllText(path);
        return Parse(json);
    }

    public static AgentConfig Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<AgentConfig>(json, JsonOptions)
                ?? throw new InvalidDataException("Agent configuration is empty");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Agent configuration is invalid: {ex.Message}", ex);
        }
    }

    public static string Serialize(AgentConfig config) => JsonSerializer.Serialize(config, JsonOptions);
}
