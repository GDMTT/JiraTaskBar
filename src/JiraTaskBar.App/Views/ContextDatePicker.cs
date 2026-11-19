using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace JiraTaskBar.App.Views;

public sealed class ContextDatePicker : DatePicker
{
    private DatePickerTextBox? _editor;

    public override void OnApplyTemplate()
    {
        if (_editor is not null)
        {
            _editor.RemoveHandler(KeyDownEvent, new KeyEventHandler(Editor_KeyDown));
            _editor.RemoveHandler(LostFocusEvent, new RoutedEventHandler(Editor_LostFocus));
        }

        base.OnApplyTemplate();
        _editor = GetTemplateChild("PART_TextBox") as DatePickerTextBox;
        if (_editor is null) return;

        // DatePicker installs its commit handlers first. Restore the display format after they run.
        _editor.AddHandler(KeyDownEvent, new KeyEventHandler(Editor_KeyDown), handledEventsToo: true);
        _editor.AddHandler(LostFocusEvent, new RoutedEventHandler(Editor_LostFocus), handledEventsToo: true);
        FormatEditor();
    }

    protected override void OnSelectedDateChanged(SelectionChangedEventArgs e)
    {
        FormatEditor();
        base.OnSelectedDateChanged(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (ReferenceEquals(e.OriginalSource, _editor) && IsCommitKey(e))
            NormalizeEditor();
    }

    protected override void OnPreviewLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnPreviewLostKeyboardFocus(e);
        if (ReferenceEquals(e.OldFocus, _editor))
            NormalizeEditor();
    }

    protected override void OnCalendarOpened(RoutedEventArgs e)
    {
        FormatEditor();
        base.OnCalendarOpened(e);
    }

    private void Editor_KeyDown(object sender, KeyEventArgs e)
    {
        if (IsCommitKey(e))
            FormatEditor();
    }

    private void Editor_LostFocus(object sender, RoutedEventArgs e) => FormatEditor();

    private static bool IsCommitKey(KeyEventArgs e) =>
        e.Key == Key.Enter || (e.Key == Key.System && e.SystemKey == Key.Down);

    private void NormalizeEditor()
    {
        // The standard en-GB parser accepts slashes. Keep its existing validation and date selection.
        if (_editor is { } editor && editor.Text.Contains('\\'))
            editor.SetCurrentValue(DatePickerTextBox.TextProperty, editor.Text.Replace('\\', '/'));
    }

    private void FormatEditor()
    {
        if (_editor is { } editor && SelectedDate is { } date)
            editor.SetCurrentValue(DatePickerTextBox.TextProperty, date.ToString(@"dd\\MM\\yyyy", CultureInfo.InvariantCulture));
    }
}
