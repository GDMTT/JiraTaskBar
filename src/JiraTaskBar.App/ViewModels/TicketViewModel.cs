using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JiraTaskBar.Core.Jira;

namespace JiraTaskBar.App.ViewModels;

public sealed partial class TicketViewModel : ObservableObject
{
    private static readonly Brush[] RankBrushes =
    [
        Frozen(0xE5, 0x48, 0x4D), // 1 – red
        Frozen(0xF5, 0x8A, 0x2E), // 2 – orange
        Frozen(0xE2, 0xB2, 0x03), // 3 – amber
        Frozen(0x35, 0x8C, 0xE8), // 4 – blue
        Frozen(0x6B, 0x9E, 0x78), // 5 – green
    ];
    private static readonly Brush NoPriorityBrush = Frozen(0x8C, 0x8C, 0x8C);

    private readonly Action<TicketViewModel> _toggle;
    private readonly Action<TicketViewModel> _open;
    private readonly Action<TicketViewModel> _comment;
    private readonly Func<bool> _showTitles;
    private readonly Func<bool> _canComment;

    /// <param name="showTitles">Whether the summary appears on the button (it is always in the tooltip).</param>
    public TicketViewModel(JiraIssue issue, Action<TicketViewModel> toggle, Action<TicketViewModel> open,
        Action<TicketViewModel> comment, Func<bool>? showTitles = null, Func<bool>? canComment = null)
    {
        _issue = issue;
        _toggle = toggle;
        _open = open;
        _comment = comment;
        _showTitles = showTitles ?? (() => true);
        _canComment = canComment ?? (() => true);
    }

    public static JiraIssue Placeholder(string key) => new(key, "(not in the current ticket query)", null, string.Empty, string.Empty, string.Empty);

    public string Key => Issue.Key;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(PriorityLabel), nameof(PriorityBrush), nameof(MetaLine), nameof(SubtitleText), nameof(HasSubtitle))]
    [NotifyPropertyChangedFor(nameof(TypeGlyph), nameof(TypeGlyphBrush))]
    private JiraIssue _issue;

    /// <summary>The issue type's icon from Jira, once loaded; null shows <see cref="TypeGlyph"/> instead.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTypeIcon), nameof(ShowTypeGlyph))]
    private ImageSource? _typeIcon;

    public bool HasTypeIcon => TypeIcon is not null;

    public bool ShowTypeGlyph => TypeIcon is null;

    public Wpf.Ui.Controls.SymbolRegular TypeGlyph => Services.TypeGlyph.For(Issue.IssueType).Symbol;

    public Brush TypeGlyphBrush => Services.TypeGlyph.For(Issue.IssueType).Brush;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubtitleText), nameof(HasSubtitle))]
    private bool _isActive;

    [ObservableProperty]
    private bool _isStale;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubtitleText), nameof(HasSubtitle))]
    private string _elapsedText = string.Empty;

    public string Summary => Issue.Summary;

    public string PriorityLabel => Issue.Priority?.Name ?? "No priority";

    public Brush PriorityBrush =>
        Issue.Priority is { Rank: >= 1 } p && p.Rank <= RankBrushes.Length ? RankBrushes[p.Rank - 1] : NoPriorityBrush;

    public string MetaLine => string.Join(" · ",
        new[] { Issue.IssueType, PriorityLabel, Issue.Status }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>Second line on the button: running time when active, otherwise the summary (if titles are shown).</summary>
    public string SubtitleText => IsActive ? "● " + ElapsedText : _showTitles() ? Summary : string.Empty;

    public bool HasSubtitle => SubtitleText.Length > 0;

    /// <summary>Call after the show-titles setting changes.</summary>
    public void RefreshSubtitle()
    {
        OnPropertyChanged(nameof(SubtitleText));
        OnPropertyChanged(nameof(HasSubtitle));
    }

    [RelayCommand]
    private void Toggle() => _toggle(this);

    [RelayCommand]
    private void OpenInBrowser() => _open(this);

    [RelayCommand(CanExecute = nameof(CanComment))]
    private void Comment() => _comment(this);

    private bool CanComment() => _canComment();

    public void RefreshConnectionState() => CommentCommand.NotifyCanExecuteChanged();

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
