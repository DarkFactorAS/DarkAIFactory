using DFCommonLib.DataAccess;

namespace dbmigration.Repository;

/// <summary>
/// Agent configuration and metrics schema. Only adds new tables; existing tables are untouched.
/// All patches are idempotent so they can recover from a failed history write.
/// </summary>
public static class AgentConfigPatcher
{
    public static bool Run(IDBPatcher patcher)
    {
        const string system = "AgentConfig";
        patcher.Init(system);

        patcher.Patch(system, 1,
            """
            CREATE TABLE IF NOT EXISTS `agents` (
                `id` BIGINT NOT NULL AUTO_INCREMENT,
                `name` VARCHAR(128) NOT NULL,
                `role` VARCHAR(255) NOT NULL,
                `behavior_prompt` LONGTEXT NOT NULL,
                `language_model` VARCHAR(128) NOT NULL,
                `enabled` BOOLEAN NOT NULL DEFAULT TRUE,
                `created` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                `updated` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                PRIMARY KEY (`id`),
                UNIQUE KEY `uq_agents_name` (`name`)
            )
            """);
        if (!patcher.Successful())
        {
            return false;
        }

        patcher.Patch(system, 2,
            """
            CREATE TABLE IF NOT EXISTS `agent_capabilities` (
                `agent_id` BIGINT NOT NULL,
                `capability` VARCHAR(64) NOT NULL,
                `language_model` VARCHAR(128) NOT NULL,
                `enabled` BOOLEAN NOT NULL DEFAULT TRUE,
                PRIMARY KEY (`agent_id`, `capability`, `language_model`),
                CONSTRAINT `fk_agent_capabilities_agent` FOREIGN KEY (`agent_id`)
                    REFERENCES `agents` (`id`) ON DELETE CASCADE
            )
            """);
        if (!patcher.Successful())
        {
            return false;
        }

        patcher.Patch(system, 3,
            """
            CREATE TABLE IF NOT EXISTS `agent_task_metrics` (
                `id` BIGINT NOT NULL AUTO_INCREMENT,
                `agent_id` BIGINT NOT NULL,
                `agent_task_id` BIGINT NULL,
                `jira_key` VARCHAR(255) NULL,
                `language_model` VARCHAR(128) NOT NULL,
                `input_tokens` BIGINT NOT NULL DEFAULT 0,
                `output_tokens` BIGINT NOT NULL DEFAULT 0,
                `tool_calls` INT NOT NULL DEFAULT 0,
                `duration_ms` BIGINT NOT NULL DEFAULT 0,
                `succeeded` BOOLEAN NOT NULL,
                `started` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                `finished` DATETIME NULL,
                PRIMARY KEY (`id`),
                KEY `ix_agent_task_metrics_agent` (`agent_id`, `started`),
                KEY `ix_agent_task_metrics_task` (`agent_task_id`),
                CONSTRAINT `fk_agent_task_metrics_agent` FOREIGN KEY (`agent_id`)
                    REFERENCES `agents` (`id`) ON DELETE CASCADE
            )
            """);
        if (!patcher.Successful())
        {
            return false;
        }

        patcher.Patch(system, 4,
            """
            CREATE TABLE IF NOT EXISTS `agent_metrics_totals` (
                `agent_id` BIGINT NOT NULL,
                `input_tokens` BIGINT NOT NULL DEFAULT 0,
                `output_tokens` BIGINT NOT NULL DEFAULT 0,
                `tool_calls` BIGINT NOT NULL DEFAULT 0,
                `total_duration_ms` BIGINT NOT NULL DEFAULT 0,
                `tasks_succeeded` BIGINT NOT NULL DEFAULT 0,
                `tasks_failed` BIGINT NOT NULL DEFAULT 0,
                `updated` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                PRIMARY KEY (`agent_id`),
                CONSTRAINT `fk_agent_metrics_totals_agent` FOREIGN KEY (`agent_id`)
                    REFERENCES `agents` (`id`) ON DELETE CASCADE
            )
            """);
        if (!patcher.Successful())
        {
            return false;
        }

        patcher.Patch(system, 5,
            """
            INSERT INTO `agents` (`name`, `role`, `behavior_prompt`, `language_model`, `enabled`)
            SELECT 'sample-agent', 'Software developer',
                'You are a careful software developer. Implement the requested task, keep changes small and run the tests before finishing.',
                'claude-sonnet-5-5', TRUE
            WHERE NOT EXISTS (SELECT 1 FROM `agents` WHERE `name` = 'sample-agent')
            """);
        if (!patcher.Successful())
        {
            return false;
        }

        patcher.Patch(system, 6,
            """
            INSERT IGNORE INTO `agent_capabilities` (`agent_id`, `capability`, `language_model`, `enabled`)
            SELECT a.`id`, c.`capability`, 'claude-sonnet-5-5', c.`enabled`
            FROM `agents` a
            JOIN (SELECT 'read_files' AS `capability`, TRUE AS `enabled`
                  UNION ALL SELECT 'edit_files', TRUE
                  UNION ALL SELECT 'run_shell', TRUE
                  UNION ALL SELECT 'web_search', FALSE) c
            WHERE a.`name` = 'sample-agent'
            """);
        if (!patcher.Successful())
        {
            return false;
        }

        patcher.Patch(system, 7,
            """
            INSERT IGNORE INTO `agent_metrics_totals` (`agent_id`)
            SELECT `id` FROM `agents` WHERE `name` = 'sample-agent'
            """);
        return patcher.Successful();
    }
}
