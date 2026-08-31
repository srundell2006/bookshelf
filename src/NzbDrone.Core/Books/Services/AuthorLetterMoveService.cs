using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Books
{
    // Files authors into the root folder that claims their surname's letter, then renames
    // their book files into the naming scheme once they have landed.
    //
    // Deliberately thin: the moving and renaming are done by the existing
    // BulkMoveAuthorCommand and RenameAuthorCommand rather than reimplemented here, so
    // this only has to decide *where* each author belongs. That also means the established
    // failure behaviour still applies - MoveAuthorService reverts an author's path in the
    // database if the folder transfer throws.
    public class AuthorLetterMoveService : IExecute<MoveAuthorToLetterCommand>
    {
        private readonly IAuthorService _authorService;
        private readonly IAuthorLetterService _letterService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly Logger _logger;

        public AuthorLetterMoveService(IAuthorService authorService,
                                       IAuthorLetterService letterService,
                                       IRootFolderService rootFolderService,
                                       IManageCommandQueue commandQueueManager,
                                       Logger logger)
        {
            _authorService = authorService;
            _letterService = letterService;
            _rootFolderService = rootFolderService;
            _commandQueueManager = commandQueueManager;
            _logger = logger;
        }

        public void Execute(MoveAuthorToLetterCommand message)
        {
            if (message.AuthorIds == null || message.AuthorIds.Empty())
            {
                return;
            }

            var rootFolders = _rootFolderService.All();
            var authors = _authorService.GetAuthors(message.AuthorIds);

            // Destination root -> the authors headed there, with the path they are leaving.
            var groups = new Dictionary<string, List<BulkMoveAuthor>>();
            var authorsToUpdate = new List<Author>();

            foreach (var author in authors)
            {
                var destination = _letterService.GetMappedRootFolder(author, rootFolders);

                if (destination == null)
                {
                    _logger.Debug("No root folder claims the letter for {0}, leaving in place", author.Name);
                    continue;
                }

                if (author.Path.IsNullOrWhiteSpace())
                {
                    _logger.Debug("{0} has no path on disk, skipping", author.Name);
                    continue;
                }

                if (!groups.TryGetValue(destination.Path, out var group))
                {
                    group = new List<BulkMoveAuthor>();
                    groups[destination.Path] = group;
                }

                group.Add(new BulkMoveAuthor
                {
                    AuthorId = author.Id,
                    SourcePath = author.Path
                });

                author.RootFolderPath = destination.Path;
                authorsToUpdate.Add(author);
            }

            if (authorsToUpdate.Empty())
            {
                _logger.ProgressInfo("No authors need moving");
                return;
            }

            _logger.ProgressInfo("Filing {0} authors into {1} letter folders", authorsToUpdate.Count, groups.Count);

            // Queue the moves before the rows are updated, matching how the author editor
            // does it: the database is pointed at the destination first and the move
            // command reverts it if the transfer fails.
            foreach (var group in groups)
            {
                _commandQueueManager.Push(new BulkMoveAuthorCommand
                {
                    DestinationRootFolder = group.Key,
                    Author = group.Value
                });
            }

            // useExistingRelativeFolder: false - rebuild the folder name from the naming
            // config under the new root rather than carrying the old relative path across,
            // which is what would re-create a nested letter folder inside a letter folder.
            _authorService.UpdateAuthors(authorsToUpdate, false);

            // Renaming runs last so the files are already sitting in their new home.
            _commandQueueManager.Push(new RenameAuthorCommand
            {
                AuthorIds = authorsToUpdate.Select(a => a.Id).ToList()
            });
        }
    }
}
