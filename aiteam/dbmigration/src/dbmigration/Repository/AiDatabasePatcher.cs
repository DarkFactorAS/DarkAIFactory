using DFCommonLib.DataAccess;

namespace dbmigration.Repository;

public class AiDatabasePatcher : StartupDatabasePatcher
{
    public AiDatabasePatcher(IDBPatcher dbPatcher) : base(dbPatcher)
    {
    }

    public override bool RunPatcher()
    {
        if (!base.RunPatcher())
        {
            return false;
        }

        _dbPatcher.Patch("StartupDatabasePatcher", 2,
            """
            CREATE TABLE `manager_projects` (
                `id` BIGINT NOT NULL AUTO_INCREMENT,
                `jira_project_key` VARCHAR(255) NOT NULL,
                `target_repository_url` VARCHAR(2048) NOT NULL,
                PRIMARY KEY (`id`),
                UNIQUE KEY `uq_manager_projects_jira_project_key` (`jira_project_key`)
            )
            """);
        if (!_dbPatcher.Successful())
        {
            return false;
        }

        _dbPatcher.Patch("StartupDatabasePatcher", 3,
            """
            CREATE TABLE `manager_plans` (
                `id` BIGINT NOT NULL AUTO_INCREMENT,
                `manager_project_id` BIGINT NOT NULL,
                `jira_key` VARCHAR(255) NOT NULL,
                `plan` LONGTEXT NOT NULL,
                `posted_to_jira` BOOLEAN NOT NULL DEFAULT FALSE,
                `created` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                PRIMARY KEY (`id`),
                UNIQUE KEY `uq_manager_plans_jira_key` (`jira_key`),
                CONSTRAINT `fk_manager_plans_project` FOREIGN KEY (`manager_project_id`)
                    REFERENCES `manager_projects` (`id`)
            )
            """);
        if (!_dbPatcher.Successful())
        {
            return false;
        }

        _dbPatcher.Patch("StartupDatabasePatcher", 4,
            """
            ALTER TABLE `manager_plans`
                ADD COLUMN IF NOT EXISTS `requirements_hash` CHAR(64) NULL,
                ADD COLUMN IF NOT EXISTS `source_updated` VARCHAR(64) NULL
            """);
        if (!_dbPatcher.Successful())
        {
            return false;
        }

        _dbPatcher.Patch("StartupDatabasePatcher", 5,
            """
            CREATE TABLE IF NOT EXISTS `pull_requests` (
                `id` BIGINT NOT NULL AUTO_INCREMENT,
                `jira_key` VARCHAR(255) NOT NULL,
                `repository_url` VARCHAR(2048) NOT NULL,
                `pr_number` BIGINT NOT NULL,
                `pr_url` VARCHAR(700) NOT NULL,
                `created` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                PRIMARY KEY (`id`),
                UNIQUE KEY `uq_pull_requests_pr_url` (`pr_url`),
                KEY `ix_pull_requests_jira_key` (`jira_key`)
            )
            """);
        return _dbPatcher.Successful()
            && IntegrationSettingsPatcher.Run(_dbPatcher)
            && WorkflowLabelPatcher.Run(_dbPatcher)
            && AgentTaskPatcher.Run(_dbPatcher);
    }
}
