using System.Collections.Generic;

namespace NzbDrone.Core.Books
{
    public class AuthorMovePreview
    {
        public AuthorMovePreview()
        {
            Files = new List<AuthorMoveFilePreview>();
        }

        public int AuthorId { get; set; }
        public string AuthorName { get; set; }

        // The A-Z bucket the author's surname falls into, and the root folder that claims it.
        public string Letter { get; set; }
        public string RootFolderPath { get; set; }

        public string ExistingPath { get; set; }
        public string NewPath { get; set; }

        public List<AuthorMoveFilePreview> Files { get; set; }
    }

    public class AuthorMoveFilePreview
    {
        public int BookFileId { get; set; }
        public int BookId { get; set; }
        public string ExistingPath { get; set; }
        public string NewPath { get; set; }
    }
}
