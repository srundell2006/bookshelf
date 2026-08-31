using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Books
{
    public interface IAuthorMovePreviewService
    {
        List<AuthorMovePreview> GetMovePreviews(List<int> authorIds);
        AuthorMovePreview GetMovePreview(int authorId);
    }

    // Works out where each author *should* live given the letter each root folder claims,
    // and what their book files would be called once they got there.
    //
    // Nothing here touches disk. The preview is deliberately conservative: an author is
    // only listed when there is a mapped destination that differs from where they already
    // are, so running the preview twice in a row produces an empty second result.
    public class AuthorMovePreviewService : IAuthorMovePreviewService
    {
        private readonly IAuthorService _authorService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IAuthorLetterService _letterService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IBuildFileNames _filenameBuilder;
        private readonly Logger _logger;

        public AuthorMovePreviewService(IAuthorService authorService,
                                        IMediaFileService mediaFileService,
                                        IAuthorLetterService letterService,
                                        IRootFolderService rootFolderService,
                                        IBuildFileNames filenameBuilder,
                                        Logger logger)
        {
            _authorService = authorService;
            _mediaFileService = mediaFileService;
            _letterService = letterService;
            _rootFolderService = rootFolderService;
            _filenameBuilder = filenameBuilder;
            _logger = logger;
        }

        public AuthorMovePreview GetMovePreview(int authorId)
        {
            return GetMovePreviews(new List<int> { authorId }).FirstOrDefault();
        }

        public List<AuthorMovePreview> GetMovePreviews(List<int> authorIds)
        {
            if (authorIds == null || authorIds.Empty())
            {
                return new List<AuthorMovePreview>();
            }

            var rootFolders = _rootFolderService.All();
            var namingConfig = (NamingConfig)null;
            var previews = new List<AuthorMovePreview>();

            foreach (var author in _authorService.GetAuthors(authorIds))
            {
                var preview = BuildPreview(author, rootFolders, namingConfig);

                if (preview != null)
                {
                    previews.Add(preview);
                }
            }

            return previews.OrderBy(p => p.NewPath, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private AuthorMovePreview BuildPreview(Author author, List<RootFolder> rootFolders, NamingConfig namingConfig)
        {
            var destination = _letterService.GetMappedRootFolder(author, rootFolders);

            // No root folder claims this author's letter (or the name yields no A-Z letter
            // at all). Leave them where they are rather than guessing a destination.
            if (destination == null)
            {
                _logger.Trace("No letter mapping for author {0}, skipping", author.Name);
                return null;
            }

            var folderName = _filenameBuilder.GetAuthorFolder(author, namingConfig);

            if (folderName.IsNullOrWhiteSpace())
            {
                _logger.Warn("Author folder format produced an empty name for {0}, skipping", author.Name);
                return null;
            }

            var newAuthorPath = Path.Combine(destination.Path, folderName);

            if (author.Path.IsNotNullOrWhiteSpace() && author.Path.PathEquals(newAuthorPath, StringComparison.Ordinal))
            {
                return null;
            }

            var preview = new AuthorMovePreview
            {
                AuthorId = author.Id,
                AuthorName = author.Name,
                Letter = _letterService.GetLetter(author),
                RootFolderPath = destination.Path,
                ExistingPath = author.Path,
                NewPath = newAuthorPath
            };

            preview.Files = BuildFilePreviews(author, newAuthorPath);

            return preview;
        }

        private List<AuthorMoveFilePreview> BuildFilePreviews(Author author, string newAuthorPath)
        {
            var files = _mediaFileService.GetFilesByAuthor(author.Id);
            var results = new List<AuthorMoveFilePreview>();

            if (files.Empty())
            {
                return results;
            }

            var counts = files.GroupBy(x => x.EditionId).ToDictionary(g => g.Key, g => g.Count());

            // Calibre-managed files are left alone, same as the rename preview does.
            foreach (var file in files.Where(x => x.CalibreId == 0))
            {
                var edition = file.Edition.Value;

                if (edition == null)
                {
                    _logger.Warn("File ({0}) is not linked to a book", file.Path);
                    continue;
                }

                file.PartCount = counts[file.EditionId];

                string newName;

                try
                {
                    newName = _filenameBuilder.BuildBookFileName(author, edition, file);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Couldn't build a file name for {0}, skipping", file.Path);
                    continue;
                }

                // BuildBookFilePath would anchor this to the author's *current* Path, which
                // is the thing we are about to change - so compose against the destination.
                var newPath = Path.Combine(newAuthorPath, newName + Path.GetExtension(file.Path));

                results.Add(new AuthorMoveFilePreview
                {
                    BookFileId = file.Id,
                    BookId = edition.BookId,
                    ExistingPath = file.Path,
                    NewPath = newPath
                });
            }

            return results.OrderBy(f => f.ExistingPath, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
