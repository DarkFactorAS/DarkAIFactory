namespace CodingAgent;

internal sealed record JiraIssue(string Key, string Summary, string Description, string StatusName = "");
