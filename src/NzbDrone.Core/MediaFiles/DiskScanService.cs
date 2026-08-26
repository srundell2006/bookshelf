using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.MediaFiles
{
    public interface IDiskScanService
    {
        void Scan(List<string> folders = null, FilterFilesType filter = FilterFilesType.Known, bool addNewAuthors = false, List<int> authorIds = null);
        IFileInfo[] GetBookFiles(string path, bool allDirectories = true);
        string[] GetNonBookFiles(string path, bool allDirectories = true);
        List<IFileInfo> FilterFiles(string basePath, IEnumerable<IFileInfo> files);
        List<string> FilterPaths(string basePath, IEnumerable<string> paths);
    }

    public class DiskScanService :
        IDiskScanService,
        IExecute<RescanFoldersCommand>
    {
        public static readonly Regex ExcludedSubFoldersRegex = new Regex(@"(?:\\|\/|^)(?:extras|@eadir|extrafanart|plex versions|\.[^\\/]+)(?:\\|\/)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        public static readonly Regex ExcludedFilesRegex = new Regex(@"^\._|^Thumbs\.db$|^\.DS_store$|\.partial~$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Number of files identified and imported per commit.  Kept small deliberately: the cost
        // of an extra round of small queries per batch is trivial next to holding an entire
        // library's worth of decisions in memory and writing nothing until the scan ends.
        private const int ImportBatchSize = 100;

        // Trailing part/volume/disc markers, used to tell whether two adjacent files in the same
        // directory belong to the same book before deciding a batch boundary is safe.
        private static readonly Regex MultiPartSuffixRegex = new Regex(@"[\s._-]*(?:\(?(?:part|pt|vol|volume|book|disc|cd)[\s._-]*\d+\)?|\(?\d+\s*of\s*\d+\)?)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly IConfigService _configService;
        private readonly IDiskProvider _diskProvider;
        private readonly ICalibreProxy _calibre;
        private readonly IMediaFileService _mediaFileService;
        private readonly IMakeImportDecision _importDecisionMaker;
        private readonly IImportApprovedBooks _importApprovedTracks;
        private readonly IAuthorService _authorService;
        private readonly IMediaFileTableCleanupService _mediaFileTableCleanupService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        public DiskScanService(IConfigService configService,
                               IDiskProvider diskProvider,
                               ICalibreProxy calibre,
                               IMediaFileService mediaFileService,
                               IMakeImportDecision importDecisionMaker,
                               IImportApprovedBooks importApprovedTracks,
                               IAuthorService authorService,
                               IRootFolderService rootFolderService,
                               IMediaFileTableCleanupService mediaFileTableCleanupService,
                               IEventAggregator eventAggregator,
                               Logger logger)
        {
            _configService = configService;
            _diskProvider = diskProvider;
            _calibre = calibre;

            _mediaFileService = mediaFileService;
            _importDecisionMaker = importDecisionMaker;
            _importApprovedTracks = importApprovedTracks;
            _authorService = authorService;
            _mediaFileTableCleanupService = mediaFileTableCleanupService;
            _rootFolderService = rootFolderService;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        // A folder asking for day 31 would otherwise never be scanned in a month that has
        // no 31st. Clamp to the last day of the current month so every folder gets its turn
        // in every month, including February.
        private static bool IsDueToday(int scanDay, int today, int daysInMonth)
        {
            return today == Math.Min(scanDay, daysInMonth);
        }

        public void Scan(List<string> folders = null, FilterFilesType filter = FilterFilesType.Known, bool addNewAuthors = false, List<int> authorIds = null)
        {
            var scheduledRun = folders == null;

            if (scheduledRun)
            {
                // A scheduled run covers every root folder, which on a large library means
                // walking the whole tree before a single file is looked at. Root folders can
                // opt into a day of the month instead, so the work spreads across the month
                // and each run only walks the folders due today. A folder with no day set
                // keeps the old behaviour and is scanned every time.
                var now = DateTime.Now;
                var today = now.Day;
                var daysInMonth = DateTime.DaysInMonth(now.Year, now.Month);
                var all = _rootFolderService.All();

                folders = all
                    .Where(x => x.ScanDayOfMonth == null || IsDueToday(x.ScanDayOfMonth.Value, today, daysInMonth))
                    .Select(x => x.Path)
                    .ToList();

                var skipped = all.Count - folders.Count;

                if (skipped > 0)
                {
                    _logger.Info("Scheduled scan covering {0} of {1} root folders due on day {2} of the month", folders.Count, all.Count, today);
                }
            }

            if (authorIds == null)
            {
                authorIds = new List<int>();
            }

            // Collect paths rather than IFileInfo for the whole scan.  A path is a few dozen
            // bytes; an IFileInfo caches stat data and runs to kilobytes, so materialising one
            // per file up front costs gigabytes on a large library before a single book is
            // imported.  IFileInfo is created per batch instead, just before it is needed.
            var mediaFilePaths = new List<string>();

            var musicFilesStopwatch = Stopwatch.StartNew();

            foreach (var folder in folders)
            {
                // We could be scanning a root folder or a subset of a root folder.  If it's a subset,
                // check if the root folder exists before cleaning.
                var rootFolder = _rootFolderService.GetBestRootFolder(folder);

                if (rootFolder == null)
                {
                    _logger.Error("Not scanning {0}, it's not a subdirectory of a defined root folder", folder);
                    return;
                }

                var folderExists = _diskProvider.FolderExists(folder);

                if (!folderExists)
                {
                    if (!_diskProvider.FolderExists(rootFolder.Path))
                    {
                        _logger.Warn("Authors' root folder ({0}) doesn't exist.", rootFolder.Path);
                        var skippedAuthors = _authorService.GetAuthors(authorIds);
                        skippedAuthors.ForEach(x => _eventAggregator.PublishEvent(new AuthorScanSkippedEvent(x, AuthorScanSkippedReason.RootFolderDoesNotExist)));
                        return;
                    }

                    if (_diskProvider.FolderEmpty(rootFolder.Path))
                    {
                        _logger.Warn("Authors' root folder ({0}) is empty.", rootFolder.Path);
                        var skippedAuthors = _authorService.GetAuthors(authorIds);
                        skippedAuthors.ForEach(x => _eventAggregator.PublishEvent(new AuthorScanSkippedEvent(x, AuthorScanSkippedReason.RootFolderIsEmpty)));
                        return;
                    }
                }

                if (!folderExists)
                {
                    _logger.Debug("Specified scan folder ({0}) doesn't exist.", folder);

                    CleanMediaFiles(folder, new List<string>());
                    continue;
                }

                _logger.ProgressInfo("Scanning {0}", folder);

                var paths = FilterPaths(folder, GetBookFilePaths(folder));

                if (!paths.Any())
                {
                    _logger.Warn("Scan folder {0} is empty.", folder);
                    continue;
                }

                CleanMediaFiles(folder, paths);
                mediaFilePaths.AddRange(paths);
            }

            musicFilesStopwatch.Stop();
            _logger.Trace("Finished getting track files for:\n{0} [{1}]", folders.ConcatToString("\n"), musicFilesStopwatch.Elapsed);

            var config = new ImportDecisionMakerConfig
            {
                Filter = filter,
                IncludeExisting = true,
                AddNewAuthors = addNewAuthors
            };

            var importStopwatch = Stopwatch.StartNew();

            // Process the scan in batches instead of building decisions for every file and
            // committing once at the end.  On a large library that single pass holds every
            // LocalBook in memory for the whole run, throughput degrades as it goes, and
            // nothing at all is written until the last book is identified - so an interrupted
            // scan loses everything.  Each batch below is identified, imported and reconciled
            // on its own, so progress is durable and restartable.
            var batches = CreateImportBatches(mediaFilePaths, ImportBatchSize);

            var totalNew = 0;
            var totalUpdated = 0;

            for (var batchNumber = 0; batchNumber < batches.Count; batchNumber++)
            {
                var batch = batches[batchNumber];

                _logger.ProgressInfo("Importing batch {0}/{1} ({2} files)", batchNumber + 1, batches.Count, batch.Count);

                try
                {
                    var (batchNew, batchUpdated) = ImportBatch(batch, config, batchNumber, batches.Count);

                    totalNew += batchNew;
                    totalUpdated += batchUpdated;
                }
                catch (Exception ex)
                {
                    // One bad batch must not destroy the whole run.  Scanning a large library is
                    // hours of work, and before this a single failing insert threw all the way out
                    // of Execute, leaving every remaining batch unscanned.  Log it and carry on;
                    // the batch will be offered again by the next scan.
                    _logger.Error(ex, "Failed to import batch {0}/{1}, continuing with the next batch", batchNumber + 1, batches.Count);
                }
            }

            _logger.Debug($"Inserted {totalNew} new unmatched trackfiles");

            _logger.Debug($"Updated info for {totalUpdated} known files");

            var authors = _authorService.GetAuthors(authorIds);
            foreach (var author in authors)
            {
                CompletedScanning(author);
            }

            importStopwatch.Stop();
            _logger.Debug("Book import complete for:\n{0} [{1}]", folders.ConcatToString("\n"), importStopwatch.Elapsed);
        }

        private static List<List<string>> CreateImportBatches(List<string> paths, int batchSize)
        {
            // Group by containing directory before batching.  Every file that belongs to the same
            // book has to reach TrackGroupingService together - splitting a multi-part book across
            // two batches would let each half be identified on its own and mis-matched.  Grouping
            // explicitly (rather than relying on enumeration order) keeps this correct whatever
            // order the provider walks the tree in.
            //
            // A directory is no longer kept whole regardless of size.  A root folder holding
            // thousands of loose files would otherwise become one enormous batch: it commits
            // nothing until it finishes, takes hours, and is repeated in full whenever a scan is
            // restarted.  Oversized directories are chunked instead - but only ever on a boundary
            // between two different books, so a multi-file book is still never split.
            var batches = new List<List<string>>();
            var current = new List<string>();

            foreach (var group in paths.GroupBy(x => Path.GetDirectoryName(x), PathEqualityComparer.Instance))
            {
                foreach (var chunk in ChunkDirectory(group.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(), batchSize))
                {
                    if (current.Count > 0 && current.Count + chunk.Count > batchSize)
                    {
                        batches.Add(current);
                        current = new List<string>();
                    }

                    current.AddRange(chunk);
                }
            }

            if (current.Count > 0)
            {
                batches.Add(current);
            }

            return batches;
        }

        private static IEnumerable<List<string>> ChunkDirectory(List<string> orderedPaths, int batchSize)
        {
            // Walk the directory in name order and only close a chunk once it is big enough AND
            // the next file belongs to a different book, so every file of a multi-file book stays
            // together.  A single book spanning more files than the batch size still comes through
            // whole, which is the behaviour that matters for correctness.
            var chunk = new List<string>();
            string previousKey = null;

            foreach (var path in orderedPaths)
            {
                var key = GetBookKey(path);

                if (chunk.Count >= batchSize &&
                    previousKey != null &&
                    !string.Equals(key, previousKey, StringComparison.OrdinalIgnoreCase))
                {
                    yield return chunk;
                    chunk = new List<string>();
                }

                chunk.Add(path);
                previousKey = key;
            }

            if (chunk.Count > 0)
            {
                yield return chunk;
            }
        }

        private static string GetBookKey(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path) ?? string.Empty;

            // Strip a trailing part/volume marker so "Title Part 1" and "Title Part 2" share a key
            // and cannot be separated.  Over-stripping is harmless here - it only makes two
            // adjacent files look like one book and delays a split - whereas under-stripping could
            // split a book, so the pattern errs towards stripping.
            var stripped = MultiPartSuffixRegex.Replace(name, string.Empty).Trim();

            return stripped.Length > 0 ? stripped : name;
        }

        private (int New, int Updated) ImportBatch(List<string> batch, ImportDecisionMakerConfig config, int batchNumber, int batchCount)
        {
            var decisionsStopwatch = Stopwatch.StartNew();

            // Stat the files for this batch only.  These go out of scope when the batch ends.
            // Checking Exists both drops files that disappeared between enumeration and now
            // and primes the cached stat data, so the importer reading Size later cannot
            // throw FileNotFoundException and abort the whole scan over one missing file.
            var batchFiles = batch
                .Select(x => _diskProvider.GetFileInfo(x))
                .Where(x => x.Exists)
                .ToList();

            if (!batchFiles.Any())
            {
                return (0, 0);
            }

            var decisions = _importDecisionMaker.GetImportDecisions(batchFiles, null, null, config);

            decisionsStopwatch.Stop();
            _logger.Debug("Import decisions complete for batch {0}/{1} [{2}]", batchNumber + 1, batchCount, decisionsStopwatch.Elapsed);

            _importApprovedTracks.Import(decisions, false);

            // decisions may have been filtered to just new files.  Anything new and approved will have been inserted.
            // Now we need to make sure anything new but not approved gets inserted.
            // Look the decision paths up exactly rather than by containing folder.  Identification
            // pulls in additional files already on disk that belong to the same book but can sit
            // outside this batch; if those are not recognised as known they get inserted a second
            // time and violate IX_BookFiles_Path, which aborts the entire scan.  A folder lookup
            // is not safe for that check because it compiles to ILIKE '<folder>%' and a folder
            // name containing a backslash is mangled by ILIKE's escape handling, so its own files
            // come back unmatched.  Exact matching also avoids reading every row under the root
            // on every batch.  Note that knownFiles will include anything imported just now.
            var decisionPaths = decisions
                .Select(x => x.Item.Path)
                .Distinct(PathEqualityComparer.Instance)
                .ToList();

            var knownFiles = _mediaFileService.GetFilesWithPaths(decisionPaths);

            var newFiles = decisions
                .ExceptBy(x => x.Item.Path, knownFiles, x => x.Path, PathEqualityComparer.Instance)
                .GroupBy(x => x.Item.Path, PathEqualityComparer.Instance)
                .Select(g => g.First())
                .Select(decision => new BookFile
                {
                    Path = decision.Item.Path,
                    CalibreId = decision.Item.CalibreId,
                    Part = decision.Item.Part,
                    PartCount = decision.Item.PartCount,
                    Size = decision.Item.Size,
                    Modified = decision.Item.Modified,
                    DateAdded = DateTime.UtcNow,
                    Quality = decision.Item.Quality,
                    MediaInfo = decision.Item.FileTrackInfo.MediaInfo,
                    Edition = decision.Item.Edition
                })
                .ToList();
            _mediaFileService.AddMany(newFiles);

            // finally update info on size/modified for existing files
            var updatedFiles = knownFiles
                .Join(decisions,
                      x => x.Path,
                      x => x.Item.Path,
                      (file, decision) => new
                      {
                          File = file,
                          Item = decision.Item
                      },
                      PathEqualityComparer.Instance)
                .Where(x => x.File.Size != x.Item.Size ||
                       Math.Abs((x.File.Modified - x.Item.Modified).TotalSeconds) > 1)
                .Select(x =>
                {
                    x.File.Size = x.Item.Size;
                    x.File.Modified = x.Item.Modified;
                    x.File.MediaInfo = x.Item.FileTrackInfo.MediaInfo;
                    x.File.Quality = x.Item.Quality;
                    return x.File;
                })
                .ToList();

            _mediaFileService.Update(updatedFiles);

            return (newFiles.Count, updatedFiles.Count);
        }

        private List<string> GetBookFilePaths(string path)
        {
            var rootFolder = _rootFolderService.GetBestRootFolder(path);

            if (rootFolder != null && rootFolder.IsCalibreLibrary && rootFolder.CalibreSettings != null)
            {
                _logger.Info($"Getting book list from calibre for {path}");
                return _calibre.GetAllBookFilePaths(rootFolder.CalibreSettings)
                    .Where(x => path.IsParentPath(x))
                    .ToList();
            }

            _logger.Debug("Scanning '{0}' for ebook files", path);

            var found = _diskProvider.GetFiles(path, true)
                .Where(file => MediaFileExtensions.AllExtensions.Contains(Path.GetExtension(file)))
                .ToList();

            _logger.Debug("{0} book files were found in {1}", found.Count, path);

            return found;
        }

        private void CleanMediaFiles(string folder, List<string> mediaFileList)
        {
            _logger.Debug($"Cleaning up media files in DB [{folder}]");
            _mediaFileTableCleanupService.Clean(folder, mediaFileList);
        }

        private void CompletedScanning(Author author)
        {
            _logger.Info("Completed scanning disk for {0}", author.Name);
            _eventAggregator.PublishEvent(new AuthorScannedEvent(author));
        }

        public IFileInfo[] GetBookFiles(string path, bool allDirectories = true)
        {
            IEnumerable<IFileInfo> filesOnDisk;

            var rootFolder = _rootFolderService.GetBestRootFolder(path);

            _logger.Trace(rootFolder.ToJson());

            if (rootFolder != null && rootFolder.IsCalibreLibrary && rootFolder.CalibreSettings != null)
            {
                _logger.Info($"Getting book list from calibre for {path}");
                var paths = _calibre.GetAllBookFilePaths(rootFolder.CalibreSettings);
                var folderPaths = paths.Where(x => path.IsParentPath(x));

                filesOnDisk = folderPaths.Select(x => _diskProvider.GetFileInfo(x));
            }
            else
            {
                _logger.Debug("Scanning '{0}' for ebook files", path);

                filesOnDisk = _diskProvider.GetFileInfos(path, allDirectories);

                _logger.Trace("{0} files were found in {1}", filesOnDisk.Count(), path);
            }

            var mediaFileList = filesOnDisk.Where(file => MediaFileExtensions.AllExtensions.Contains(file.Extension))
                .ToArray();

            _logger.Debug("{0} book files were found in {1}", mediaFileList.Length, path);

            return mediaFileList;
        }

        public string[] GetNonBookFiles(string path, bool allDirectories = true)
        {
            _logger.Debug("Scanning '{0}' for non-ebook files", path);

            var filesOnDisk = _diskProvider.GetFiles(path, allDirectories).ToList();

            var mediaFileList = filesOnDisk.Where(file => !MediaFileExtensions.AllExtensions.Contains(Path.GetExtension(file)))
                                           .ToList();

            _logger.Trace("{0} files were found in {1}", filesOnDisk.Count, path);
            _logger.Debug("{0} non-ebook files were found in {1}", mediaFileList.Count, path);

            return mediaFileList.ToArray();
        }

        public List<string> FilterPaths(string basePath, IEnumerable<string> paths)
        {
            return paths.Where(file => !ExcludedSubFoldersRegex.IsMatch(basePath.GetRelativePath(file)))
                        .Where(file => !ExcludedFilesRegex.IsMatch(Path.GetFileName(file)))
                        .ToList();
        }

        public List<IFileInfo> FilterFiles(string basePath, IEnumerable<IFileInfo> files)
        {
            return files.Where(file => !ExcludedSubFoldersRegex.IsMatch(basePath.GetRelativePath(file.FullName)))
                        .Where(file => !ExcludedFilesRegex.IsMatch(file.Name))
                        .ToList();
        }

        public void Execute(RescanFoldersCommand message)
        {
            Scan(message.Folders, message.Filter, message.AddNewAuthors, message.AuthorIds);
        }
    }
}
