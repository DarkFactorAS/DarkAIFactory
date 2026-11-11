using DFCommonLib.DataAccess;

namespace dbmigration.Repository;

/// <summary>
/// Agent configuration tables (all prefixed new_). Every patch is idempotent
/// (CREATE TABLE IF NOT EXISTS / INSERT IGNORE) so it can recover from a failed history write.
/// </summary>
public static class AgentConfigPatcher
{
    public static bool Run(IDBPatcher patcher)
    {
        const string system = "AgentConfig";
        patcher.Init(system);

        patcher.Patch(system, 1,
            """
            CREATE TABLE IF NOT EXISTS `new_agents` (
                `id` BIGINT NOT NULL AUTO_INCREMENT,
                `name` VARCHAR(255) NOT NULL,
                `role` VARCHAR(255) NOT NULL,
                `instructions` LONGTEXT NOT NULL,
                `language_model_key` VARCHAR(255) NOT NULL,
                `created` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                `updated` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                PRIMARY KEY (`id`),
                UNIQUE KEY `uq_new_agents_name` (`name`)
            )
            """);
        if (!patcher.Successful()) return false;

        patcher.Patch(system, 2,
            """
            CREATE TABLE IF NOT EXISTS `new_language_models` (
                `id` BIGINT NOT NULL AUTO_INCREMENT,
                `model_key` VARCHAR(255) NOT NULL,
                `enabled` BOOLEAN NOT NULL DEFAULT TRUE,
                PRIMARY KEY (`id`),
                UNIQUE KEY `uq_new_language_models_key` (`model_key`)
            )
            """);
        if (!patcher.Successful()) return false;

        patcher.Patch(system, 3,
            """
            CREATE TABLE IF NOT EXISTS `new_agent_capabilities` (
                `id` BIGINT NOT NULL AUTO_INCREMENT,
                `agent_id` BIGINT NOT NULL,
                `capability` VARCHAR(128) NOT NULL,
                `enabled` BOOLEAN NOT NULL DEFAULT FALSE,
                PRIMARY KEY (`id`),
                UNIQUE KEY `uq_new_agent_capabilities_agent_capability` (`agent_id`, `capability`),
                CONSTRAINT `fk_new_agent_capabilities_agent` FOREIGN KEY (`agent_id`)
                    REFERENCES `new_agents` (`id`) ON DELETE CASCADE
            )
            """);
        if (!patcher.Successful()) return false;

        patcher.Patch(system, 4,
            """
            CREATE TABLE IF NOT EXISTS `new_agent_metrics` (
                `id` BIGINT NOT NULL AUTO_INCREMENT,
                `agent_id` BIGINT NOT NULL,
                `tokens` BIGINT NOT NULL DEFAULT 0,
                `tool_calls` BIGINT NOT NULL DEFAULT 0,
                `duration_ms` BIGINT NOT NULL DEFAULT 0,
                `success_count` BIGINT NOT NULL DEFAULT 0,
                `failure_count` BIGINT NOT NULL DEFAULT 0,
                `created` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                PRIMARY KEY (`id`),
                UNIQUE KEY `uq_new_agent_metrics_agent` (`agent_id`),
                CONSTRAINT `fk_new_agent_metrics_agent` FOREIGN KEY (`agent_id`)
                    REFERENCES `new_agents` (`id`) ON DELETE CASCADE
            )
            """);
        if (!patcher.Successful()) return false;

        patcher.Patch(system, 5,
            """
            INSERT IGNORE INTO `new_language_models` (`model_key`, `enabled`)
            VALUES ('gpt-5-mini', TRUE)
            """);
        if (!patcher.Successful()) return false;

        patcher.Patch(system, 6,
            """
            INSERT IGNORE INTO `new_agents` (`name`, `role`, `instructions`, `language_model_key`)
            VALUES ('default', 'general', 'You are a helpful general-purpose coding agent.', 'gpt-5-mini')
            """);
        return patcher.Successful();
    }
}
