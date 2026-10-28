using System.Net;
using System.Runtime.Versioning;
using JiraTaskBar.Core.Jira;
using JiraTaskBar.Core.Storage;

namespace JiraTaskBar.Core.Tests;

public class ScopedTokenTests
{
    private const string CloudId = "1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d";
    private static readonly Uri Site = new("https://acme.atlassian.net/");

    [Fact]
    public async Task Scoped_token_calls_go_through_the_api_gateway()
    {
        var handler = new FakeHandler().Respond("""{"accountId":"a","displayName":"Pat"}""");
        var client = new JiraClient(new HttpClient(handler), new JiraConnection(Site, "me@acme.com", "scoped", CloudId));

        await client.GetMyselfAsync();

        var url = handler.Requests.Single().Url;
        Assert.Equal("api.atlassian.com", url.Host);
        Assert.Equal($"/ex/jira/{CloudId}/rest/api/3/myself", url.AbsolutePath);
        Assert.StartsWith("Basic ", handler.Requests[0].Headers["Authorization"]);
    }

    [Fact]
    public async Task Classic_token_calls_go_to_the_site()
    {
        var handler = new FakeHandler().Respond("{}");
        var client = new JiraClient(new HttpClient(handler), new JiraConnection(Site, "me@acme.com", "classic"));

        await client.GetMyselfAsync();

        Assert.Equal("acme.atlassian.net", handler.Requests.Single().Url.Host);
    }

    [Fact]
    public void Browser_links_always_use_the_site()
    {
        var connection = new JiraConnection(Site, "me@acme.com", "scoped", CloudId);
        Assert.Equal("https://acme.atlassian.net/browse/ABC-1", connection.BrowseUrl("ABC-1").ToString());
        Assert.True(connection.IsScoped);
    }

    [Theory]
    [InlineData("../../evil")]                         // path injection into the gateway URL
    [InlineData("1a2b3c4d/../../x")]
    [InlineData("not-a-guid")]
    [InlineData("")]
    public void Cloud_id_must_be_a_guid(string input)
    {
        Assert.Throws<ArgumentException>(() => JiraConnection.NormalizeCloudId(input));
    }

    [Fact]
    public void Cloud_id_is_normalised()
    {
        Assert.Equal(CloudId, JiraConnection.NormalizeCloudId(" 1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D "));
    }

    [Fact]
    public async Task Cloud_id_lookup_sends_no_credentials()
    {
        var handler = new FakeHandler().Respond($$"""{"cloudId":"{{CloudId}}"}""");

        var id = await JiraClient.TryGetCloudIdAsync(new HttpClient(handler), Site);

        Assert.Equal(CloudId, id);
        var request = handler.Requests.Single();
        Assert.Equal("https://acme.atlassian.net/_edge/tenant_info", request.Url.ToString());
        Assert.False(request.Headers.ContainsKey("Authorization"));
    }

    [Theory]
    [InlineData("""{"cloudId":"../../evil"}""", HttpStatusCode.OK)]
    [InlineData("""{"other":1}""", HttpStatusCode.OK)]
    [InlineData("<html>login</html>", HttpStatusCode.OK)]
    [InlineData("{}", HttpStatusCode.NotFound)]
    public async Task Cloud_id_lookup_fails_safely(string body, HttpStatusCode status)
    {
        var handler = new FakeHandler().Respond(body, status);
        Assert.Null(await JiraClient.TryGetCloudIdAsync(new HttpClient(handler), Site));
    }
}

[SupportedOSPlatform("windows")]
public class ScopedCredentialStorageTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Cloud_id_is_stored_with_the_token()
    {
        var store = new SecretStore(_dir.File("credentials.bin"));
        var creds = new StoredCredentials("https://acme.atlassian.net/", "me@acme.com", "t", "1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d");

        store.Save(creds);

        Assert.Equal(creds, store.Load());
    }

    [Fact]
    public void Credentials_saved_before_scoped_support_load_as_classic()
    {
        // A blob written by the previous version has no CloudId property.
        var store = new SecretStore(_dir.File("credentials.bin"));
        store.Save(new StoredCredentials("https://acme.atlassian.net/", "me@acme.com", "t"));

        Assert.Null(store.Load()!.CloudId);
    }
}
