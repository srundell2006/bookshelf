using System;
using System.Linq;
using NLog;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books
{
    // The metadata provider does not supply birth or death dates -- BookInfo's author
    // payload carries only id, name, description, image, url and ratings -- so
    // MapAuthorMetadata hardcodes every author to Continuing and the Ended filter can
    // never match anything.
    //
    // Readarr's Ended really means "no longer producing new work" rather than
    // "deceased", and that is inferable from data we already hold: an author whose
    // newest release is older than AuthorEndedAfterYears is treated as Ended.
    //
    // This runs off AuthorRefreshCompleteEvent, which the refresh pipeline publishes
    // after children have been refreshed and saved, so the book list is current. It is
    // self-correcting: a new release moves the author straight back to Continuing.
    public class AuthorStatusHandler : IHandle<AuthorRefreshCompleteEvent>
    {
        private readonly IBookService _bookService;
        private readonly IAuthorMetadataService _authorMetadataService;
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public AuthorStatusHandler(IBookService bookService,
                                   IAuthorMetadataService authorMetadataService,
                                   IConfigService configService,
                                   Logger logger)
        {
            _bookService = bookService;
            _authorMetadataService = authorMetadataService;
            _configService = configService;
            _logger = logger;
        }

        public void Handle(AuthorRefreshCompleteEvent message)
        {
            var author = message.Author;

            if (author?.Metadata?.Value == null)
            {
                return;
            }

            var years = _configService.AuthorEndedAfterYears;

            // A non-positive threshold disables the heuristic, leaving whatever the
            // metadata provider set.
            if (years <= 0)
            {
                return;
            }

            var latestRelease = _bookService.GetBooksByAuthor(author.Id)
                .Select(x => x.ReleaseDate)
                .Where(x => x.HasValue)
                .Max();

            // No dated books tells us nothing about whether the author is still
            // active, so leave them Continuing rather than guessing.
            if (!latestRelease.HasValue)
            {
                return;
            }

            var status = latestRelease.Value < DateTime.UtcNow.AddYears(-years)
                ? AuthorStatusType.Ended
                : AuthorStatusType.Continuing;

            var metadata = author.Metadata.Value;

            if (metadata.Status == status)
            {
                return;
            }

            _logger.Debug("Author {0} moving from {1} to {2}; newest release {3:yyyy-MM-dd} against a {4} year threshold",
                          author.Name,
                          metadata.Status,
                          status,
                          latestRelease.Value,
                          years);

            metadata.Status = status;
            _authorMetadataService.Upsert(metadata);
        }
    }
}
