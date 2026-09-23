using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dupli.Contracts;

/// <summary>Wire format shared by agent and server: web defaults, enums as strings.</summary>
public static class DupliJson
{
    public static JsonSerializerOptions Options { get; } = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter());

        // Polymorphic discriminators ("type", "kind") are not guaranteed to be the first property:
        // PostgreSQL jsonb reorders keys, and other clients may too.
        options.AllowOutOfOrderMetadataProperties = true;
        return options;
    }
}
