using System.Collections.Generic;
using System.IO;
using System.Linq;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.MediaFiles
{
    public interface IMediaFileRepository : IBasicRepository<BookFile>
    {
        List<BookFile> GetFilesByAuthor(int authorId);
        List<BookFile> GetFilesByAuthorMetadataId(int authorMetadataId);
        List<BookFile> GetFilesByBook(int bookId);
        List<BookFile> GetFilesByEdition(int editionId);
        List<BookFile> GetUnmappedFiles();
        List<BookFile> GetFilesWithBasePath(string path);
        List<BookFile> GetFilesWithPaths(List<string> paths);
        List<BookFile> GetFileWithPath(List<string> paths);
        BookFile GetFileWithPath(string path);
        void DeleteFilesByBook(int bookId);
        void UnlinkFilesByBook(int bookId);
    }

    public class MediaFileRepository : BasicRepository<BookFile>, IMediaFileRepository
    {
        public MediaFileRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        // always join with all the other good stuff
        // needed more often than not so better to load it all now
        protected override SqlBuilder Builder() => new SqlBuilder(_database.DatabaseType)
            .LeftJoin<BookFile, Edition>((b, e) => b.EditionId == e.Id)
            .LeftJoin<Edition, Book>((e, b) => e.BookId == b.Id)
            .LeftJoin<Book, Author>((book, author) => book.AuthorMetadataId == author.AuthorMetadataId)
            .LeftJoin<Author, AuthorMetadata>((a, m) => a.AuthorMetadataId == m.Id);

        protected override List<BookFile> Query(SqlBuilder builder) => Query(_database, builder).ToList();

        public static IEnumerable<BookFile> Query(IDatabase database, SqlBuilder builder)
        {
            return database.QueryJoined<BookFile, Edition, Book, Author, AuthorMetadata>(builder, (file, edition, book, author, metadata) => Map(file, edition, book, author, metadata));
        }

        private static BookFile Map(BookFile file, Edition edition, Book book, Author author, AuthorMetadata metadata)
        {
            file.Edition = edition;

            if (edition != null)
            {
                edition.Book = book;
            }

            if (author != null)
            {
                author.Metadata = metadata;
            }

            file.Author = author;

            return file;
        }

        public List<BookFile> GetFilesByAuthor(int authorId)
        {
            return Query(Builder().Where<Author>(a => a.Id == authorId));
        }

        public List<BookFile> GetFilesByAuthorMetadataId(int authorMetadataId)
        {
            return Query(Builder().Where<Book>(b => b.AuthorMetadataId == authorMetadataId));
        }

        public List<BookFile> GetFilesByBook(int bookId)
        {
            return Query(Builder().Where<Book>(b => b.Id == bookId));
        }

        public List<BookFile> GetFilesByEdition(int editionId)
        {
            return Query(Builder().Where<BookFile>(f => f.EditionId == editionId));
        }

        public List<BookFile> GetUnmappedFiles()
        {
            return _database.Query<BookFile>(new SqlBuilder(_database.DatabaseType).Select(typeof(BookFile))
                                              .Where<BookFile>(t => t.EditionId == 0)).ToList();
        }

        public void DeleteFilesByBook(int bookId)
        {
            var fileIds = GetFilesByBook(bookId).Select(x => x.Id).ToList();
            Delete(x => fileIds.Contains(x.Id));
        }

        public void UnlinkFilesByBook(int bookId)
        {
            var files = GetFilesByBook(bookId);
            files.ForEach(x => x.EditionId = 0);
            SetFields(files, f => f.EditionId);
        }

        public List<BookFile> GetFilesWithBasePath(string path)
        {
            // ensure path ends with a single trailing path separator to avoid matching partial paths
            var safePath = path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            // StartsWith compiles to "Path ILIKE @prefix || '%'", so the prefix is interpreted as a
            // LIKE pattern rather than a literal.  A folder named "_" therefore matched every
            // single-character folder, and a folder containing "%" would match almost anything -
            // callers that delete whatever this returns then reach far outside the folder they were
            // given.  The SQL stays as a coarse, index-friendly prefilter and the real boundary is
            // enforced here, which also keeps behaviour identical on SQLite (whose generated LIKE
            // has no ESCAPE clause to escape into).
            return _database.Query<BookFile>(new SqlBuilder(_database.DatabaseType).Where<BookFile>(x => x.Path.StartsWith(safePath)))
                .Where(x => x.Path != null && x.Path.StartsWith(safePath, DiskProviderBase.PathStringComparison))
                .ToList();
        }

        public List<BookFile> GetFilesWithPaths(List<string> paths)
        {
            // Exact match on a set of paths, compiled to "Path = ANY (@paths)".
            //
            // Deliberately not GetFilesWithBasePath: StartsWith compiles to ILIKE '<prefix>%',
            // and ILIKE treats a backslash in the prefix as an escape character, so any folder
            // whose name contains one silently fails to match its own files.  A caller checking
            // "is this file already known" would then be told no and insert it a second time.
            //
            // Deliberately not GetFileWithPath(List): that one reads every BookFile row joined
            // to Editions and filters in memory, which is far too expensive to run per batch.
            if (paths == null || paths.Count == 0)
            {
                return new List<BookFile>();
            }

            return _database.Query<BookFile>(new SqlBuilder(_database.DatabaseType).Where<BookFile>(x => paths.Contains(x.Path))).ToList();
        }

        public BookFile GetFileWithPath(string path)
        {
            return Query(x => x.Path == path).SingleOrDefault();
        }

        public List<BookFile> GetFileWithPath(List<string> paths)
        {
            // use more limited join for speed
            var builder = new SqlBuilder(_database.DatabaseType)
                .LeftJoin<BookFile, Edition>((f, t) => f.EditionId == t.Id);

            var all = _database.QueryJoined<BookFile, Edition>(builder, (file, book) => MapTrack(file, book)).ToList();

            var joined = all.Join(paths, x => x.Path, x => x, (file, path) => file, PathEqualityComparer.Instance).ToList();
            return joined;
        }

        private BookFile MapTrack(BookFile file, Edition book)
        {
            file.Edition = book;
            return file;
        }
    }
}
