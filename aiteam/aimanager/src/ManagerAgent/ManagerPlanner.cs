using AiTeam.Configuration;
using ManagerAgent.Repository;

namespace ManagerAgent;

internal sealed class ManagerPlanner(
    AgentConfiguration configuration,
    ManagerPlanRepository plans,
    JiraClient jira,
    Func<JiraIssue, CancellationToken, Task<string>> generatePlan)
{
    public async Task PlanAsync(long projectId, JiraIssue candidate, CancellationToken cancellationToken)
    {
        var issue = await jira.GetIssueAsync(candidate.Key, cancellationToken);
        if (issue.Labels.Contains(configuration.WorkerPickupLabel) ||
            issue.Labels.Contains(configuration.FailureLabel) ||
            !string.Equals(issue.StatusName, candidate.StatusName, StringComparison.Ordinal))
        {
            return;
        }
        var requirementsHash = ManagerPlan.Fingerprint(issue.Summary, issue.Description, configuration.GitHubRepositoryUrl);
        var storedPlan = plans.Find(issue.Key);
        if (storedPlan is not null && storedPlan.ManagerProjectId != projectId)
        {
            plans.RebindProject(projectId, issue.Key);
            storedPlan = storedPlan with { ManagerProjectId = projectId };
        }

        if (storedPlan is null || !storedPlan.IsCurrent(issue.Summary, issue.Description, configuration.GitHubRepositoryUrl))
        {
            await jira.RemoveLabelAsync(issue.Key, configuration.AnalyzedLabel, cancellationToken);
            if (configuration.PickupLabel != configuration.AnalyzedLabel)
            {
                await jira.RemoveLabelAsync(issue.Key, configuration.PickupLabel, cancellationToken);
            }
            var plan = await generatePlan(issue, cancellationToken);
            if (string.IsNullOrWhiteSpace(plan))
            {
                throw new InvalidDataException("The manager returned an empty implementation plan.");
            }
            var latest = await jira.GetIssueAsync(issue.Key, cancellationToken);
            if (latest.Labels.Contains(configuration.WorkerPickupLabel) ||
                latest.Labels.Contains(configuration.FailureLabel) ||
                !string.Equals(latest.StatusName, issue.StatusName, StringComparison.Ordinal) ||
                ManagerPlan.Fingerprint(latest.Summary, latest.Description, configuration.GitHubRepositoryUrl) != requirementsHash)
            {
                Console.WriteLine($"Discarding plan for {issue.Key}: the issue changed or was claimed during planning.");
                return;
            }
            plans.Save(projectId, issue.Key, plan, requirementsHash, issue.UpdatedAt);
            storedPlan = new ManagerPlan(plan, false, requirementsHash, issue.UpdatedAt);
        }
        else if (storedPlan.PostedToJira &&
            issue.Labels.Contains(configuration.AnalyzedLabel) &&
            issue.Labels.Contains(configuration.PickupLabel))
        {
            return;
        }

        if (!storedPlan.PostedToJira)
        {
            foreach (var comment in PromptBuilder.BuildPlanComments(storedPlan.Plan))
            {
                await jira.AddCommentAsync(issue.Key, comment, cancellationToken);
            }
            plans.MarkPostedToJira(issue.Key, requirementsHash);
        }

        var beforeHandoff = await jira.GetIssueAsync(issue.Key, cancellationToken);
        if (beforeHandoff.Labels.Contains(configuration.WorkerPickupLabel) ||
            beforeHandoff.Labels.Contains(configuration.FailureLabel) ||
            !string.Equals(beforeHandoff.StatusName, issue.StatusName, StringComparison.Ordinal) ||
            !storedPlan.IsCurrent(beforeHandoff.Summary, beforeHandoff.Description, configuration.GitHubRepositoryUrl))
        {
            Console.WriteLine($"Deferring handoff for {issue.Key}: the issue changed or was claimed.");
            return;
        }
        if (configuration.PickupLabel != configuration.AnalyzedLabel)
        {
            await jira.AddLabelAsync(issue.Key, configuration.PickupLabel, cancellationToken);
        }
        await jira.AddLabelAsync(issue.Key, configuration.AnalyzedLabel, cancellationToken);
        Console.WriteLine($"Plan for {issue.Key} stored in Jira and manager_plans.");
    }
}
