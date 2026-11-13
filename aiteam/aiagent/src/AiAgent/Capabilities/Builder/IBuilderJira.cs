namespace AiAgent.Capabilities.Builder;

/// <summary>The Jira operations the Builder needs.</summary>
internal interface IBuilderJira : IDisposable
{
    Task<IReadOnlyList<BuilderIssue>> FindWorkAsync(string jql, CancellationToken cancellationToken);
    Task<BuilderIssue> GetIssueAsync(string issueKey, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetCommentsAsync(string issueKey, CancellationToken cancellationToken);
    Task AddLabelAsync(string issueKey, string label, CancellationToken cancellationToken);
    Task RemoveLabelAsync(string issueKey, string label, CancellationToken cancellationToken);
    Task AssignAsync(string issueKey, string accountId, CancellationToken cancellationToken);
    Task AddCommentAsync(string issueKey, string text, CancellationToken cancellationToken);
    Task TransitionToAsync(string issueKey, string statusName, CancellationToken cancellationToken);
}
