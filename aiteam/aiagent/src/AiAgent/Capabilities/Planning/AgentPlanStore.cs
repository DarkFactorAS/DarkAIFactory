using AiAgent.Core;
using AiAgent.Repositories;

namespace AiAgent.Capabilities.Planning;

/// <summary>agent_plans for one agent, exposed with the interface the copied planner expects.</summary>
internal sealed class AgentPlanStore(IAgentPlanRepository repository, long agentId)
{
    public ManagerPlan? Find(string jiraKey) =>
        repository.Get(agentId, jiraKey) is { } stored
            ? new ManagerPlan(stored.Plan, stored.PostedToJira, stored.RequirementsHash, stored.SourceUpdated,
                stored.ManagerProjectId)
            : null;

    public void RebindProject(long projectId, string jiraKey) =>
        repository.RebindProject(agentId, jiraKey, projectId);

    /// <summary>Stores a new plan with its requirements fingerprint; it is not posted to Jira yet.</summary>
    public void Save(long projectId, string jiraKey, string plan, string requirementsHash, string sourceUpdated) =>
        repository.Save(new AgentPlan(agentId, projectId, jiraKey, plan, false, requirementsHash, sourceUpdated));

    public void MarkPostedToJira(string jiraKey, string requirementsHash) =>
        repository.MarkPostedToJira(agentId, jiraKey, requirementsHash);

    public void ReplacePlan(string jiraKey, string requirementsHash, string plan) =>
        repository.ReplacePlan(agentId, jiraKey, requirementsHash, plan);
}
