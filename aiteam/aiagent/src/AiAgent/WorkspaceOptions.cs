namespace AiAgent;

internal sealed record WorkspaceOptions(string Root, string RepositoryUrl, string Branch)
{
    public const string MarkerFileName = ".aiagent-fetch-complete";

    public string CheckoutPath => Path.Combine(Root, "repo");

    public static WorkspaceOptions FromEnvironment(Func<string, string?> get) => new(
        get("AIAGENT_WORKSPACE_ROOT") ?? "/workspaces",
        get("AIAGENT_REPOSITORY_URL") ?? "",
        get("AIAGENT_REPOSITORY_BRANCH") ?? "");
}
