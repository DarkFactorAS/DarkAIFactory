using DFCommonLib.DataAccess;
using AiTeam.Configuration;

namespace aibuildpage.Repository;

public class AiDatabasePatcher : StartupDatabasePatcher
{
    public AiDatabasePatcher(IDBPatcher dbPatcher) : base(dbPatcher)
    {
    }

    public override bool RunPatcher()
    {
        return base.RunPatcher() && IntegrationSettingsPatcher.Run(_dbPatcher);
    }
}
