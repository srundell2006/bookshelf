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

        public void Scan(List<string> folders = null, FilterFilesType filter = FilterFilesType.Known, bool addNewAuthors = false, List<int> authorIds = null)
        {
            if (folders == null)
            {
                folders = _rootFolderService.All().Select(x => x.Path).ToList();
            }

            if (authorIds == null)
            {
                authorIds = new List<int>();
            }

            var mediaFileList = new List<IFileInfo>();

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

                var files = FilterFiles(folder, GetBookFiles(folder));

                if (!files.Any())
                {
                    _logger.Warn("Scan folder {0} is empty.", folder);
                    continue;
                }

                CleanMediaFiles(folder, files.Select(x => x.FullName).ToList());
                mediaFileList.AddRange(files);
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
            var batches = CreateImportBatches(mediaFileList, ImportBatchSize);

            var totalNew = 0;
            var totalUpdated = 0;

            for (var batchNumber = 0; batchNumber < batches.Count; batchNumber++)
            {
                var batch = batches[batchNumber];

                _logger.ProgressInfo("Importing batch {0}/{1} ({2} files)", batchNumber + 1, batches.Count, batch.Count);

                var decisionsStopwatch = Stopwatch.StartNew();

                var decisions = _importDecisionMaker.GetImportDecisions(batch, null, null, config);

                decisionsStopwatch.Stop();
                _logger.Debug("Import decisions complete for batch {0}/{1} [{2}]", batchNumber + 1, batches.Count, decisionsStopwatch.Elapsed);

                _importApprovedTracks.Import(decisions, false);

                // decisions may have been filtered to just new files.  Anything new and approved will have been inserted.
                // Now we need to make sure anything new but not approved gets inserted.
                // Only the directories this batch touched are re-read, so the lookup stays cheap
                // no matter how large the library is.  Note that knownFiles will include anything
                // imported just now.
                var knownFiles = new List<BookFile>();
                GetBatchFolders(batch).ForEach(x => knownFiles.AddRange(_mediaFileService.GetFilesWithBasePath(x)));

                var newFiles = decisions
                    .ExceptBy(x => x.Item.Path, knownFiles, x => x.Path, PathEqualityComparer.Instance)
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

                totalNew += newFiles.Count;

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

                totalUpdated += updatedFiles.Count;
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

        private static List<string> GetBatchFolders(List<IFileInfo> batch)
        {
            return batch
                .Select(x => Path.GetDirectoryName(x.FullName))
                .Where(x => x.IsNotNullOrWhiteSpace())
                .Distinct(PathEqualityComparer.Instance)
                .ToList();
        }

        private static List<List<IFileInfo>> CreateImportBatches(List<IFileInfo> files, int batchSize)
        {
            // Group by containing directory before batching.  Every file that belongs to the same
            // book has to reach TrackGroupingService together - splitting a multi-part book across
            // two batches would let each half be identified on its own and mis-matched.  A single
            // directory therefore always stays whole, even if it is larger than the batch size.
            var batches = new List<List<IFileInfo>>();
            var current = new List<IFileInfo>();

            foreach (var group in files.GroupBy(x => Path.GetDirectoryName(x.FullName), PathEqualityComparer.Instance))
            {
                var groupFiles = group.ToList();

                if (current.Count > 0 && current.Count + groupFiles.Count > batchSize)
                {
                    batches.Add(current);
                    current = new List<IFileInfo>();
                }

                current.AddRange(groupFiles);
            }

            if (current.Count > 0)
            {
                batches.Add(current);
            }

            return batches;
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
