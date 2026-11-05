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
                        ('Jira.JiraPickupLabel', 'Jira.JiraAnalyzedLabel', 'GitHub.GitHubRepositoryToken',
                         'GitHub.AgentModel'))
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
                ('shared', 'Jira.JiraInProgressStatus', 'In Progress'),
                ('shared', 'Jira.JiraInReviewStatus', 'In Review'),
                ('shared', 'GitHub.AgentGitHubToken', ''),
                ('shared', 'GitHub.TargetRepositoryUrl', ''),
                ('aimanager', 'GitHub.GitHubRepositoryToken', ''),
                ('aiworker', 'GitHub.GitHubRepositoryToken', ''),
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
        if (!patcher.Successful())
        {
            return false;
        }

        patcher.Patch(system, 4,
            """
            ALTER TABLE `settings`
                DROP CONSTRAINT `ck_settings_scope`,
                ADD CONSTRAINT `ck_settings_scope` CHECK (
                    (`client` = 'shared' AND `name` NOT IN
                        ('Jira.JiraPickupLabel', 'Jira.JiraAnalyzedLabel', 'GitHub.GitHubRepositoryToken'))
                    OR
                    (`client` IN ('aimanager', 'aiworker', 'aibuildpage') AND `name` IN
                        ('Jira.JiraPickupLabel', 'Jira.JiraAnalyzedLabel', 'GitHub.GitHubRepositoryToken',
                         'GitHub.AgentModel'))
                )
            """);
        if (!patcher.Successful())
        {
            return false;
        }

        patcher.Patch(system, 5,
            """
            INSERT INTO `settings` (`client`, `name`, `value`) VALUES
                ('aiworker', 'GitHub.AgentModel', 'gpt-5-mini')
            ON DUPLICATE KEY UPDATE `value` = `settings`.`value`
            """);
        if (!patcher.Successful())
        {
            return false;
        }

        // Rename the aibuildpage client to aidashboard: widen the constraint, move the rows, then narrow it.
        patcher.Patch(system, 6,
            """
            ALTER TABLE `settings`
                DROP CONSTRAINT `ck_settings_scope`,
                ADD CONSTRAINT `ck_settings_scope` CHECK (
                    (`client` = 'shared' AND `name` NOT IN
                        ('Jira.JiraPickupLabel', 'Jira.JiraAnalyzedLabel', 'GitHub.GitHubRepositoryToken'))
                    OR
                    (`client` IN ('aimanager', 'aiworker', 'aibuildpage', 'aidashboard') AND `name` IN
                        ('Jira.JiraPickupLabel', 'Jira.JiraAnalyzedLabel', 'GitHub.GitHubRepositoryToken',
                         'GitHub.AgentModel'))
                )
            """);
        if (!patcher.Successful())
        {
            return false;
        }

        patcher.Patch(system, 7,
            """
            UPDATE IGNORE `settings` SET `client` = 'aidashboard' WHERE `client` = 'aibuildpage'
            """);
        if (!patcher.Successful())
        {
            return false;
        }

        patcher.Patch(system, 8,
            """
            DELETE FROM `settings` WHERE `client` = 'aibuildpage'
            """);
        if (!patcher.Successful())
        {
            return false;
        }

        patcher.Patch(system, 9,
            """
            ALTER TABLE `settings`
                DROP CONSTRAINT `ck_settings_scope`,
                ADD CONSTRAINT `ck_settings_scope` CHECK (
                    (`client` = 'shared' AND `name` NOT IN
                        ('Jira.JiraPickupLabel', 'Jira.JiraAnalyzedLabel', 'GitHub.GitHubRepositoryToken'))
                    OR
                    (`client` IN ('aimanager', 'aiworker', 'aidashboard') AND `name` IN
                        ('Jira.JiraPickupLabel', 'Jira.JiraAnalyzedLabel', 'GitHub.GitHubRepositoryToken',
                         'GitHub.AgentModel'))
                )
            """);
        return patcher.Successful();
    }
}
