using System.Collections.Generic;
using System.Linq;
using Readarr.Http.REST;

namespace Readarr.Api.V1.Author
{
    public class MoveAuthorResource : RestResource
    {
        public int AuthorId { get; set; }
        public string AuthorName { get; set; }
        public string Letter { get; set; }
        public string RootFolderPath { get; set; }
        public string ExistingPath { get; set; }
        public string NewPath { get; set; }
        public List<MoveAuthorFileResource> Files { get; set; }
    }

    public class MoveAuthorFileResource
    {
        public int BookFileId { get; set; }
        public int BookId { get; set; }
        public string ExistingPath { get; set; }
        public string NewPath { get; set; }
    }

    public static class MoveAuthorResourceMapper
    {
        public static MoveAuthorResource ToResource(this NzbDrone.Core.Books.AuthorMovePreview model)
        {
            if (model == null)
            {
                return null;
            }

            return new MoveAuthorResource
            {
                // The frontend keys rows on authorId; Id is set so the table has a stable
                // key even if an author somehow appears twice.
                Id = model.AuthorId,
                AuthorId = model.AuthorId,
                AuthorName = model.AuthorName,
                Letter = model.Letter,
                RootFolderPath = model.RootFolderPath,
                ExistingPath = model.ExistingPath,
                NewPath = model.NewPath,
                Files = model.Files?.Select(ToResource).ToList() ?? new List<MoveAuthorFileResource>()
            };
        }

        public static MoveAuthorFileResource ToResource(this NzbDrone.Core.Books.AuthorMoveFilePreview model)
        {
            if (model == null)
            {
                return null;
            }

            return new MoveAuthorFileResource
            {
                BookFileId = model.BookFileId,
                BookId = model.BookId,
                ExistingPath = model.ExistingPath,
                NewPath = model.NewPath
            };
        }

        public static List<MoveAuthorResource> ToResource(this IEnumerable<NzbDrone.Core.Books.AuthorMovePreview> models)
        {
            return models.Select(ToResource).ToList();
        }
    }
}
