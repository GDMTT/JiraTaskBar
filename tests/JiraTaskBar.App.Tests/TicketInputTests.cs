using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using JiraTaskBar.App.Services;
using JiraTaskBar.App.Views;

namespace JiraTaskBar.App.Tests;

public class TicketInputTests
{
    private static readonly Uri CloudSite = new("https://example.atlassian.net/");
    private static readonly Uri DataCenterSite = new("https://jira.example.com:8443/team/jira/");

    [Theory]
    [InlineData("https://example.atlassian.net/browse/abc_2-123?focusedCommentId=1#comment", "ABC_2-123")]
    [InlineData("https://example.atlassian.net:443/browse/ABC-1", "ABC-1")]
    [InlineData("http://example.atlassian.net/browse/ABC-1", null)]
    [InlineData("https://else.atlassian.net/browse/ABC-1", null)]
    [InlineData("https://example.atlassian.net.evil.com/browse/ABC-1", null)]
    [InlineData("https://example.atlassian.net:8443/browse/ABC-1", null)]
    [InlineData("https://example.atlassian.net@evil.com/browse/ABC-1", null)]
    [InlineData("https://user@example.atlassian.net/browse/ABC-1", null)]
    [InlineData("https://example.atlassian.net/other/../browse/ABC-1", null)]
    [InlineData("https://example.atlassian.net/browse/%41BC-1", null)]
    [InlineData("https://example.atlassian.net/browse/ABC-1%2Fmore", null)]
    [InlineData("https://example.atlassian.net/browse/ABC-1/extra", null)]
    [InlineData("https://example.atlassian.net/issues?url=/browse/ABC-1", null)]
    [InlineData("https://example.atlassian.net/browse/ABC-1\r\nhttps://example.atlassian.net/browse/ABC-2", null)]
    public void Parses_only_canonical_ticket_urls_on_the_connected_site(string url, string? expected) =>
        Assert.Equal(expected, TicketInput.KeyFromUrl(url, CloudSite));

    [Fact]
    public void Requires_the_configured_data_center_context_path_and_port()
    {
        Assert.Equal("TEAM-7", TicketInput.KeyFromUrl("https://jira.example.com:8443/team/jira/browse/TEAM-7", DataCenterSite));
        Assert.Null(TicketInput.KeyFromUrl("https://jira.example.com:8443/browse/TEAM-7", DataCenterSite));
        Assert.Null(TicketInput.KeyFromUrl("https://jira.example.com/team/jira/browse/TEAM-7", DataCenterSite));
    }

    [Fact]
    public void Accepts_browser_url_and_text_in_managed_data_objects()
    {
        const string url = "https://example.atlassian.net/browse/ABC-1";
        using var bytes = new MemoryStream(Encoding.Unicode.GetBytes(url + '\0'));
        var native = new DataObject();
        native.SetData("UniformResourceLocatorW", bytes, autoConvert: false);

        Assert.Equal("ABC-1", BarWindow.BrowserDropKey(native, CloudSite));
        Assert.Equal("ABC-1", BarWindow.BrowserDropKey(new DataObject(DataFormats.UnicodeText, url), CloudSite));
        Assert.Null(BarWindow.BrowserDropKey(new DataObject(DataFormats.Html, url), CloudSite));
        Assert.Null(BarWindow.BrowserDropKey(new DataObject(DataFormats.UnicodeText, url + "?" + new string('a', 2048)), CloudSite));
    }

    [Theory]
    [InlineData(DragDropEffects.Copy | DragDropEffects.Link, DragDropEffects.Copy)]
    [InlineData(DragDropEffects.Link | DragDropEffects.Move, DragDropEffects.Link)]
    [InlineData(DragDropEffects.Move, DragDropEffects.None)]
    [InlineData(DragDropEffects.None, DragDropEffects.None)]
    public void Negotiates_only_copy_or_link_for_ticket_drops(DragDropEffects allowed, DragDropEffects expected) =>
        Assert.Equal(expected, BarWindow.ReadDropEffect(allowed));

    [Fact]
    public void Reads_browser_url_from_a_native_ole_data_object()
    {
        const string url = "https://example.atlassian.net/browse/ABC-1";
        var interfaceId = typeof(System.Runtime.InteropServices.ComTypes.IDataObject).GUID;
        Marshal.ThrowExceptionForHR(SHCreateDataObject(0, 0, 0, 0, in interfaceId, out var nativeData));
        try
        {
            var format = new FORMATETC
            {
                cfFormat = unchecked((short)DataFormats.GetDataFormat("UniformResourceLocatorW").Id),
                dwAspect = DVASPECT.DVASPECT_CONTENT,
                lindex = -1,
                tymed = TYMED.TYMED_HGLOBAL,
            };
            var bytes = Encoding.Unicode.GetBytes(url + '\0');
            var payload = GlobalAlloc(2, (nuint)bytes.Length);
            Assert.NotEqual(0, payload);
            try
            {
                var address = GlobalLock(payload);
                Assert.NotEqual(0, address);
                try { Marshal.Copy(bytes, 0, address, bytes.Length); }
                finally { GlobalUnlock(payload); }
                var medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = payload };
                nativeData.SetData(ref format, ref medium, release: true);
                payload = 0;
                Assert.Equal("ABC-1", BarWindow.BrowserDropKey(new DataObject(nativeData), CloudSite));
            }
            finally { if (payload != 0) GlobalFree(payload); }
        }
        finally { Marshal.ReleaseComObject(nativeData); }
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHCreateDataObject(nint folder, uint count, nint children, nint inner,
        in Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out System.Runtime.InteropServices.ComTypes.IDataObject data);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GlobalLock(nint global);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern bool GlobalUnlock(nint global);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GlobalFree(nint global);
}
