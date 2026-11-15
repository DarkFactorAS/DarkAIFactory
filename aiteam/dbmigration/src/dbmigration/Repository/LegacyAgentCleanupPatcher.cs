using DFCommonLib.DataAccess;

namespace dbmigration.Repository;

/// <summary>
/// DFAI-215: removes the database objects that only the retired agents (aiworker, aiclaudeworker,
/// aireviewworker, aimanager/AITechLead and aijiramanager) used. Forward-only: earlier patches are not edited
/// because fresh databases replay them in order. Every patch is idempotent so it can recover from a failed
/// history write. Kept on purpose: manager_projects (aiagent and the dashboard use it, agent_projects and
/// agent_plans have foreign keys to it), agent_tasks, all agent_* tables, logtable, the aiagent workflow_labels
/// rows and the migration history rows.
/// </summary>
public static class LegacyAgentCleanupPatcher
{
    public static bool Run(IDBPatcher patcher)
    {
        const string system = "LegacyAgentCleanup";
        patcher.Init(system);

        string[] patches =
        [
            // The review attempts reference pull_requests, so they go first.
            "DROP TABLE IF EXISTS `review_comment_attempts`",
            "DROP TABLE IF EXISTS `pull_requests`",
            "DROP TABLE IF EXISTS `manager_plans`",
            "DELETE FROM `workflow_labels` WHERE `agent_name` IN ('aimanager', 'aiworker', 'aiclaudeworker')",
            "DELETE FROM `settings` WHERE `client` IN ('aimanager', 'aiworker', 'aiclaudeworker', 'aibuildpage')",
            // The dashboard no longer reads these rows (see IntegrationSettingsRepository in shared). Earlier
            // constraints also allowed GitHub.AgentModel under shared; only the retired agents read it, and the
            // new constraint below rejects it, so that row is removed here too.
            """
            DELETE FROM `settings`
            WHERE (`client` = 'aidashboard' AND `name` IN
                    ('Jira.JiraPickupLabel', 'Jira.JiraAnalyzedLabel', 'GitHub.GitHubRepositoryToken', 'GitHub.AgentModel'))
                OR (`client` = 'shared' AND `name` = 'GitHub.AgentModel')
            """,
            // Existing rows are already valid (the deletes above ran first), so the new constraint can be added.
            """
            ALTER TABLE `settings`
                DROP CONSTRAINT `ck_settings_scope`,
                ADD CONSTRAINT `ck_settings_scope` CHECK (
                    (`client` = 'shared' AND `name` NOT IN
                        ('GitHub.GitHubRepositoryToken', 'Claude.AnthropicApiKey', 'Claude.AgentModel',
                         'GitHub.AgentModel', 'Jira.JiraPickupLabel', 'Jira.JiraAnalyzedLabel',
                         'Dashboard.AiLabels', 'Dashboard.ActiveStatuses'))
                    OR
                    (`client` = 'aidashboard' AND `name` IN ('Dashboard.AiLabels', 'Dashboard.ActiveStatuses'))
                    OR
                    (`client` = 'aiagent' AND `name` IN
                        ('GitHub.GitHubRepositoryToken', 'Claude.AnthropicApiKey'))
                )
            """,
            // Drops only the exact legacy default label names and keeps the other labels in order. An empty
            // result falls back to the aiagent labels in workflow_labels, then to the literal default.
            """
            UPDATE `settings`
            SET `value` = COALESCE(
                NULLIF(TRIM(BOTH ',' FROM REGEXP_REPLACE(CONCAT(',', `value`, ','),
                    ',[[:space:]]*(ai-manager-planned|ai-analyzed|ai-agent-picked)[[:space:]]*(?=,)', '')), ''),
                (SELECT GROUP_CONCAT(`label_name`
                        ORDER BY FIELD(`code_key`, 'PickupLabel', 'AnalyzedLabel', 'PlannedLabel') SEPARATOR ',')
                    FROM `workflow_labels`
                    WHERE `agent_name` = 'aiagent' AND `code_key` IN ('PickupLabel', 'AnalyzedLabel', 'PlannedLabel')),
                'aiagent-picked,aiagent-analyzed,aiagent-planned')
            WHERE `client` = 'aidashboard' AND `name` = 'Dashboard.AiLabels'
            """
        ];

        for (var i = 0; i < patches.Length; i++)
        {
            patcher.Patch(system, i + 1, patches[i]);
            if (!patcher.Successful())
            {
                return false;
            }
        }

        return true;
    }
}
