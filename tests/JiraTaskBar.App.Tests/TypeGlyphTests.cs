using JiraTaskBar.App.Services;
using Wpf.Ui.Controls;

namespace JiraTaskBar.App.Tests;

public class TypeGlyphTests
{
    // The work types from the Jira site's "Change work type" menu.
    [Theory]
    [InlineData("Work Package", SymbolRegular.CheckboxChecked20)]
    [InlineData("Service request", SymbolRegular.Chat20)]
    [InlineData("Incident", SymbolRegular.Alert20)]
    [InlineData("Maintenance", SymbolRegular.Wrench20)]
    [InlineData("Renewal", SymbolRegular.ArrowRepeatAll20)]
    [InlineData("Problem", SymbolRegular.Warning20)]
    [InlineData("Continuous Improvement", SymbolRegular.Lightbulb20)]
    [InlineData("Project - Time", SymbolRegular.Clock20)]
    [InlineData("Change", SymbolRegular.ArrowSwap20)]
    [InlineData("Post-incident review", SymbolRegular.DocumentSearch20)]
    [InlineData("Task", SymbolRegular.CheckboxChecked20)]
    [InlineData("Sub-task", SymbolRegular.TextBulletListTree20)]
    [InlineData("Security", SymbolRegular.ShieldCheckmark20)]
    [InlineData("JLM", SymbolRegular.Document20)]
    [InlineData(null, SymbolRegular.Document20)]
    public void Fallback_glyph_matches_the_type_name(string? typeName, SymbolRegular expected)
    {
        Assert.Equal(expected, TypeGlyph.For(typeName).Symbol);
    }
}
