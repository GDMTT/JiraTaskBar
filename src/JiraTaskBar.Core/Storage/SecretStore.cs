using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JiraTaskBar.Core.Storage;

/// <summary>
/// The Jira site, email and API token, always stored and loaded together so the token can
/// only ever be sent to the site it was saved for.
/// </summary>
/// <param name="CloudId">Set when the token is a scoped token (calls go through the API gateway).</param>
public sealed record StoredCredentials(string SiteUrl, string Email, string ApiToken, string? CloudId = null);

public enum CredentialLoadStatus { Missing, Loaded, Unavailable }

/// <summary>
/// Stores <see cref="StoredCredentials"/> in Windows Credential Manager for the current user.
/// Because the site is inside the credential, editing settings.json can't redirect the token.
/// Existing DPAPI files are migrated only after the Credential Manager write is verified.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("JiraTaskBar.Credentials.v2");
    private static readonly byte[] LegacyEntropy = Encoding.UTF8.GetBytes("JiraTaskBar.ApiToken.v1");

    private readonly string _path;
    private readonly string? _legacyTokenPath;
    private readonly ICredentialBackend _backend;
    private readonly Func<string, byte[], byte[]?> _unprotect;
    private readonly Action<string> _deleteFile;

    /// <param name="path">The previous version's encrypted credentials file, used only for migration.</param>
    /// <param name="legacyTokenPath">The v1 file that held only the token, if it may still exist.</param>
    public SecretStore(string path, string? legacyTokenPath = null, ICredentialBackend? backend = null,
        Func<string, byte[], byte[]?>? legacyReader = null, Action<string>? legacyDelete = null)
    {
        _path = path;
        _legacyTokenPath = legacyTokenPath;
        _backend = backend ?? new WindowsCredentialBackend();
        _unprotect = legacyReader ?? Unprotect;
        _deleteFile = legacyDelete ?? File.Delete;
    }

    public CredentialLoadStatus LastLoadStatus { get; private set; } = CredentialLoadStatus.Missing;

    public StoredCredentials? Load()
    {
        LastLoadStatus = CredentialLoadStatus.Missing;
        byte[]? stored;
        try
        {
            stored = _backend.Read();
        }
        catch (CredentialBackendException)
        {
            // An inaccessible vault must not silently fall back to a possibly stale credential.
            LastLoadStatus = CredentialLoadStatus.Unavailable;
            return null;
        }

        if (stored is not null)
        {
            try
            {
                var credentials = Deserialize(stored);
                LastLoadStatus = credentials is null ? CredentialLoadStatus.Unavailable : CredentialLoadStatus.Loaded;
                return credentials;
            }
            finally { CryptographicOperations.ZeroMemory(stored); }
        }

        byte[]? plain;
        try { plain = _unprotect(_path, Entropy); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            LastLoadStatus = CredentialLoadStatus.Unavailable;
            return null;
        }
        if (plain is null)
        {
            LastLoadStatus = File.Exists(_path) ? CredentialLoadStatus.Unavailable : CredentialLoadStatus.Missing;
            return null;
        }
        try
        {
            var credentials = Deserialize(plain);
            if (credentials is null)
            {
                LastLoadStatus = CredentialLoadStatus.Unavailable;
                return null;
            }
            LastLoadStatus = CredentialLoadStatus.Loaded;

            // Keep using the valid legacy credential if migration is temporarily unavailable,
            // and keep the file so the next startup can retry.
            try
            {
                WriteAndVerify(plain);
                DeleteFileBestEffort(_path);
            }
            catch (CredentialBackendException) { }
            return credentials;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public void Save(StoredCredentials credentials)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(credentials);
        try
        {
            WriteAndVerify(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
        DeleteFileBestEffort(_path);
        DeleteLegacy();
    }

    /// <summary>The token from a v1 install (no site bound to it), or null.</summary>
    public string? LoadLegacyToken()
    {
        if (_legacyTokenPath is null) return null;
        var plain = _unprotect(_legacyTokenPath, LegacyEntropy);
        return plain is null ? null : Encoding.UTF8.GetString(plain);
    }

    public void DeleteLegacy()
    {
        if (_legacyTokenPath is not null) DeleteFileBestEffort(_legacyTokenPath);
    }

    public void Clear()
    {
        _backend.Delete();
        DeleteFileBestEffort(_path);
        DeleteLegacy();
    }

    private void WriteAndVerify(byte[] plain)
    {
        _backend.Write(plain);
        var verified = _backend.Read();
        if (verified is null)
            throw new CredentialBackendException("Credential Manager did not return the saved credential.");
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(plain, verified))
                throw new CredentialBackendException("Credential Manager returned different credential data.");
        }
        finally { CryptographicOperations.ZeroMemory(verified); }
    }

    private void DeleteFileBestEffort(string path)
    {
        if (!File.Exists(path)) return;
        try { _deleteFile(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static StoredCredentials? Deserialize(byte[] plain)
    {
        try
        {
            var credentials = JsonSerializer.Deserialize<StoredCredentials>(plain);
            return credentials is { SiteUrl.Length: > 0, Email.Length: > 0, ApiToken.Length: > 0 } ? credentials : null;
        }
        catch (JsonException) { return null; }
    }

    private static byte[]? Unprotect(string path, byte[] entropy)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return ProtectedData.Unprotect(File.ReadAllBytes(path), entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
