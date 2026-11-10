using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using JiraTaskBar.Core.Jira;
using JiraTaskBar.Core.Markup;
using JiraTaskBar.Core.Storage;
using JiraTaskBar.Core.Tracking;

namespace JiraTaskBar.Core.Tests;

[SupportedOSPlatform("windows")]
public class SecretStoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeCredentialBackend _backend = new();
    private static readonly StoredCredentials Creds = new("https://acme.atlassian.net/", "me@acme.com", "secret-token");

    public void Dispose() => _dir.Dispose();

    private SecretStore Store => new(_dir.File("credentials.bin"), _dir.File("token.bin"), _backend);

    [Fact]
    public void Site_email_and_token_round_trip_together()
    {
        Store.Save(Creds);

        Assert.Equal(Creds, Store.Load());
        Assert.False(File.Exists(_dir.File("credentials.bin")));
        Assert.NotNull(_backend.Data);
    }

    [Fact]
    public void Tampered_blob_is_rejected()
    {
        Store.Save(Creds);
        _backend.Data![^1] ^= 0xFF;

        Assert.Null(Store.Load());
    }

    [Fact]
    public void Legacy_token_is_readable_until_new_credentials_are_saved()
    {
        var legacyPath = _dir.File("token.bin");
        File.WriteAllText(legacyPath, "legacy placeholder");
        var store = new SecretStore(_dir.File("credentials.bin"), legacyPath, _backend,
            (path, _) => path == legacyPath ? Encoding.UTF8.GetBytes("old-token") : null);

        Assert.Null(store.Load());
        Assert.Equal("old-token", store.LoadLegacyToken());

        store.Save(Creds with { ApiToken = "old-token" });

        Assert.False(File.Exists(_dir.File("token.bin")));
        Assert.Equal("old-token", store.Load()!.ApiToken);
    }

    [Fact]
    public void Dpapi_credentials_migrate_only_after_verified_write()
    {
        var path = _dir.File("credentials.bin");
        File.WriteAllText(path, "legacy placeholder");
        var plain = JsonSerializer.SerializeToUtf8Bytes(Creds);
        var store = new SecretStore(path, backend: _backend,
            legacyReader: (candidate, _) => candidate == path ? plain.ToArray() : null);

        Assert.Equal(Creds, store.Load());
        Assert.False(File.Exists(path));
        Assert.Equal(Creds, store.Load());
    }

    [Fact]
    public void Failed_migration_preserves_dpapi_file_and_credential()
    {
        var path = _dir.File("credentials.bin");
        File.WriteAllText(path, "legacy placeholder");
        _backend.FailWrites = true;
        var plain = JsonSerializer.SerializeToUtf8Bytes(Creds);
        var store = new SecretStore(path, backend: _backend,
            legacyReader: (candidate, _) => candidate == path ? plain.ToArray() : null);

        Assert.Equal(Creds, store.Load());
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Inaccessible_or_corrupt_vault_returns_no_credentials()
    {
        var store = Store;
        _backend.FailReads = true;
        Assert.Null(store.Load());
        Assert.Equal(CredentialLoadStatus.Unavailable, store.LastLoadStatus);

        _backend.FailReads = false;
        _backend.Data = Encoding.UTF8.GetBytes("not json");
        Assert.Null(store.Load());
        Assert.Equal(CredentialLoadStatus.Unavailable, store.LastLoadStatus);
    }

    [Fact]
    public void Corrupt_vault_blocks_legacy_fallback()
    {
        _backend.Data = Encoding.UTF8.GetBytes("not json");
        var legacyPath = _dir.File("token.bin");
        File.WriteAllText(legacyPath, "legacy placeholder");
        var store = new SecretStore(_dir.File("credentials.bin"), legacyPath, _backend,
            (path, _) => path == legacyPath ? Encoding.UTF8.GetBytes("old-token") : null);

        Assert.Null(store.Load());
        Assert.Equal(CredentialLoadStatus.Unavailable, store.LastLoadStatus);
        Assert.NotEqual(CredentialLoadStatus.Missing, store.LastLoadStatus);
    }

    [Fact]
    public void Cleanup_failures_do_not_hide_verified_credentials()
    {
        var path = _dir.File("credentials.bin");
        File.WriteAllText(path, "legacy placeholder");
        var plain = JsonSerializer.SerializeToUtf8Bytes(Creds);
        var store = new SecretStore(path, backend: _backend,
            legacyReader: (candidate, _) => candidate == path ? plain.ToArray() : null,
            legacyDelete: _ => throw new IOException("locked"));

        Assert.Equal(Creds, store.Load());
        Assert.True(File.Exists(path));
        Assert.Equal(Creds, store.Load());
    }

    [Fact]
    public void Clear_removes_vault_and_legacy_files()
    {
        Store.Save(Creds);
        File.WriteAllText(_dir.File("credentials.bin"), "old");
        File.WriteAllText(_dir.File("token.bin"), "older");

        Store.Clear();

        Assert.Null(_backend.Data);
        Assert.False(File.Exists(_dir.File("credentials.bin")));
        Assert.False(File.Exists(_dir.File("token.bin")));
    }

    [Fact]
    public void Clear_reports_legacy_cleanup_failure_without_removing_vault_credentials()
    {
        Store.Save(Creds);
        var legacyPath = _dir.File("token.bin");
        File.WriteAllText(legacyPath, "legacy placeholder");
        var store = new SecretStore(_dir.File("credentials.bin"), legacyPath, _backend,
            legacyDelete: _ => throw new IOException("locked"));

        Assert.Throws<IOException>(() => store.Clear());

        Assert.NotNull(_backend.Data);
        Assert.Equal(Creds, store.Load());
        Assert.True(File.Exists(legacyPath));
    }

    [Fact]
    public void Site_and_email_are_not_written_to_settings()
    {
        var json = JsonSerializer.Serialize(new AppSettings());
        Assert.DoesNotContain("SiteUrl", json);
        Assert.DoesNotContain("Email", json);

        // ...but a v1 file that still has them can be read for migration.
        var legacy = JsonSerializer.Deserialize<AppSettings>("""{"SiteUrl":"acme","Email":"me@acme.com"}""")!;
        Assert.Equal("acme", legacy.SiteUrl);
    }
}

