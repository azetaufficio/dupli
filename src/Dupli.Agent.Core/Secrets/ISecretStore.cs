namespace Dupli.Agent.Core.Secrets;

/// <summary>Local encrypted secret storage (DPAPI on Windows). Never plaintext on disk.</summary>
public interface ISecretStore
{
    string? Get(string name);

    void Set(string name, string value);

    string GetRequired(string name) =>
        Get(name) ?? throw new InvalidOperationException($"Secret '{name}' is not configured");
}
