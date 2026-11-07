namespace ClaudeCodingAgent;

internal static class WorkerIdentity
{
    /// <summary>Settings client and workflow_labels agent name of this worker.</summary>
    public const string AgentName = "aiclaudeworker";

    /// <summary>Prefix used in the "[tag] Not picked up" Jira comments.</summary>
    public const string CommentTag = "AIClaudeWorker";
}