public class ThrottlingTests
{
    private static (JiraClient Client, FakeHandler Handler, List<TimeSpan> Delays) Create()
    {
        var handler = new FakeHandler();
        var delays = new List<TimeSpan>();
        var client = new JiraClient(new HttpClient(handler),
            new JiraConnection(new Uri("https://acme.atlassian.net/"), "me@acme.com", "secret"),
            (delay, _) => { delays.Add(delay); return Task.CompletedTask; });
        return (client, handler, delays);
    }

    [Fact]
    public async Task Rate_limited_request_is_retried_after_retry_after()
    {
        var (client, handler, delays) = Create();
        handler.Respond("", HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(7));
        handler.Respond("""{"accountId":"a","displayName":"Pat"}""");

        var me = await client.GetMyselfAsync();

        Assert.Equal("Pat", me.DisplayName);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(7)], delays);
    }

    [Fact]
    public async Task Backs_off_exponentially_then_gives_up()
    {
        var (client, handler, delays) = Create();
        for (var i = 0; i <= JiraClient.MaxThrottleRetries; i++)
            handler.Respond("", HttpStatusCode.TooManyRequests);

        var ex = await Assert.ThrowsAsync<JiraApiException>(() => client.AddWorklogAsync("ABC-1", DateTimeOffset.Now, 60));

        Assert.Equal(429, ex.StatusCode);
        Assert.Equal(JiraClient.MaxThrottleRetries + 1, handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)], delays);
        // Every retry re-sent the same body.
        Assert.All(handler.Requests, r => Assert.Contains("\"timeSpentSeconds\":60", r.Body));
    }

    [Fact]
    public async Task Retry_after_is_capped()
    {
        var (client, handler, delays) = Create();
        handler.Respond("", HttpStatusCode.ServiceUnavailable, retryAfter: TimeSpan.FromHours(1));
        handler.Respond("{}");

        await client.GetMyselfAsync();

        Assert.Equal([TimeSpan.FromSeconds(60)], delays);
    }

    [Fact]
    public async Task Attachment_upload_is_rebuilt_for_each_retry()
    {
        using var dir = new TempDir();
        var file = dir.File("shot.png");
        File.WriteAllBytes(file, [1, 2, 3]);
        var (client, handler, _) = Create();
        handler.Respond("", HttpStatusCode.TooManyRequests);
        handler.Respond("""[{"filename":"shot.png"}]""");

        Assert.Equal("shot.png", await client.AddAttachmentAsync("ABC-1", file));
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal("no-check", r.Headers["X-Atlassian-Token"]));
    }

    [Fact]
    public async Task Rejected_token_is_reported_as_auth_failure()
    {
        var handler = new FakeHandler().Respond("{}", HttpStatusCode.Unauthorized);
        var client = new JiraClient(new HttpClient(handler),
            new JiraConnection(new Uri("https://acme.atlassian.net/"), "me@acme.com", "expired"));
        using var dir = new TempDir();
        var service = new WorklogService(() => client,
            new WorklogQueue(new JsonFileStore<List<PendingWorklog>>(dir.File("q.json"))), () => ShortSessionMode.Skip);
        var start = DateTimeOffset.UtcNow;

        var result = await service.LogAsync(new CompletedSession("ABC-1", start, start.AddMinutes(5)));

        Assert.Equal(WorklogOutcome.Queued, result.Outcome);
        Assert.True(result.IsAuthFailure);
        Assert.Equal(1, service.PendingCount);
    }
}

public class PreviewHardeningTests
{
    [Fact]
    public void Remote_images_are_not_loaded()
    {
        var html = WikiPreview.ToHtml("!https://tracker.example/pixel.gif!", _ => "https://should-not-be-used");
        Assert.DoesNotContain("<img", html);
        Assert.Contains("remote image", html);
    }

    [Fact]
    public void Document_has_a_restrictive_content_security_policy()
    {
        var doc = WikiPreview.ToDocument("hi", imageOrigin: "https://attachments.local");
        Assert.Contains("Content-Security-Policy", doc);
        Assert.Contains("default-src 'none'", doc);
        Assert.Contains("img-src https://attachments.local", doc);

        Assert.Contains("img-src 'none'", WikiPreview.ToDocument("hi"));
    }
}
