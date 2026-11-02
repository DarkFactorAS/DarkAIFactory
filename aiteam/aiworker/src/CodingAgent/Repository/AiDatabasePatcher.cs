using AiTeam.Configuration;
using DFCommonLib.DataAccess;

namespace CodingAgent.Repository;

public sealed class AiDatabasePatcher(IDBPatcher dbPatcher) : StartupDatabasePatcher(dbPatcher)
{
    public override bool RunPatcher() =>
        base.RunPatcher() && IntegrationSettingsPatcher.Run(_dbPatcher);
}
