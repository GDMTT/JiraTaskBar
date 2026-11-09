using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using JiraTaskBar.App.Services;
using JiraTaskBar.Core.Jira;

namespace JiraTaskBar.App.Views;

public sealed class AskRovoWindow : Window
{
    public AskRovoWindow(JiraIssue issue, JiraConnection connection)
    {
        Title = $"Ask Rovo — {issue.Key}";
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        Width = 540;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new Thickness(20) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = $"Ticket: {issue.Key}", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = "1. Enter your question and copy it.\n2. Open this ticket in Jira, select Ask Rovo, and paste the question.\nAnswers appear in your browser; JiraTaskBar does not submit the prompt automatically.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10),
        });
        var question = new TextBox { Text = "Summarise this ticket and explain its blockers and next steps.", AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, Height = 110, MaxLength = 10000, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(question, "Your Rovo question");
        panel.Children.Add(question);
        var context = new TextBox { Text = connection.BrowseUrl(issue.Key).AbsoluteUri, IsReadOnly = true, Margin = new Thickness(0, 8, 0, 8) };
        AutomationProperties.SetName(context, "Ticket URL");
        panel.Children.Add(context);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        panel.Children.Add(status);
        void SetStatus(string message)
        {
            status.Text = message;
            UIElementAutomationPeer.FromElement(status)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; panel.Children.Add(buttons);
        var copy = new Button { Content = "Copy question", Padding = new Thickness(8, 4, 8, 4) }; buttons.Children.Add(copy);
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(RovoPrompt.Build(connection, issue.Key, question.Text)); SetStatus("Copied with ticket context. Paste into Rovo in Jira."); }
            catch (ArgumentException ex) { SetStatus(ex.Message); }
            catch (System.Runtime.InteropServices.COMException) { SetStatus("Clipboard is busy. Please try copying again."); }
        };
        var open = new Button { Content = "Open ticket in Jira", Margin = new Thickness(8, 0, 8, 0), Padding = new Thickness(8, 4, 8, 4) }; buttons.Children.Add(open);
        open.Click += (_, _) => SetStatus(Shell.OpenUrl(connection.BrowseUrl(issue.Key))
            ? "In Jira, select Ask Rovo and paste your question. Sign in there if required."
            : "Could not open the browser. Copy the ticket URL above and open it manually.");
        var close = new Button { Content = "Close", IsCancel = true, Padding = new Thickness(8, 4, 8, 4) }; buttons.Children.Add(close);
        panel.Children.Add(new TextBlock
        {
            Text = "Requires Jira Cloud and Rovo access in your browser account. If Ask Rovo is missing or access is denied, contact your site admin about availability, plan, permissions, and Rovo credits. Network and sign-in errors appear in Jira. No API token is copied or sent to another AI service. Your timer keeps running.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), Opacity = 0.75,
        });
    }
}
