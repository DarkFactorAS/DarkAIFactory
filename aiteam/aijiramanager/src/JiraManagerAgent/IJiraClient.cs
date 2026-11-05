namespace JiraManagerAgent;

internal interface IJiraClient
{
    Task<string> CreateIssueAsync(
        string projectKey, string title, string description, CancellationToken cancellationToken);

    Task TransitionToAsync(string issueKey, string statusName, CancellationToken cancellationToken);
}
