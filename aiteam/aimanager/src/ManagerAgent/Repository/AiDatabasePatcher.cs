using DFCommonLib.DataAccess;

namespace ManagerAgent.Repository;

public class AiDatabasePatcher : StartupDatabasePatcher
{
    public AiDatabasePatcher(IDBPatcher dbPatcher) : base(dbPatcher)
    {
    }

    public override bool RunPatcher()
    {
        return base.RunPatcher();
    }
}
