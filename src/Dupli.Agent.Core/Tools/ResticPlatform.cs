using System.Runtime.InteropServices;

namespace Dupli.Agent.Core.Tools;

public static class ResticPlatform
{
    /// <summary>Name of the restic release asset platform for this process (e.g. <c>linux_arm64</c>).</summary>
    public static string Current
    {
        get
        {
            var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "darwin" : "linux";
            var arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "amd64",
                Architecture.Arm64 => "arm64",
                Architecture.X86 => "386",
                var other => other.ToString().ToLowerInvariant(),
            };
            return $"{os}_{arch}";
        }
    }
}
