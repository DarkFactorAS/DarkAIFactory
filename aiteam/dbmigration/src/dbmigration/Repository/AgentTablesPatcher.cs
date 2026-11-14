using DFCommonLib.DataAccess;

namespace dbmigration.Repository;

/// <summary>
/// Tables and seed data for the configurable aiAgent (Stage 1 and 2). Only adds new agent_* tables, one
/// settings constraint clause and seed rows; the old agents' tables are untouched.
/// Every patch is idempotent so it can recover from a failed history write.
/// </summary>
public static class AgentTablesPatcher
{
    public static bool Run(IDBPatcher patcher)
    {
        const string system = "AgentTables";
        patcher.Init(system);

        string[] patches =
        [
            """
            CREATE TABLE IF NOT EXISTS `agent_definitions` (
                `id` BIGINT NOT NULL AUTO_INCREMENT,
                `name` VARCHAR(255) NOT NULL,
                `role` TEXT NOT NULL,
                `behavior` TEXT NOT NULL,
                `llm_provider` ENUM('Claude', 'Copilot') NOT NULL DEFAULT 'Claude',
                `llm_model` VARCHAR(255) NULL,
                `enabled` BOOLEAN NOT NULL DEFAULT FALSE,
                `created_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                `updated_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                `deleted_at` DATETIME NULL,
                PRIMARY KEY (`id`)
            )
            """,
            """
            CREATE TABLE IF NOT EXISTS `agent_capabilities` (
                `agent_id` BIGINT NOT NULL,
                `capability` ENUM('Jira', 'Planning', 'Builder', 'Review') NOT NULL,
                `enabled` BOOLEAN NOT NULL DEFAULT FALSE,
                PRIMARY KEY (`agent_id`, `capability`),
                CONSTRAINT `fk_agent_capabilities_agent` FOREIGN KEY (`agent_id`)
                    REFERENCES `agent_definitions` (`id`) ON DELETE CASCADE
            )
            """,
            """
            CREATE TABLE IF NOT EXISTS `agent_projects` (
                `agent_id` BIGINT NOT NULL,
                `project_key` VARCHAR(255) NOT NULL,
                PRIMARY KEY (`agent_id`, `project_key`),
                CONSTRAINT `fk_agent_projects_agent` FOREIGN KEY (`agent_id`)
                    REFERENCES `agent_definitions` (`id`) ON DELETE CASCADE,
                CONSTRAINT `fk_agent_projects_manager_project` FOREIGN KEY (`project_key`)
                    REFERENCES `manager_projects` (`jira_project_key`)
            )
            """,
            // Same columns as manager_plans (including requirements_hash and source_updated) plus agent_id.
            """
            CREATE TABLE IF NOT EXISTS `agent_plans` (
                `id` BIGINT NOT NULL AUTO_INCREMENT,
                `agent_id` BIGINT NOT NULL,
                `manager_project_id` BIGINT NOT NULL,
                `jira_key` VARCHAR(255) NOT NULL,
                `plan` LONGTEXT NOT NULL,
                `posted_to_jira` BOOLEAN NOT NULL DEFAULT FALSE,
                `created` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                `requirements_hash` CHAR(64) NULL,
                `source_updated` VARCHAR(64) NULL,
                PRIMARY KEY (`id`),
                UNIQUE KEY `uq_agent_plans_agent_jira_key` (`agent_id`, `jira_key`),
                CONSTRAINT `fk_agent_plans_agent` FOREIGN KEY (`agent_id`)
                    REFERENCES `agent_definitions` (`id`),
                CONSTRAINT `fk_agent_plans_project` FOREIGN KEY (`manager_project_id`)
                    REFERENCES `manager_projects` (`id`)
            )
            """,
            """
            CREATE TABLE IF NOT EXISTS `agent_pull_requests` (
                `id` BIGINT NOT NULL AUTO_INCREMENT,
                `agent_id` BIGINT NOT NULL,
                `jira_key` VARCHAR(255) NOT NULL,
                `repository_url` VARCHAR(2048) NOT NULL,
                `pr_number` BIGINT NOT NULL,
                `pr_url` VARCHAR(700) NOT NULL,
                `created` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                PRIMARY KEY (`id`),
                UNIQUE KEY `uq_agent_pull_requests_pr_url` (`pr_url`),
                KEY `ix_agent_pull_requests_jira_key` (`jira_key`),
                CONSTRAINT `fk_agent_pull_requests_agent` FOREIGN KEY (`agent_id`)
                    REFERENCES `agent_definitions` (`id`)
            )
            """,
            """
            CREATE TABLE IF NOT EXISTS `agent_review_attempts` (
                `id` BIGINT NOT NULL AUTO_INCREMENT,
                `agent_id` BIGINT NOT NULL,
                `pull_request_id` BIGINT NOT NULL,
                `github_comment_id` BIGINT NOT NULL,
                `attempts` INT NOT NULL DEFAULT 0,
                `status` VARCHAR(32) NOT NULL DEFAULT 'Pending',
                `created` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                `updated` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                `last_error` TEXT NULL,
                PRIMARY KEY (`id`),
                UNIQUE KEY `uq_agent_review_attempts_pr_comment` (`pull_request_id`, `github_comment_id`),
                CONSTRAINT `ck_agent_review_attempts_status` CHECK (
                    `status` IN ('Pending', 'InProgress', 'Resolved', 'Failed')),
                CONSTRAINT `fk_agent_review_attempts_agent` FOREIGN KEY (`agent_id`)
                    REFERENCES `agent_definitions` (`id`),
                CONSTRAINT `fk_agent_review_attempts_pull_request` FOREIGN KEY (`pull_request_id`)
                    REFERENCES `agent_pull_requests` (`id`) ON DELETE CASCADE
            )
            """,
            // Default agent: provider Claude, disabled. Only inserted when no agent with that name exists.
            """
            INSERT INTO `agent_definitions` (`name`, `role`, `behavior`, `llm_provider`, `enabled`)
            SELECT 'aiAgent', 'Software development agent',
                'Plan, implement and review Jira issues in the configured projects.', 'Claude', FALSE
            FROM DUAL
            WHERE NOT EXISTS (SELECT 1 FROM `agent_definitions` WHERE `name` = 'aiAgent')
            """,
            // All capabilities off. Existing rows are never overwritten.
            """
            INSERT IGNORE INTO `agent_capabilities` (`agent_id`, `capability`, `enabled`)
            SELECT `agent`.`id`, `capability`.`name`, FALSE
            FROM (SELECT MIN(`id`) AS `id` FROM `agent_definitions` WHERE `name` = 'aiAgent') AS `agent`
            CROSS JOIN (
                SELECT 'Jira' AS `name` UNION ALL SELECT 'Planning'
                UNION ALL SELECT 'Builder' UNION ALL SELECT 'Review'
            ) AS `capability`
            WHERE `agent`.`id` IS NOT NULL
            """,
            """
            ALTER TABLE `settings`
                DROP CONSTRAINT `ck_settings_scope`,
                ADD CONSTRAINT `ck_settings_scope` CHECK (
                    (`client` = 'shared' AND `name` NOT IN
                        ('Jira.JiraPickupLabel', 'Jira.JiraAnalyzedLabel', 'GitHub.GitHubRepositoryToken',
                         'Claude.AnthropicApiKey', 'Claude.AgentModel'))
                    OR
                    (`client` IN ('aimanager', 'aiworker', 'aidashboard') AND `name` IN
                        ('Jira.JiraPickupLabel', 'Jira.JiraAnalyzedLabel', 'GitHub.GitHubRepositoryToken',
                         'GitHub.AgentModel'))
                    OR
                    (`client` = 'aiclaudeworker' AND `name` IN
                        ('GitHub.GitHubRepositoryToken', 'Claude.AnthropicApiKey', 'Claude.AgentModel'))
                    OR
                    (`client` = 'aiagent' AND `name` IN
                        ('GitHub.GitHubRepositoryToken', 'Claude.AnthropicApiKey'))
                )
            """,
            """
            INSERT INTO `settings` (`client`, `name`, `value`) VALUES
                ('aiagent', 'GitHub.GitHubRepositoryToken', ''),
                ('aiagent', 'Claude.AnthropicApiKey', '')
            ON DUPLICATE KEY UPDATE `value` = `settings`.`value`
            """,
            // Stage 2: live status written by aiAgent. No ON DELETE CASCADE because agents are soft deleted.
            """
            CREATE TABLE IF NOT EXISTS `agent_status` (
                `agent_id` BIGINT NOT NULL,
                `state` ENUM('Idle', 'Running', 'Error', 'Disabled') NOT NULL DEFAULT 'Idle',
                `current_issue_key` VARCHAR(255) NULL,
                `current_capability` ENUM('Jira', 'Planning', 'Builder', 'Review') NULL,
                `heartbeat_at` DATETIME NULL,
                PRIMARY KEY (`agent_id`),
                CONSTRAINT `fk_agent_status_agent` FOREIGN KEY (`agent_id`)
                    REFERENCES `agent_definitions` (`id`)
            )
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
