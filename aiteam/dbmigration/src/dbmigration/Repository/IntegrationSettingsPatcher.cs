using DFCommonLib.DataAccess;

namespace dbmigration.Repository;

public static class IntegrationSettingsPatcher
{
    public static bool Run(IDBPatcher patcher)
    {
        const string system = "IntegrationSettings";
        patcher.Init(system);
        patcher.Patch(system, 1,
            """
            CREATE TABLE IF NOT EXISTS `settings` (
                `client` VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
                `name` VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
                `value` TEXT NOT NULL,
                PRIMARY KEY (`client`, `name`),
                CONSTRAINT `ck_settings_scope` CHECK (
                    (`client` = 'shared' AND `name` NOT IN
                        ('Jira.JiraPickupLabel', 'Jira.JiraAnalyzedLabel', 'GitHub.GitHubRepositoryToken'))
                    OR
                    (`client` IN ('aimanager', 'aiworker', 'aibuildpage') AND `name` IN
                        ('Jira.JiraPickupLabel', 'Jira.JiraAnalyzedLabel', 'GitHub.GitHubRepositoryToken'))
                )
            )
            """);
        if (!patcher.Successful())
        {
            return false;
        }

        patcher.Patch(system, 2,
            """
            INSERT INTO `settings` (`client`, `name`, `value`) VALUES
                ('shared', 'Jira.JiraBaseUrl', ''),
                ('shared', 'Jira.JiraEmail', ''),
                ('shared', 'Jira.JiraApiToken', ''),
                ('shared', 'Jira.JiraProjectKey', 'DFAI'),
                ('shared', 'Jira.JiraStatusName', 'Selected for Development'),
                ('shared', 'Jira.JiraJql', ''),
                ('shared', 'Jira.JiraFailureLabel', 'ai-agent-failed'),
                ('shared', 'Jira.JiraInProgressStatus', 'In Progress'),
                ('shared', 'Jira.JiraInReviewStatus', 'In Review'),
                ('shared', 'GitHub.AgentGitHubToken', ''),
                ('shared', 'GitHub.TargetRepositoryUrl', ''),
                ('aimanager', 'Jira.JiraPickupLabel', 'ai-manager-planned'),
                ('aimanager', 'Jira.JiraAnalyzedLabel', 'ai-analyzed'),
                ('aimanager', 'GitHub.GitHubRepositoryToken', ''),
                ('aiworker', 'Jira.JiraPickupLabel', 'ai-agent-picked'),
                ('aiworker', 'Jira.JiraAnalyzedLabel', 'ai-analyzed'),
                ('aiworker', 'GitHub.GitHubRepositoryToken', ''),
                ('aibuildpage', 'Jira.JiraPickupLabel', 'ai-build-picked'),
                ('aibuildpage', 'Jira.JiraAnalyzedLabel', 'ai-analyzed'),
                ('aibuildpage', 'GitHub.GitHubRepositoryToken', '')
            ON DUPLICATE KEY UPDATE `value` = `settings`.`value`
            """);
        if (!patcher.Successful())
        {
            return false;
        }

        patcher.Patch(system, 3,
            """
            INSERT INTO `settings` (`client`, `name`, `value`) VALUES
                ('shared', 'Jira.JiraAssigneeAccountId', '')
            ON DUPLICATE KEY UPDATE `value` = `settings`.`value`
            """);
        return patcher.Successful();
    }
}
