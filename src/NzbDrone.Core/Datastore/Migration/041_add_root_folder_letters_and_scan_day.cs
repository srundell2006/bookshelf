using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(041)]
    public class add_root_folder_letters_and_scan_day : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Alter.Table("RootFolders").AddColumn("Letters").AsString().Nullable();
            Alter.Table("RootFolders").AddColumn("ScanDayOfMonth").AsInt32().Nullable();
        }
    }
}
