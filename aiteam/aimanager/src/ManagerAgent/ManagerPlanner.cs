using System.Text.Json;
using AiTeam.Configuration;
using DFCommonLib.Logger;
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
        if (string.Equals(issue.StatusName, candidate.StatusName, StringComparison.Ordinal))
        {
            if (issue.Labels.Contains(configuration.WorkerPickupLabel))
            {
                await ReportIgnoredAsync(issue.Key, $"it already has the \"{configuration.WorkerPickupLabel}\" label, so the worker owns it.", cancellationToken);
            }
            else if (issue.Labels.Contains(configuration.FailureLabel))
            {
                await ReportIgnoredAsync(issue.Key, $"it has the \"{configuration.FailureLabel}\" label. Remove the label to retry.", cancellationToken);
            }
        }
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

        var reusable = storedPlan is not null &&
            storedPlan.IsCurrent(issue.Summary, issue.Description, configuration.GitHubRepositoryUrl);
        var hasApprovalLabel = issue.Labels.Contains(configuration.ApprovalLabel);
        var hasApprovedLabel = issue.Labels.Contains(configuration.ApprovedLabel);
        var newlyHeld = false;
        var approvalRequired = false;
        if (storedPlan is not null && IsDependencyHold(storedPlan.Plan))
        {
            await ReportIgnoredAsync(issue.Key, "it is waiting for its dependencies to be done.", cancellationToken);
            return;
        }
        if (reusable && TriageResult.ReadMarker(storedPlan!.Plan) is { } storedDecision)
        {
            if (!storedPlan.PostedToJira && storedDecision == TriageDecision.Split)
            {
                await CompleteSplitAsync(issue.Key, storedPlan.Plan, storedPlan.RequirementsHash!, cancellationToken);
                return;
            }
            else if (storedPlan.PostedToJira && storedDecision == TriageDecision.AnalysisOnly)
            {
                if (!string.Equals(issue.StatusName, configuration.AnalysisOnlyStatus, StringComparison.OrdinalIgnoreCase))
                {
                    await jira.TransitionToAsync(issue.Key, configuration.AnalysisOnlyStatus, cancellationToken);
                }
                return;
            }
            else if (storedPlan.PostedToJira && storedDecision == TriageDecision.Split)
            {
                await ReleaseSubtasksAsync(storedPlan.Plan, cancellationToken);
                return;
            }
            else if (storedDecision == TriageDecision.NeedsApproval)
            {
                approvalRequired = true;
                if (storedPlan.PostedToJira && !hasApprovedLabel)
                {
                    await ReportIgnoredAsync(issue.Key, $"it is waiting for approval (add the \"{configuration.ApprovedLabel}\" label).", cancellationToken);
                    return;
                }
            }
            else if (storedPlan.PostedToJira && issue.Labels.Contains(configuration.ClarificationLabel))
            {
                await ReportIgnoredAsync(issue.Key, $"it is waiting for clarification (remove the \"{configuration.ClarificationLabel}\" label once answered).", cancellationToken);
                return;
            }
            else if (!storedPlan.PostedToJira && storedDecision != TriageDecision.NeedsApproval)
            {
                reusable = false;
            }
            else
            {
                reusable = false;
            }
        }

        if (!reusable)
        {
            await jira.RemoveLabelAsync(issue.Key, configuration.AnalyzedLabel, cancellationToken);
            if (configuration.PickupLabel != configuration.AnalyzedLabel)
            {
                await jira.RemoveLabelAsync(issue.Key, configuration.PickupLabel, cancellationToken);
            }
            foreach (var staleLabel in new[] { configuration.ClarificationLabel, configuration.ApprovalLabel, configuration.ApprovedLabel }
                .Where(label => issue.Labels.Contains(label)))
            {
                await jira.RemoveLabelAsync(issue.Key, staleLabel, cancellationToken);
            }
            hasApprovalLabel = false;
            hasApprovedLabel = false;
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
                DFLogger.LogOutput(DFLogLevel.INFO, "Planner", $"Discarding plan for {issue.Key}: the issue changed or was claimed during planning.");
                return;
            }

            if (TriageResult.TryParse(plan, out var triage) && triage is not null)
            {
                var decision = triage.Decision == TriageDecision.Small && !configuration.AutoApproveSmallChanges
                    ? TriageDecision.NeedsApproval
                    : triage.Decision;
                switch (decision)
                {
                    case TriageDecision.Clarify:
                        await ApplyClarifyAsync(projectId, issue, triage, requirementsHash, cancellationToken);
                        return;
                    case TriageDecision.AnalysisOnly:
                        await ApplyAnalysisOnlyAsync(projectId, issue, triage, requirementsHash, cancellationToken);
                        return;
                    case TriageDecision.Split:
                        await ApplySplitAsync(projectId, issue, triage, requirementsHash, cancellationToken);
                        return;
                    case TriageDecision.NeedsApproval:
                        plan = triage.RiskAreas.Count == 0
                            ? triage.Plan
                            : $"{triage.Plan}\n\nRisk areas requiring human approval: {string.Join(", ", triage.RiskAreas)}";
                        plan = ManagerPlanMarker(TriageDecision.NeedsApproval, plan);
                        await jira.AddLabelAsync(issue.Key, configuration.ApprovalLabel, cancellationToken);
                        hasApprovalLabel = true;
                        newlyHeld = true;
                        approvalRequired = true;
                        break;
                    default:
                        plan = triage.Plan;
                        break;
                }
            }

            plans.Save(projectId, issue.Key, plan, requirementsHash, issue.UpdatedAt);
            storedPlan = new ManagerPlan(plan, false, requirementsHash, issue.UpdatedAt);
        }
        else if (storedPlan!.PostedToJira &&
            !(hasApprovedLabel && (hasApprovalLabel || approvalRequired)) &&
            issue.Labels.Contains(configuration.AnalyzedLabel) &&
            issue.Labels.Contains(configuration.PickupLabel))
        {
            return;
        }

        if (!storedPlan!.PostedToJira)
        {
            foreach (var comment in PromptBuilder.BuildPlanComments(TriageResult.StripMarker(storedPlan.Plan)))
            {
                await jira.AddCommentAsync(issue.Key, comment, cancellationToken);
            }
            if (newlyHeld)
            {
                await jira.AddCommentAsync(
                    issue.Key,
                    "This change touches authentication, database or architecture, so a human must approve the plan first. " +
                    $"Add the `{configuration.ApprovedLabel}` label to release it to the worker. " +
                    "Editing the title or description discards this plan and triggers a new triage.",
                    cancellationToken);
            }
            plans.MarkPostedToJira(issue.Key, requirementsHash);
        }

        if ((approvalRequired || hasApprovalLabel) && !hasApprovedLabel)
        {
            DFLogger.LogOutput(DFLogLevel.INFO, "Planner", $"Plan for {issue.Key} is awaiting human approval.");
            return;
        }

        var beforeHandoff = await jira.GetIssueAsync(issue.Key, cancellationToken);
        if (beforeHandoff.Labels.Contains(configuration.WorkerPickupLabel) ||
            beforeHandoff.Labels.Contains(configuration.FailureLabel) ||
            !string.Equals(beforeHandoff.StatusName, issue.StatusName, StringComparison.Ordinal) ||
            !storedPlan.IsCurrent(beforeHandoff.Summary, beforeHandoff.Description, configuration.GitHubRepositoryUrl))
        {
            DFLogger.LogOutput(DFLogLevel.INFO, "Planner", $"Deferring handoff for {issue.Key}: the issue changed or was claimed.");
            return;
        }
        if (configuration.PickupLabel != configuration.AnalyzedLabel)
        {
            await jira.AddLabelAsync(issue.Key, configuration.PickupLabel, cancellationToken);
        }
        await jira.AddLabelAsync(issue.Key, configuration.AnalyzedLabel, cancellationToken);
        if (approvalRequired)
        {
            plans.ReplacePlan(issue.Key, requirementsHash, TriageResult.StripMarker(storedPlan.Plan));
        }
        if (approvalRequired || hasApprovalLabel)
        {
            await jira.RemoveLabelAsync(issue.Key, configuration.ApprovalLabel, cancellationToken);
            await jira.RemoveLabelAsync(issue.Key, configuration.ApprovedLabel, cancellationToken);
        }
        DFLogger.LogOutput(DFLogLevel.INFO, "Planner", $"Plan for {issue.Key} stored in Jira and manager_plans.");
    }

    private async Task ApplyClarifyAsync(
        long projectId, JiraIssue issue, TriageResult triage, string requirementsHash, CancellationToken cancellationToken)
    {
        var text = string.Join("\n", triage.Questions.Select((question, index) => $"{index + 1}. {question}"));
        SaveTriaged(projectId, issue, TriageDecision.Clarify, text, requirementsHash);
        await jira.AddCommentAsync(
            issue.Key,
            "The requirements need clarification before the manager can plan this task:\n\n" + text +
            "\n\nUpdate the title or description with the answers to trigger a new triage. " +
            $"No code was implemented and the task was not handed to the worker.",
            cancellationToken);
        plans.MarkPostedToJira(issue.Key, requirementsHash);
        await jira.AddLabelAsync(issue.Key, configuration.ClarificationLabel, cancellationToken);
        DFLogger.LogOutput(DFLogLevel.INFO, "Planner", $"Requested clarification for {issue.Key}.");
    }

    private async Task ApplyAnalysisOnlyAsync(
        long projectId, JiraIssue issue, TriageResult triage, string requirementsHash, CancellationToken cancellationToken)
    {
        SaveTriaged(projectId, issue, TriageDecision.AnalysisOnly, triage.Analysis, requirementsHash);
        foreach (var comment in PromptBuilder.BuildComments(
            triage.Analysis, part => $"Analysis (generated by the manager, part {part}):"))
        {
            await jira.AddCommentAsync(issue.Key, comment, cancellationToken);
        }
        plans.MarkPostedToJira(issue.Key, requirementsHash);
        await jira.TransitionToAsync(issue.Key, configuration.AnalysisOnlyStatus, cancellationToken);
        DFLogger.LogOutput(DFLogLevel.INFO, "Planner", $"Analysis for {issue.Key} posted; moved to {configuration.AnalysisOnlyStatus}.");
    }

    private async Task ApplySplitAsync(
        long projectId, JiraIssue issue, TriageResult triage, string requirementsHash, CancellationToken cancellationToken)
    {
        var order = triage.ExecutionOrder();
        var keys = new string[triage.Subtasks.Count];
        var titleToIndex = triage.Subtasks
            .Select((subtask, index) => (subtask.Title.Trim().ToLowerInvariant(), index))
            .ToDictionary(entry => entry.Item1, entry => entry.index);
        foreach (var index in order)
        {
            var subtask = triage.Subtasks[index];
            var dependencies = subtask.DependsOn.Select(title => keys[titleToIndex[title.Trim().ToLowerInvariant()]]).ToArray();
            var description = subtask.Description +
                $"\n\nParent task: {issue.Key}" +
                (dependencies.Length == 0 ? string.Empty : $"\nDepends on: {string.Join(", ", dependencies)}");
            var key = await jira.CreateSubtaskAsync(issue.Key, subtask.Title, description, cancellationToken);
            keys[index] = key;
            var created = await jira.GetIssueAsync(key, cancellationToken);
            var subtaskPlan = $"{subtask.Description}\n\nThis is part of {issue.Key}: {issue.Summary}." +
                (dependencies.Length == 0 ? string.Empty : $" Its dependencies are {string.Join(", ", dependencies)}.");
            var storedSubtaskPlan = dependencies.Length == 0
                ? subtaskPlan
                : DependencyHoldPlan(subtaskPlan, dependencies);
            plans.Save(projectId, key, storedSubtaskPlan,
                ManagerPlan.Fingerprint(created.Summary, created.Description, configuration.GitHubRepositoryUrl),
                created.UpdatedAt);
            foreach (var comment in PromptBuilder.BuildPlanComments(subtaskPlan))
            {
                await jira.AddCommentAsync(key, comment, cancellationToken);
            }
            plans.MarkPostedToJira(
                key, ManagerPlan.Fingerprint(created.Summary, created.Description, configuration.GitHubRepositoryUrl));
            foreach (var dependency in dependencies)
            {
                try
                {
                    await jira.LinkIssuesAsync(dependency, key, cancellationToken);
                }
                catch (HttpRequestException exception)
                {
                    DFLogger.LogOutput(DFLogLevel.ERROR, "Planner", $"Could not link {dependency} as blocking {key}: {exception.Message}");
                }
            }
        }

        var state = new SplitState(triage.Plan, order.Select(index => new SplitSubtask(
            keys[index],
            triage.Subtasks[index].Title,
            triage.Subtasks[index].DependsOn
                .Select(title => keys[titleToIndex[title.Trim().ToLowerInvariant()]]).ToArray())).ToArray());
        SaveTriaged(projectId, issue, TriageDecision.Split, JsonSerializer.Serialize(state), requirementsHash);
        await CompleteSplitAsync(
            issue.Key, ManagerPlanMarker(TriageDecision.Split, JsonSerializer.Serialize(state)), requirementsHash, cancellationToken);
        DFLogger.LogOutput(DFLogLevel.INFO, "Planner", $"Split {issue.Key} into {keys.Length} sub-tasks.");
    }

    private async Task CompleteSplitAsync(
        string issueKey, string storedPlan, string requirementsHash, CancellationToken cancellationToken)
    {
        var state = ReadSplitState(storedPlan);
        await jira.AddCommentAsync(issueKey, BuildSplitComment(state), cancellationToken);
        plans.MarkPostedToJira(issueKey, requirementsHash);
        await ReleaseSubtasksAsync(storedPlan, cancellationToken);
    }

    internal Task ReportIgnoredAsync(string issueKey, string reason, CancellationToken cancellationToken)
    {
        DFLogger.LogOutput(DFLogLevel.INFO, "Planner", $"Ignoring {issueKey}: {reason}");
        return IgnoredTaskNotifier.NotifyAsync(
            "AIManager",
            issueKey,
            reason,
            jira.GetCommentsAsync,
            jira.AddCommentAsync,
            message => DFLogger.LogOutput(DFLogLevel.WARNING, "Planner", message),
            cancellationToken);
    }

    private async Task ReleaseSubtasksAsync(string storedPlan, CancellationToken cancellationToken)
    {
        var state = ReadSplitState(storedPlan);
        foreach (var subtask in state.Subtasks)
        {
            var current = await jira.GetIssueAsync(subtask.Key, cancellationToken);
            if (string.Equals(current.StatusName, configuration.DoneStatus, StringComparison.OrdinalIgnoreCase) ||
                current.Labels.Contains(configuration.AnalyzedLabel) ||
                current.Labels.Contains(configuration.FailureLabel) ||
                current.Labels.Contains(configuration.WorkerPickupLabel))
            {
                continue;
            }

            var ready = true;
            foreach (var dependency in subtask.DependsOn)
            {
                var dependencyIssue = await jira.GetIssueAsync(dependency, cancellationToken);
                if (!string.Equals(dependencyIssue.StatusName, configuration.DoneStatus, StringComparison.OrdinalIgnoreCase))
                {
                    ready = false;
                    break;
                }
            }

            if (!ready)
            {
                continue;
            }

            var storedSubtaskPlan = plans.Find(subtask.Key);
            if (storedSubtaskPlan is not null && IsDependencyHold(storedSubtaskPlan.Plan))
            {
                var dependencyHold = ReadDependencyHold(storedSubtaskPlan.Plan);
                plans.ReplacePlan(
                    subtask.Key,
                    storedSubtaskPlan.RequirementsHash!,
                    dependencyHold.Plan);
                if (!storedSubtaskPlan.IsCurrent(
                    current.Summary, current.Description, configuration.GitHubRepositoryUrl))
                {
                    if (!string.Equals(current.StatusName, configuration.JiraStatusName, StringComparison.OrdinalIgnoreCase))
                    {
                        await jira.TransitionToAsync(subtask.Key, configuration.JiraStatusName, cancellationToken);
                    }
                    continue;
                }
            }
            if (!string.Equals(current.StatusName, configuration.JiraStatusName, StringComparison.OrdinalIgnoreCase))
            {
                await jira.TransitionToAsync(subtask.Key, configuration.JiraStatusName, cancellationToken);
            }
            if (configuration.PickupLabel != configuration.AnalyzedLabel)
            {
                await jira.AddLabelAsync(subtask.Key, configuration.PickupLabel, cancellationToken);
            }
            await jira.AddLabelAsync(subtask.Key, configuration.AnalyzedLabel, cancellationToken);
            DFLogger.LogOutput(DFLogLevel.INFO, "Planner", $"Released sub-task {subtask.Key} to the worker.");
        }
    }

    private void SaveTriaged(
        long projectId, JiraIssue issue, TriageDecision decision, string text, string requirementsHash) =>
        plans.Save(projectId, issue.Key, ManagerPlanMarker(decision, text), requirementsHash, issue.UpdatedAt);

    private static string ManagerPlanMarker(TriageDecision decision, string text) =>
        $"{TriageResult.MarkerFor(decision)}\n{text}";

    private const string DependencyHoldMarker = "[triage:dependency_hold]";

    private static string DependencyHoldPlan(string plan, string[] dependencies) =>
        $"{DependencyHoldMarker}\n{JsonSerializer.Serialize(new DependencyHoldState(plan, dependencies))}";

    private static bool IsDependencyHold(string plan) =>
        plan.StartsWith(DependencyHoldMarker, StringComparison.Ordinal);

    private static DependencyHoldState ReadDependencyHold(string plan) =>
        JsonSerializer.Deserialize<DependencyHoldState>(plan[DependencyHoldMarker.Length..].TrimStart('\r', '\n'))
            ?? throw new InvalidDataException("The stored dependency hold is invalid.");

    private static SplitState ReadSplitState(string storedPlan) =>
        JsonSerializer.Deserialize<SplitState>(TriageResult.StripMarker(storedPlan))
            ?? throw new InvalidDataException("The stored split state is invalid.");

    private string BuildSplitComment(SplitState state)
    {
        var listing = string.Join("\n", state.Subtasks.Select((subtask, index) =>
            $"{index + 1}. {subtask.Key} {subtask.Title}" +
            (subtask.DependsOn.Length == 0 ? " (no dependencies)" : $" (after {string.Join(", ", subtask.DependsOn)})")));
        var intro = state.Plan.Length == 0 ? string.Empty : state.Plan + "\n\n";
        return $"{intro}The task was split into sub-tasks. Execution order:\n\n{listing}\n\n" +
            $"Sub-tasks without open dependencies are moved to {configuration.JiraStatusName} for the worker. " +
            $"The others are released when all their dependencies reach {configuration.DoneStatus}.";
    }

    private sealed record DependencyHoldState(string Plan, string[] Dependencies);

    private sealed record SplitSubtask(string Key, string Title, string[] DependsOn);

    private sealed record SplitState(string Plan, SplitSubtask[] Subtasks);
}
