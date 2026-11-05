using DFCommonLib.DataAccess;

namespace dbmigration.Repository;

public static class AgentTaskPatcher
{
    public static bool Run(IDBPatcher patcher)
    {
        const string system = "AgentTasks";
        patcher.Init(system);
        patcher.Patch(system, 1,
            """
            CREATE TABLE IF NOT EXISTS `agent_tasks` (
                `id` BIGINT NOT NULL AUTO_INCREMENT,
                `project_key` VARCHAR(64) NOT NULL,
                `title` VARCHAR(255) NOT NULL,
                `description` TEXT NOT NULL,
                `status` VARCHAR(16) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT 'New',
                `jira_key` VARCHAR(255) NULL,
                `created` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                `updated` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                PRIMARY KEY (`id`),
                KEY `ix_agent_tasks_status` (`status`),
                CONSTRAINT `ck_agent_tasks_status` CHECK (`status` IN ('New', 'Created', 'Failed'))
            )
            """);
        return patcher.Successful();
    }
}
