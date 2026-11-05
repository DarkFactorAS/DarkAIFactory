using DFCommonLib.DataAccess;

namespace dbmigration.Repository;

public static class WorkflowLabelPatcher
{
    public static bool Run(IDBPatcher patcher)
    {
        const string system = "WorkflowLabels";
        patcher.Init(system);
        patcher.Patch(system, 1,
            """
            CREATE TABLE IF NOT EXISTS `workflow_labels` (
                `agent_name` VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
                `code_key` VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
                `label_name` VARCHAR(255) NOT NULL,
                `description` TEXT NOT NULL,
                PRIMARY KEY (`agent_name`, `code_key`)
            )
            """);
        if (!patcher.Successful())
        {
            return false;
        }

        // The labels formerly hardcoded in the agents; settings values never override these.
        patcher.Patch(system, 2,
            """
            INSERT INTO `workflow_labels` (`agent_name`, `code_key`, `label_name`, `description`) VALUES
                ('aimanager', 'PickupLabel', 'ai-manager-planned', 'Added by the manager agent to a Jira issue once it has been planned, so the manager does not pick it up again.'),
                ('aimanager', 'FailureLabel', 'ai-agent-failed', 'Added by the manager agent when processing a Jira issue fails; issues with this label are excluded from polling.'),
                ('aimanager', 'AnalyzedLabel', 'ai-analyzed', 'Added by the manager agent after the plan is posted to Jira; hands the issue over to the worker agent. Must equal the worker AnalyzedLabel.'),
                ('aimanager', 'ClarificationLabel', 'ai-needs-clarification', 'Added by the manager agent when the requirements are unclear and questions have been posted as a Jira comment.'),
                ('aimanager', 'ApprovalLabel', 'ai-awaiting-approval', 'Added by the manager agent when the plan needs human approval (authentication, database or architecture changes).'),
                ('aimanager', 'ApprovedLabel', 'ai-approved', 'Added by a human to release a plan held for approval; the manager then hands the issue off and removes the approval labels.'),
                ('aiworker', 'PickupLabel', 'ai-agent-picked', 'Added by the worker agent when it picks up a Jira issue, to avoid repeated pickup. Also excluded from the manager query.'),
                ('aiworker', 'FailureLabel', 'ai-agent-failed', 'Added by the worker agent when processing a Jira issue fails; issues with this label are excluded from polling.'),
                ('aiworker', 'AnalyzedLabel', 'ai-analyzed', 'Manager handoff label required by the worker default query; the worker only processes issues that have it.')
            ON DUPLICATE KEY UPDATE `label_name` = `workflow_labels`.`label_name`
            """);
        if (!patcher.Successful())
        {
            return false;
        }

        patcher.Patch(system, 3,
            """
            DELETE FROM `settings` WHERE `name` IN (
                'Jira.JiraPickupLabel', 'Jira.JiraAnalyzedLabel', 'Jira.JiraFailureLabel',
                'Jira.JiraClarificationLabel', 'Jira.JiraApprovalLabel', 'Jira.JiraApprovedLabel')
            """);
        return patcher.Successful();
    }
}
