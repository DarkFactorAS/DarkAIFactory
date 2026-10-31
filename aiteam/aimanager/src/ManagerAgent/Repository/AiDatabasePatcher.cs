using DFCommonLib.DataAccess;

namespace ManagerAgent.Repository;

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
        return _dbPatcher.Successful();
    }
}
