using System.Reflection;

namespace Dupli.Agent.Configuration;

/// <summary>
/// The one version string of this build: <c>AssemblyInformationalVersion</c> without the <c>+commit</c> suffix
/// (so <c>0.2.0-beta.1</c> survives). Used for <c>versions\&lt;ver&gt;\</c>, enrollment, heartbeat and updates.
/// </summary>
public static class AgentVersion
{
    public static readonly string Current = Resolve(typeof(AgentVersion).Assembly);

    internal static string Resolve(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }
        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    /// <summary>Versions become directory names: digits, letters, dots and dashes only.</summary>
    public static bool IsValid(string version) =>
        version.Length is > 0 and <= 64 &&
        char.IsAsciiDigit(version[0]) &&
        version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-');
}
