using System.Globalization;
using System.Reflection;
using System.Windows;
using JiraTaskBar.App.Services;

namespace JiraTaskBar.App.Views;

public partial class AboutWindow
{
    private const string Repository = "https://github.com/GDMTT/JiraTaskBar";

    public AboutWindow()
    {
        InitializeComponent();
        var assembly = typeof(AboutWindow).Assembly;
        ProductName.Text = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "Jira TaskBar";
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(item => item.Key, item => item.Value);
        ReleaseVersion.Text = $"Version: {metadata.GetValueOrDefault("ReleaseVersion", "Unknown")}";
        var buildTime = DateTimeOffset.TryParse(metadata.GetValueOrDefault("BuildTimeUtc"),
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp)
            ? timestamp.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)
            : "Unknown";
        BuildTime.Text = $"Built: {buildTime}";
    }

    private void Repository_Click(object sender, RoutedEventArgs e) => Shell.OpenUrl(Repository);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
