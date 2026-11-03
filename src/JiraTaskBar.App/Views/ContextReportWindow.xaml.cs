using System.Windows;
using JiraTaskBar.Core.Tracking;

namespace JiraTaskBar.App.Views;

public partial class ContextReportWindow
{
    private readonly ContextSwitchHistory _history;

    public ContextReportWindow(ContextSwitchHistory history)
    {
        InitializeComponent();
        _history = history;
        var today = DateTime.Today;
        FromDate.SelectedDate = today.AddDays(-6);
        ToDate.SelectedDate = today;
        RefreshReport();
    }

    private void Date_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => RefreshReport();

    private void LastWeek_Click(object sender, RoutedEventArgs e)
    {
        var today = DateTime.Today;
        FromDate.SelectedDate = today.AddDays(-6);
        ToDate.SelectedDate = today;
    }

    private void RefreshReport()
    {
        if (!IsInitialized || FromDate.SelectedDate is not { } from || ToDate.SelectedDate is not { } to) return;
        if (to < from)
        {
            TicketGrid.ItemsSource = null;
            TransitionGrid.ItemsSource = null;
            return;
        }

        var fromUtc = LocalMidnightUtc(from);
        var toUtc = LocalMidnightUtc(to.AddDays(1));
        var report = _history.BuildReport(fromUtc, toUtc);
        SwitchCount.Text = report.SwitchCount.ToString();
        IntervalCount.Text = report.IntervalCount.ToString();
        ActiveTime.Text = Format(report.TotalActive);
        AverageInterval.Text = Format(report.AverageInterval);
        TicketGrid.ItemsSource = report.Tickets.Select(t => new TicketRow(
            t.Key, t.Intervals, Format(t.Total), Format(t.Average))).ToList();
        TransitionGrid.ItemsSource = report.Transitions.Select(t => new TransitionRow(
            t.FromKey, t.ToKey, t.Count)).ToList();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(this,
                "Delete all locally stored context-switch intervals and transitions? This cannot be undone.",
                "Delete context-switch history", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _history.Clear();
        RefreshReport();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static DateTimeOffset LocalMidnightUtc(DateTime date) =>
        new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified), TimeZoneInfo.Local), TimeSpan.Zero);

    private static string Format(TimeSpan value) =>
        $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}";

    private sealed record TicketRow(string Key, int Intervals, string Total, string Average);
    private sealed record TransitionRow(string From, string To, int Count);
}
