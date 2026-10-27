using JiraTaskBar.Core.Markup;

namespace JiraTaskBar.Core.Tests;

public class WikiPreviewTests
{
    [Fact]
    public void Formats_inline_text()
    {
        var html = WikiPreview.ToHtml("*bold* _italic_ -gone- +under+ {{code *x*}} well-known");
        Assert.Contains("<b>bold</b>", html);
        Assert.Contains("<i>italic</i>", html);
        Assert.Contains("<s>gone</s>", html);
        Assert.Contains("<u>under</u>", html);
        Assert.Contains("<code>code *x*</code>", html);
        Assert.Contains("well-known", html);
    }

    [Fact]
    public void Escapes_html()
    {
        Assert.Contains("&lt;script&gt;", WikiPreview.ToHtml("<script>alert(1)</script>"));
    }

    [Fact]
    public void Renders_tables_with_headers()
    {
        var html = WikiPreview.ToHtml("||Name||Value||\n|a|[link|https://x.test/a]|");
        Assert.Contains("<th>Name</th><th>Value</th>", html);
        Assert.Contains("<td>a</td>", html);
        Assert.Contains("<a href=\"https://x.test/a\">link</a>", html);
    }

    [Fact]
    public void Renders_nested_lists()
    {
        var html = WikiPreview.ToHtml("* one\n** nested\n* two\n# first");
        Assert.Equal("<ul><li>one<ul><li>nested</li></ul>\n</li><li>two</li></ul>\n<ol><li>first</li></ol>\n", html);
    }

    [Fact]
    public void Renders_headings_code_and_quotes()
    {
        var html = WikiPreview.ToHtml("h2. Title\n{code:java}\nint x = *1*;\n{code}\nbq. quoted");
        Assert.Contains("<h2>Title</h2>", html);
        Assert.Contains("<pre><code>int x = *1*;</code></pre>", html);
        Assert.Contains("<blockquote>quoted</blockquote>", html);
    }

    [Fact]
    public void Converts_excel_cells_to_a_table()
    {
        Assert.Equal("||Name||Qty||\n|Widget|3|\n|Pipe a\\|b| |\n",
            WikiTables.FromTabSeparated("Name\tQty\r\nWidget\t3\r\nPipe a|b\t\r\n"));
    }

    [Theory]
    [InlineData("just one line\twith a tab")]
    [InlineData("two\nlines without tabs")]
    [InlineData("a\tb\nc\td\te")]
    public void Ignores_text_that_is_not_a_grid(string text)
    {
        Assert.Null(WikiTables.FromTabSeparated(text));
    }

    [Fact]
    public void Resolves_images()
    {
        var html = WikiPreview.ToHtml("!shot.png|thumbnail! and !old.png!",
            name => name == "shot.png" ? "https://local/shot.png" : null);
        Assert.Contains("<img src=\"https://local/shot.png\" alt=\"shot.png\" class=\"thumb\">", html);
        Assert.Contains("class=\"missing\"", html);
    }
}
