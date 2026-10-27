using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace JiraTaskBar.Core.Storage;

/// <summary>Stores the Jira API token encrypted with DPAPI for the current Windows user.</summary>
[SupportedOSPlatform("windows")]
public sealed class SecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("JiraTaskBar.ApiToken.v1");
    private readonly string _path;

    public SecretStore(string path) => _path = path;

    public string? LoadToken()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public void SaveToken(string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_path, cipher);
    }

    public void Clear()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
