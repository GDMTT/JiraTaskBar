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

/// <summary>
/// Stores <see cref="StoredCredentials"/> encrypted with DPAPI for the current Windows user.
/// Because the site is inside the encrypted blob, editing settings.json can't redirect the token.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("JiraTaskBar.Credentials.v2");
    private static readonly byte[] LegacyEntropy = Encoding.UTF8.GetBytes("JiraTaskBar.ApiToken.v1");

    private readonly string _path;
    private readonly string? _legacyTokenPath;

    /// <param name="path">The encrypted credentials file.</param>
    /// <param name="legacyTokenPath">The v1 file that held only the token, if it may still exist.</param>
    public SecretStore(string path, string? legacyTokenPath = null)
    {
        _path = path;
        _legacyTokenPath = legacyTokenPath;
    }

    public StoredCredentials? Load()
    {
        var plain = Unprotect(_path, Entropy);
        if (plain is null) return null;
        try
        {
            var credentials = JsonSerializer.Deserialize<StoredCredentials>(plain);
            return credentials is { SiteUrl.Length: > 0, Email.Length: > 0, ApiToken.Length: > 0 } ? credentials : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(StoredCredentials credentials)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var plain = JsonSerializer.SerializeToUtf8Bytes(credentials);
        try
        {
            var cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            var temp = _path + ".tmp";
            File.WriteAllBytes(temp, cipher);
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
        DeleteLegacy();
    }

    /// <summary>The token from a v1 install (no site bound to it), or null.</summary>
    public string? LoadLegacyToken()
    {
        if (_legacyTokenPath is null) return null;
        var plain = Unprotect(_legacyTokenPath, LegacyEntropy);
        return plain is null ? null : Encoding.UTF8.GetString(plain);
    }

    public void DeleteLegacy()
    {
        if (_legacyTokenPath is not null && File.Exists(_legacyTokenPath)) File.Delete(_legacyTokenPath);
    }

    public void Clear()
    {
        if (File.Exists(_path)) File.Delete(_path);
        DeleteLegacy();
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
