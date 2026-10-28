using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JiraTaskBar.Core.Jira;
using Wpf.Ui.Controls;

namespace JiraTaskBar.App.Services;

/// <summary>
/// Loads each issue type's icon from Jira once per session (the same icons Jira shows,
/// including custom types). Anything that can't be loaded falls back to <see cref="TypeGlyph"/>.
/// </summary>
public sealed class IssueTypeIcons
{
    private readonly Func<IJiraClient?> _client;
    private readonly Dictionary<string, Task<ImageSource?>> _cache = new();
    private bool _failureLogged;

    public IssueTypeIcons(Func<IJiraClient?> client) => _client = client;

    /// <summary>Forget cached icons (e.g. after switching site or token).</summary>
    public void Reset()
    {
        _cache.Clear();
        _failureLogged = false;
    }

    public Task<ImageSource?> GetAsync(string? avatarId)
    {
        if (avatarId is null || _client() is not { } client) return Task.FromResult<ImageSource?>(null);
        if (!_cache.TryGetValue(avatarId, out var task))
            _cache[avatarId] = task = LoadAsync(client, avatarId);
        return task;
    }

    private async Task<ImageSource?> LoadAsync(IJiraClient client, string avatarId)
    {
        try
        {
            var bytes = await client.GetIssueTypeIconAsync(avatarId);
            if (bytes is null) return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 32; // crisp at 16 DIP up to 200% scaling
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            // Typically a scoped token without avatar access, or a non-PNG icon; glyphs are used instead.
            if (!_failureLogged)
            {
                Log.Info($"Issue type icon {avatarId} unavailable, using built-in glyphs: {ex.Message}");
                _failureLogged = true;
            }
            return null;
        }
    }
}

/// <summary>Built-in icon for an issue type, chosen from its name, used when Jira's icon isn't available.</summary>
public static class TypeGlyph
{
    private static readonly (string[] Words, SymbolRegular Symbol, Color Color)[] Rules =
    [
        (["post-incident", "post incident", "pir"], SymbolRegular.DocumentSearch20, Rgb(0x8F, 0x7E, 0xE7)),
        (["sub-task", "subtask"], SymbolRegular.TextBulletListTree20, Rgb(0x46, 0x88, 0xEC)),
        (["incident"], SymbolRegular.Alert20, Rgb(0xE5, 0x48, 0x4D)),
        (["problem"], SymbolRegular.Warning20, Rgb(0xE5, 0x48, 0x4D)),
        (["bug", "defect"], SymbolRegular.Bug20, Rgb(0xE5, 0x48, 0x4D)),
        (["change"], SymbolRegular.ArrowSwap20, Rgb(0x8F, 0x7E, 0xE7)),
        (["service request", "request", "access"], SymbolRegular.Chat20, Rgb(0x22, 0xA0, 0x6B)),
        (["security", "vulnerab"], SymbolRegular.ShieldCheckmark20, Rgb(0x62, 0x6F, 0x86)),
        (["epic"], SymbolRegular.Flash20, Rgb(0x8F, 0x7E, 0xE7)),
        (["story"], SymbolRegular.Bookmark20, Rgb(0x22, 0xA0, 0x6B)),
        (["maintenance", "bau"], SymbolRegular.Wrench20, Rgb(0xF5, 0x8A, 0x2E)),
        (["renewal"], SymbolRegular.ArrowRepeatAll20, Rgb(0x8F, 0x7E, 0xE7)),
        (["improvement"], SymbolRegular.Lightbulb20, Rgb(0x46, 0x88, 0xEC)),
        (["time"], SymbolRegular.Clock20, Rgb(0x46, 0x88, 0xEC)),
        (["task", "work package"], SymbolRegular.CheckboxChecked20, Rgb(0x46, 0x88, 0xEC)),
    ];

    private static readonly Dictionary<Color, Brush> Brushes = new();

    public static (SymbolRegular Symbol, Brush Brush) For(string? issueTypeName)
    {
        var name = (issueTypeName ?? string.Empty).ToLowerInvariant();
        foreach (var (words, symbol, color) in Rules)
            if (words.Any(name.Contains))
                return (symbol, BrushFor(color));
        return (SymbolRegular.Document20, BrushFor(Rgb(0x8C, 0x8C, 0x8C)));
    }

    private static Brush BrushFor(Color color)
    {
        lock (Brushes)
        {
            if (!Brushes.TryGetValue(color, out var brush))
            {
                brush = new SolidColorBrush(color);
                brush.Freeze();
                Brushes[color] = brush;
            }
            return brush;
        }
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
}
