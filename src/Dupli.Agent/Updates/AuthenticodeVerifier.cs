using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Dupli.Agent.Configuration;

namespace Dupli.Agent.Updates;

public interface IPackageSignatureVerifier
{
    /// <summary>Throws <see cref="InvalidDataException"/> when the package does not satisfy <see cref="UpdateConfig"/>.</summary>
    void Verify(string file);
}

/// <summary>
/// Optional Authenticode check of a downloaded agent package: WinVerifyTrust (valid chain, not revoked) plus,
/// when configured, a signer thumbprint allow-list. The trust settings live in the local agent.json only.
/// </summary>
public sealed class AuthenticodeVerifier(UpdateConfig config) : IPackageSignatureVerifier
{
    public void Verify(string file)
    {
        if (!config.RequireSignature)
            return;
        if (!OperatingSystem.IsWindows())
            throw new InvalidDataException("Signature verification is required but only supported on Windows");

        VerifyWindows(file);
    }

    [SupportedOSPlatform("windows")]
    private void VerifyWindows(string file)
    {
        var status = WinTrust.Verify(file);
        if (status != 0)
            throw new InvalidDataException($"Authenticode signature of {Path.GetFileName(file)} is not valid (WinVerifyTrust 0x{status:X8})");

        if (config.SignerThumbprints.Count == 0)
            return;

#pragma warning disable SYSLIB0057 // No X509CertificateLoader equivalent for reading the Authenticode signer.
        using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(file));
#pragma warning restore SYSLIB0057
        if (!config.SignerThumbprints.Any(t => string.Equals(t.Replace(" ", ""), signer.Thumbprint, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"{Path.GetFileName(file)} is signed by {signer.Subject} ({signer.Thumbprint}), not an accepted signer");
    }

    [SupportedOSPlatform("windows")]
    private static class WinTrust
    {
        private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        private const uint UiNone = 2;
        private const uint RevokeWholeChain = 1;
        private const uint ChoiceFile = 1;
        private const uint StateActionVerify = 1;
        private const uint StateActionClose = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FileInfo
        {
            public uint cbStruct;
            public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TrustData
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref TrustData data);

        public static int Verify(string path)
        {
            var fileInfo = new FileInfo { cbStruct = (uint)Marshal.SizeOf<FileInfo>(), pcwszFilePath = path };
            var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
            try
            {
                Marshal.StructureToPtr(fileInfo, filePtr, false);
                var data = new TrustData
                {
                    cbStruct = (uint)Marshal.SizeOf<TrustData>(),
                    dwUIChoice = UiNone,
                    fdwRevocationChecks = RevokeWholeChain,
                    dwUnionChoice = ChoiceFile,
                    pFile = filePtr,
                    dwStateAction = StateActionVerify,
                };
                var action = GenericVerifyV2;
                var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                data.dwStateAction = StateActionClose;
                WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                return result;
            }
            finally
            {
                Marshal.DestroyStructure<FileInfo>(filePtr);
                Marshal.FreeHGlobal(filePtr);
            }
        }
    }
}
