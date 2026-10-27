using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JiraTaskBar.App.Interop;
using JiraTaskBar.App.Services;
using JiraTaskBar.Core.Jira;
using JiraTaskBar.Core.Markup;
using JiraTaskBar.Core.Storage;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using static JiraTaskBar.App.Interop.NativeMethods;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;
using TextBox = System.Windows.Controls.TextBox;

namespace JiraTaskBar.App.Views;

public sealed class PendingAttachment(string fileName, string localPath)
{
    public string FileName { get; } = fileName;
    public string LocalPath { get; } = localPath;
    public bool IsImage { get; } = ImageExtensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());
    public bool Uploaded { get; set; }
    public string Icon => IsImage ? "🖼" : "📎";

    private static readonly HashSet<string> ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg"];
}

public partial class CommentWindow : FluentWindow
{
    public static readonly RoutedCommand PostCommand = new();
    public static readonly RoutedCommand CancelCommand = new();

    private const string PreviewHost = "attachments.jirataskbar.local";
    private const string PreviewFile = "__preview.html";
    private const int TableMaxRows = 8;
    private const int TableMaxColumns = 8;

    private readonly JiraIssue _issue;
    private readonly IJiraClient _client;
    private readonly string _workDir;
    private readonly ObservableCollection<PendingAttachment> _attachments = [];
    private bool _previewReady;
    private bool _busy;
    private bool _allowClose;

    public CommentWindow(JiraIssue issue, IJiraClient client)
    {
        InitializeComponent();
        SystemThemeWatcher.Watch(this);
        _issue = issue;
        _client = client;
        _workDir = Path.Combine(AppPaths.DataDirectory, "pending", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);

        Title = $"Comment on {issue.Key}";
        TitleBarControl.Title = Title;
        IssueKeyText.Text = issue.Key;
        IssueSummaryText.Text = issue.Summary;
        IssueSummaryText.ToolTip = issue.Summary;
        VisibilityPanel.Visibility = issue.IsServiceDesk ? Visibility.Visible : Visibility.Collapsed;
        AttachmentList.ItemsSource = _attachments;

        CommandBindings.Add(new CommandBinding(PostCommand, async (_, _) => await PostAsync()));
        CommandBindings.Add(new CommandBinding(CancelCommand, (_, _) => Close()));

        // Images / files on the clipboard become attachments instead of being ignored by the TextBox.
        CommandManager.AddPreviewCanExecuteHandler(Editor, OnPreviewCanPaste);
        CommandManager.AddPreviewExecutedHandler(Editor, OnPreviewPaste);

        BuildTablePicker();
        Loaded += (_, _) => Editor.Focus();
    }

