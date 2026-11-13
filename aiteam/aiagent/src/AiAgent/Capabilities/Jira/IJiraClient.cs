namespace AiAgent.Capabilities.Jira;

public interface IJiraClient
{
    Task<string> CreateIssueAsync(string projectKey, string title, string description, CancellationToken cancellationToken);
    Task<IReadOnlyList<JiraIssue>> SearchAsync(string jql, CancellationToken cancellationToken);
    Task<JiraIssue> GetIssueAsync(string issueKey, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetCommentsAsync(string issueKey, CancellationToken cancellationToken);
    Task AddLabelAsync(string issueKey, string label, CancellationToken cancellationToken);
    Task RemoveLabelAsync(string issueKey, string label, CancellationToken cancellationToken);
    Task AssignAsync(string issueKey, string accountId, CancellationToken cancellationToken);
    Task AddCommentAsync(string issueKey, string text, CancellationToken cancellationToken);
    Task TransitionToAsync(string issueKey, string statusName, CancellationToken cancellationToken);
}
