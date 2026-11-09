using JiraTaskBar.Core.Jira;

namespace JiraTaskBar.Core.Tests;

public class TicketFilterTests
{
    [Fact]
    public void Jira_search_preserves_unknown_and_zero_remaining_estimates()
    {
        var data = System.Text.Json.Nodes.JsonNode.Parse("""
            {"issues":[{"key":"ABC-1","fields":{"timeestimate":null}},
                       {"key":"ABC-2","fields":{"timeestimate":0}},
                       {"key":"ABC-3","fields":{"timeestimate":600}}]}
            """);
        var parsed = JiraPayloads.ParseSearch(data, new Dictionary<string, JiraPriority>()).Issues;
        Assert.Null(parsed[0].RemainingEstimateSeconds);
        Assert.Equal(0, parsed[1].RemainingEstimateSeconds);
        Assert.Equal(600, parsed[2].RemainingEstimateSeconds);
    }
    private static JiraIssue Issue(int? remaining = null) => new("ABC-123", "Fix printer connection", null, "Open", "software", "Bug", RemainingEstimateSeconds: remaining);
    [Fact]
    public void Combines_types_key_words_and_bounds_case_insensitively()
    {
        var filter = new TicketFilter { Types = ["bug", "Task"], Key = "c-12", Summary = "PRINTER fix", MinimumSeconds = 60, MaximumSeconds = 3600 };
        Assert.True(filter.Matches(Issue(600)));
        Assert.False(filter.Matches(Issue(0)));
        Assert.False(filter.Matches(Issue(null)));
        Assert.False(filter.Matches(Issue(3601)));
        Assert.False(filter.Matches(Issue(600) with { Summary = "Fix scanner" }));
    }
    [Fact]
    public void Missing_estimate_is_not_zero_and_reset_matches_everything()
    {
        Assert.True(new TicketFilter().Matches(Issue()));
        Assert.False(new TicketFilter().IsActive);
        Assert.True(new TicketFilter { MaximumSeconds = 0 }.Matches(Issue(0)));
        Assert.False(new TicketFilter { MaximumSeconds = 0 }.Matches(Issue()));
        Assert.True(new TicketFilter { Estimate = EstimateFilter.NoEstimate }.Matches(Issue()));
        Assert.False(new TicketFilter { Estimate = EstimateFilter.NoEstimate }.Matches(Issue(0)));
    }
}
