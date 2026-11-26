using System.Text.RegularExpressions;

namespace JiraTaskBar.App.Services;

internal static partial class TicketInput
{
    public const int MaxUrlLength = 2048;
    public const int MaxSearchLength = 128;

    public static string? ParseKey(string text) =>
        text.Length <= MaxSearchLength && KeyPattern().IsMatch(text) ? text.ToUpperInvariant() : null;

    public static string? KeyFromUrl(string text, Uri site)
    {
        if (text.Length > MaxUrlLength || text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\') ||
            !Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(url.IdnHost, site.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            url.Port != site.Port || url.UserInfo.Length != 0)
            return null;

        // Compare the original path so URI normalization cannot hide dot segments or encoded separators.
        const string scheme = "https://";
        if (!text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;
        var pathStart = text.IndexOf('/', scheme.Length);
        var suffixStart = text.IndexOfAny(['?', '#'], scheme.Length);
        if (pathStart < 0 || (suffixStart >= 0 && pathStart >= suffixStart) ||
            text.AsSpan(scheme.Length, pathStart - scheme.Length).Contains('@'))
            return null;

        var path = text[pathStart..(suffixStart < 0 ? text.Length : suffixStart)];
        var prefix = site.AbsolutePath.TrimEnd('/') + "/browse/";
        return path.StartsWith(prefix, StringComparison.Ordinal) ? ParseKey(path[prefix.Length..]) : null;
    }

    [GeneratedRegex(@"\A[A-Za-z][A-Za-z0-9_]*-[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