    /// <summary>Shows the window beside the bar, on the side with the most room.</summary>
    public void ShowNextTo(IntPtr barHwnd)
    {
        WindowStartupLocation = barHwnd == IntPtr.Zero ? WindowStartupLocation.CenterScreen : WindowStartupLocation.Manual;
        Show();
        if (barHwnd == IntPtr.Zero) return;

        var hwnd = new WindowInteropHelper(this).Handle;
        GetWindowRect(barHwnd, out var bar);
        GetWindowRect(hwnd, out var me);
        GetCursorPos(out var cursor);
        var wa = MonitorInfo.FromWindow(barHwnd).WorkArea;
        var gap = 8;
        int w = me.Width, h = me.Height;

        int x, y;
        if (bar.Right + gap + w <= wa.Right && bar.Height > bar.Width)
            (x, y) = (bar.Right + gap, cursor.Y - h / 3);
        else if (bar.Left - gap - w >= wa.Left && bar.Height > bar.Width)
            (x, y) = (bar.Left - gap - w, cursor.Y - h / 3);
        else if (bar.Bottom + gap + h <= wa.Bottom)
            (x, y) = (cursor.X - w / 2, bar.Bottom + gap);
        else
            (x, y) = (cursor.X - w / 2, bar.Top - gap - h);

        x = Math.Clamp(x, wa.Left, Math.Max(wa.Left, wa.Right - w));
        y = Math.Clamp(y, wa.Top, Math.Max(wa.Top, wa.Bottom - h));
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER);
        Activate();
    }

    // ---- Formatting ---------------------------------------------------------------------

    private void Bold_Click(object sender, RoutedEventArgs e) => Wrap("*", "*", "bold text");
    private void Italic_Click(object sender, RoutedEventArgs e) => Wrap("_", "_", "italic text");
    private void Underline_Click(object sender, RoutedEventArgs e) => Wrap("+", "+", "underlined text");
    private void Strike_Click(object sender, RoutedEventArgs e) => Wrap("-", "-", "deleted text");
    private void InlineCode_Click(object sender, RoutedEventArgs e) => Wrap("{{", "}}", "code");
    private void Link_Click(object sender, RoutedEventArgs e) => InsertLink();
    private void Heading_Click(object sender, RoutedEventArgs e) => PrefixLines("h3. ");
    private void Bullets_Click(object sender, RoutedEventArgs e) => PrefixLines("* ");
    private void Numbers_Click(object sender, RoutedEventArgs e) => PrefixLines("# ");
    private void Quote_Click(object sender, RoutedEventArgs e) => PrefixLines("bq. ");

    private void CodeBlock_Click(object sender, RoutedEventArgs e)
    {
        var before = NeedsLeadingNewline() ? "\n" : string.Empty;
        Wrap(before + "{code}\n", "\n{code}\n", "code");
    }

    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        switch (e.Key)
        {
            case Key.B: Wrap("*", "*", "bold text"); break;
            case Key.I: Wrap("_", "_", "italic text"); break;
            case Key.U: Wrap("+", "+", "underlined text"); break;
            case Key.K: InsertLink(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void InsertLink()
    {
        var selected = Editor.SelectedText;
        if (Uri.TryCreate(selected, UriKind.Absolute, out _))
            Wrap("[", "]", selected);
        else
            Wrap("[", "|https://]", "link text");
    }

    /// <summary>Wraps the selection (or a selected placeholder) in markup.</summary>
    private void Wrap(string prefix, string suffix, string placeholder)
    {
        var start = Editor.SelectionStart;
        var selected = Editor.SelectedText;
        var body = selected.Length > 0 ? selected : placeholder;
        Editor.SelectedText = prefix + body + suffix;
        Editor.Select(start + prefix.Length, body.Length);
        Editor.Focus();
    }

    /// <summary>Adds a block prefix (h3., *, #, bq.) to every selected line.</summary>
    private void PrefixLines(string prefix)
    {
        var text = Editor.Text;
        var selStart = Editor.SelectionStart;
        var selEnd = selStart + Editor.SelectionLength;
        var lineStart = selStart == 0 ? 0 : text.LastIndexOf('\n', selStart - 1) + 1;
        var lineEnd = text.IndexOf('\n', Math.Max(selEnd - (Editor.SelectionLength > 0 && selEnd > 0 && text[selEnd - 1] == '\n' ? 1 : 0), lineStart));
        if (lineEnd < 0) lineEnd = text.Length;

        var lines = text[lineStart..lineEnd].Split('\n');
        var replaced = string.Join("\n", lines.Select(l => l.StartsWith(prefix, StringComparison.Ordinal) ? l : prefix + l.TrimStart('\r')));
        Editor.Select(lineStart, lineEnd - lineStart);
        Editor.SelectedText = replaced;
        Editor.Select(lineStart + replaced.Length, 0);
        Editor.Focus();
    }

    private bool NeedsLeadingNewline()
    {
        var i = Editor.SelectionStart;
        return i > 0 && Editor.Text[i - 1] != '\n';
    }

    private void InsertAtCaret(string text)
    {
        var start = Editor.SelectionStart;
        Editor.SelectedText = text;
        Editor.Select(start + text.Length, 0);
        Editor.Focus();
    }

    // ---- Tables -------------------------------------------------------------------------

    private void BuildTablePicker()
    {
        var idle = (Brush)FindResource("ControlFillColorDefaultBrush");
        var stroke = (Brush)FindResource("ControlStrokeColorDefaultBrush");
        var hot = (Brush)FindResource("AccentFillColorDefaultBrush");

        for (var r = 0; r < TableMaxRows; r++)
        for (var c = 0; c < TableMaxColumns; c++)
        {
            var cell = new Border
            {
                Width = 18, Height = 18, Margin = new Thickness(1),
                Background = idle, BorderBrush = stroke, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2),
                Tag = (Row: r, Col: c),
            };
            cell.MouseEnter += (_, _) =>
            {
                var (hr, hc) = ((int Row, int Col))cell.Tag;
                foreach (Border b in TableGrid.Children)
                {
                    var (br, bc) = ((int Row, int Col))b.Tag;
                    b.Background = br <= hr && bc <= hc ? hot : idle;
                }
                TableSizeText.Text = $"{hc + 1} column(s) × header + {Math.Max(1, hr)} row(s)";
            };
            cell.MouseLeftButtonUp += (_, _) =>
            {
                var (tr, tc) = ((int Row, int Col))cell.Tag;
                TablePopup.IsOpen = false;
                InsertTable(columns: tc + 1, bodyRows: Math.Max(1, tr));
            };
            TableGrid.Children.Add(cell);
        }
    }

    private void Table_Click(object sender, RoutedEventArgs e) => TablePopup.IsOpen = true;

    private void InsertTable(int columns, int bodyRows)
    {
        var header = "||" + string.Join("||", Enumerable.Range(1, columns).Select(i => $"Heading {i}")) + "||";
        var row = "|" + string.Join("|", Enumerable.Repeat(" ", columns)) + "|";
        var lines = new[] { header }.Concat(Enumerable.Repeat(row, bodyRows));
        var before = NeedsLeadingNewline() ? "\n" : string.Empty;
        InsertAtCaret(before + string.Join("\n", lines) + "\n");
    }

    // ---- Images & attachments -----------------------------------------------------------

    private void Image_Click(object sender, RoutedEventArgs e) =>
        PickFiles("Images|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|All files|*.*");

    private void Attach_Click(object sender, RoutedEventArgs e) => PickFiles("All files|*.*");

    private void PickFiles(string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter, Multiselect = true };
        if (dialog.ShowDialog(this) == true)
            foreach (var file in dialog.FileNames) AddFile(file);
    }

    private void AddFile(string sourcePath)
    {
        try
        {
            var name = UniqueName(Path.GetFileName(sourcePath));
            var target = Path.Combine(_workDir, name);
            File.Copy(sourcePath, target);
            AddAttachment(new PendingAttachment(name, target));
        }
        catch (Exception ex)
        {
            ShowError($"Couldn't attach {Path.GetFileName(sourcePath)}: {ex.Message}");
        }
    }

    private void AddAttachment(PendingAttachment attachment)
    {
        _attachments.Add(attachment);
        var markup = attachment.IsImage ? $"!{attachment.FileName}|thumbnail!" : $"[^{attachment.FileName}]";
        InsertAtCaret(markup);
    }

    private string UniqueName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var name = fileName.Replace(' ', '_').Replace('|', '_').Replace('!', '_');
        for (var i = 2; _attachments.Any(a => a.FileName.Equals(name, StringComparison.OrdinalIgnoreCase))
                        || name.Equals(PreviewFile, StringComparison.OrdinalIgnoreCase); i++)
            name = $"{stem}-{i}{ext}".Replace(' ', '_');
        return name;
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PendingAttachment a) return;
        _attachments.Remove(a);
        Editor.Text = Editor.Text
            .Replace($"!{a.FileName}|thumbnail!", string.Empty)
            .Replace($"!{a.FileName}!", string.Empty)
            .Replace($"[^{a.FileName}]", string.Empty);
    }

    private static bool ClipboardHasAttachment() =>
        !Clipboard.ContainsText() && (Clipboard.ContainsImage() || Clipboard.ContainsFileDropList());

    private void OnPreviewCanPaste(object sender, CanExecuteRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste || !ClipboardHasAttachment()) return;
        e.CanExecute = true;
        e.Handled = true;
    }

    private void OnPreviewPaste(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste) return;

        // Cells copied from Excel / a web table arrive as tab-separated text: paste them as a Jira table.
        if (Clipboard.ContainsText() && WikiTables.FromTabSeparated(Clipboard.GetText()) is { } table)
        {
            e.Handled = true;
            InsertAtCaret((NeedsLeadingNewline() ? "\n" : string.Empty) + table);
            return;
        }

        if (!ClipboardHasAttachment()) return;
        e.Handled = true;

        if (Clipboard.ContainsFileDropList())
        {
            foreach (var file in Clipboard.GetFileDropList().Cast<string>()) AddFile(file);
            return;
        }

        try
        {
            var name = UniqueName($"paste-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            var path = Path.Combine(_workDir, name);
            SaveClipboardImage(path);
            AddAttachment(new PendingAttachment(name, path));
        }
        catch (Exception ex)
        {
            ShowError("Couldn't paste the image: " + ex.Message);
        }
    }

    private static void SaveClipboardImage(string path)
    {
        // Prefer the PNG clipboard format (keeps transparency, e.g. from Snipping Tool).
        if (Clipboard.GetData("PNG") is MemoryStream png)
        {
            File.WriteAllBytes(path, png.ToArray());
            return;
        }

        var image = Clipboard.GetImage() ?? throw new InvalidOperationException("No image on the clipboard.");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private void Editor_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Editor_PreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        e.Handled = true;
        foreach (var file in files.Where(File.Exists)) AddFile(file);
    }

    // ---- Preview ------------------------------------------------------------------------

    private async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Tabs || Tabs.SelectedIndex != 1) return;
        await RenderPreviewAsync();
    }

    private async Task RenderPreviewAsync()
    {
        try
        {
            if (!_previewReady)
            {
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.DataDirectory, "WebView2"));
                await PreviewView.EnsureCoreWebView2Async(env);
                PreviewView.CoreWebView2.SetVirtualHostNameToFolderMapping(PreviewHost, _workDir, CoreWebView2HostResourceAccessKind.Allow);
                PreviewView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                PreviewView.CoreWebView2.Settings.IsScriptEnabled = false;
                PreviewView.CoreWebView2.NavigationStarting += (_, args) =>
                {
                    if (args.Uri.StartsWith($"https://{PreviewHost}/", StringComparison.OrdinalIgnoreCase)) return;
                    args.Cancel = true;
                    if (args.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)) Shell.Open(args.Uri);
                };
                PreviewView.CoreWebView2.NewWindowRequested += (_, args) =>
                {
                    args.Handled = true;
                    Shell.Open(args.Uri);
                };
                _previewReady = true;
            }

            var html = WikiPreview.ToDocument(Editor.Text, name =>
                _attachments.Any(a => a.IsImage && a.FileName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ? $"https://{PreviewHost}/{Uri.EscapeDataString(name)}"
                    : null,
                App.IsDarkTheme);
            await File.WriteAllTextAsync(Path.Combine(_workDir, PreviewFile), html);
            PreviewView.CoreWebView2.Navigate($"https://{PreviewHost}/{PreviewFile}?v={Environment.TickCount64}");
        }
        catch (Exception ex)
        {
            Log.Error("Preview failed", ex);
            PreviewView.Visibility = Visibility.Collapsed;
            PreviewFallback.Visibility = Visibility.Visible;
            PreviewFallback.Text = "Preview isn't available (the Microsoft Edge WebView2 runtime may be missing). " +
                                   "Your comment will still be formatted by Jira.\n\n" + ex.Message;
        }
    }

    // ---- Posting ------------------------------------------------------------------------

    private async void Post_Click(object sender, RoutedEventArgs e) => await PostAsync();

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Closes without asking about unsent text (used on app exit).</summary>
    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    private async Task PostAsync()
    {
        if (_busy) return;
        var text = Editor.Text.Trim();
        if (text.Length == 0)
        {
            ShowError("Write a comment first.");
            return;
        }

        SetBusy(true);
        try
        {
            var toUpload = _attachments.Where(a => !a.Uploaded).ToList();
            for (var i = 0; i < toUpload.Count; i++)
            {
                var a = toUpload[i];
                ShowInfo($"Uploading {a.FileName} ({i + 1}/{toUpload.Count})…");
                var stored = await _client.AddAttachmentAsync(_issue.Key, a.LocalPath);
                a.Uploaded = true;
                if (!stored.Equals(a.FileName, StringComparison.Ordinal))
                    text = text.Replace(a.FileName, stored);
            }

            ShowInfo("Posting comment…");
            bool? isInternal = _issue.IsServiceDesk ? InternalRadio.IsChecked == true : null;
            await _client.AddCommentAsync(_issue.Key, text, isInternal);
            Log.Info($"Comment posted to {_issue.Key} (internal={isInternal})");

            _allowClose = true;
            Close();
        }
        catch (Exception ex)
        {
            Log.Error($"Posting comment to {_issue.Key} failed", ex);
            ShowError(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        PostButton.IsEnabled = !busy;
        Editor.IsReadOnly = busy;
    }

    private void ShowInfo(string message)
    {
        StatusText.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush");
        StatusText.Text = message;
    }

    private void ShowError(string message)
    {
        StatusText.Foreground = Brushes.IndianRed;
        StatusText.Text = message;
        StatusText.ToolTip = message;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_allowClose || (Editor.Text.Trim().Length == 0 && _attachments.Count == 0)) return;

        e.Cancel = true;
        if (_busy) return;
        var box = new MessageBox
        {
            Title = "Discard comment?",
            Content = $"Your comment on {_issue.Key} hasn't been posted.",
            PrimaryButtonText = "Discard",
            CloseButtonText = "Keep editing",
        };
        if (await box.ShowDialogAsync() == MessageBoxResult.Primary)
        {
            _allowClose = true;
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        PreviewView.Dispose();
        try
        {
            Directory.Delete(_workDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // WebView2 may still hold a file briefly; leftovers are harmless.
        }
    }
}
