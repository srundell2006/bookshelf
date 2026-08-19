using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.MediaFiles
{
    public interface IMediaFileTableCleanupService
    {
        void Clean(string folder, List<string> filesOnDisk);
    }

    public class MediaFileTableCleanupService : IMediaFileTableCleanupService
    {
        // A scan of one folder should never be able to remove a big multiple of what that folder
        // actually holds.  If it tries, the lookup has gone wrong rather than the library, so stop
        // and say so instead of deleting.  Small folders still clean normally thanks to the floor.
        private const int SuspiciousDeleteFloor = 50;
        private const int SuspiciousDeleteFactor = 10;

        private readonly IMediaFileService _mediaFileService;
        private readonly Logger _logger;

        public MediaFileTableCleanupService(IMediaFileService mediaFileService,
                                            Logger logger)
        {
            _mediaFileService = mediaFileService;
            _logger = logger;
        }

        public void Clean(string folder, List<string> filesOnDisk)
        {
            var safeFolder = folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            // Only ever consider rows that really do live under the folder being scanned.  The
            // lookup behind this is a SQL LIKE prefix match, so a folder whose name contains a LIKE
            // wildcard used to pull in rows from completely unrelated folders - a folder named "_"
            // matched every single-character folder - and every one of them then looked "missing
            // from disk" and was deleted.  Re-checking the boundary here means a lookup that
            // over-matches can no longer authorise a delete outside the scanned folder.
            var dbFiles = _mediaFileService.GetFilesWithBasePath(folder)
                .Where(x => x.Path != null && x.Path.StartsWith(safeFolder, DiskProviderBase.PathStringComparison))
                .ToList();

            // get files in database that are missing on disk and remove from database
            var missingFiles = dbFiles.ExceptBy(x => x.Path, filesOnDisk, x => x, PathEqualityComparer.Instance).ToList();

            if (!missingFiles.Any())
            {
                return;
            }

            // An empty filesOnDisk means the caller found the folder gone entirely and does want
            // every row under it removed, so the ratio check only applies once something was
            // actually enumerated.  That is also the case it needs to catch: a partial listing of a
            // large folder (a flaky network mount returning a short read) otherwise looks exactly
            // like thousands of books having been deleted at once.
            if (filesOnDisk.Count > 0 &&
                missingFiles.Count > SuspiciousDeleteFloor &&
                missingFiles.Count > filesOnDisk.Count * SuspiciousDeleteFactor)
            {
                _logger.Error("Refusing to clean {0}: it would remove {1} files from the database but only {2} were found on disk. That ratio means the lookup is wrong, not the library. Nothing has been deleted.",
                              folder,
                              missingFiles.Count,
                              filesOnDisk.Count);
                return;
            }

            _logger.Debug("The following files no longer exist on disk, removing from db:\n{0}",
                          string.Join("\n", missingFiles.Select(x => x.Path)));

            _mediaFileService.DeleteMany(missingFiles, DeleteMediaFileReason.MissingFromDisk);
        }
    }
}
