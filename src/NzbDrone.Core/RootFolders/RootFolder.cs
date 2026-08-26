using System.Collections.Generic;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.RootFolders
{
    public class RootFolder : ModelBase
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public int DefaultMetadataProfileId { get; set; }
        public int DefaultQualityProfileId { get; set; }
        public MonitorTypes DefaultMonitorOption { get; set; }
        public NewItemMonitorTypes DefaultNewItemMonitorOption { get; set; }
        public HashSet<int> DefaultTags { get; set; } = new ();
        public bool IsCalibreLibrary { get; set; }
        public CalibreSettings CalibreSettings { get; set; }

        // Author surname initials filed under this folder, used to pick the default
        // root folder when adding an author. Empty means the folder claims no letters.
        public List<string> Letters { get; set; } = new ();

        // Day of the month (1-31) on which the scheduled scan covers this folder.
        // Null means scan it on every scheduled run.
        public int? ScanDayOfMonth { get; set; }

        public bool Accessible { get; set; }
        public long? FreeSpace { get; set; }
        public long? TotalSpace { get; set; }
    }
}
