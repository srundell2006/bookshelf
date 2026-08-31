using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Books
{
    public interface IAuthorLetterService
    {
        string GetLetter(Author author);
        RootFolder GetMappedRootFolder(Author author);
        RootFolder GetMappedRootFolder(Author author, List<RootFolder> rootFolders);
    }

    // Works out which letter bucket an author belongs to, and which root folder has
    // claimed that letter.
    //
    // This is the server-side twin of frontend/src/Utilities/Author/getAuthorLetter.js
    // and the two must agree: the frontend uses it to pre-select a root folder when an
    // author is added, and this is used to decide where an existing author should be
    // filed. If they disagree, adding an author and then previewing a move would
    // propose relocating an author who was already filed correctly.
    public class AuthorLetterService : IAuthorLetterService
    {
        private static readonly Regex SingleLetter = new Regex(@"^[A-Z]$", RegexOptions.Compiled);

        private readonly IRootFolderService _rootFolderService;

        public AuthorLetterService(IRootFolderService rootFolderService)
        {
            _rootFolderService = rootFolderService;
        }

        public string GetLetter(Author author)
        {
            if (author?.Metadata?.Value == null)
            {
                return null;
            }

            var metadata = author.Metadata.Value;

            // Surname-first forms ("le Carre, John") already lead with the character we
            // want, and get multi-word surnames right: John le Carre belongs under L.
            // Empty-vs-whitespace matters here. The frontend selects these with `||`, so
            // "" falls through to the next field but "   " does not - it is taken as the
            // surname-first form and yields no letter at all. Using a whitespace check
            // instead would quietly file such an author under their given name.
            var lastFirst = string.IsNullOrEmpty(metadata.SortNameLastFirst)
                ? metadata.NameLastFirst
                : metadata.SortNameLastFirst;

            if (!string.IsNullOrEmpty(lastFirst))
            {
                return InitialOf(Fold(lastFirst));
            }

            // Natural order ("Matt Dinniman") - the surname is the last word, so the
            // first character would file the author under their given name instead.
            var natural = string.IsNullOrEmpty(metadata.SortName) ? metadata.Name : metadata.SortName;

            if (string.IsNullOrEmpty(natural))
            {
                return null;
            }

            var words = Fold(natural).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

            return words.Length == 0 ? null : InitialOf(words[^1]);
        }

        public RootFolder GetMappedRootFolder(Author author)
        {
            return GetMappedRootFolder(author, _rootFolderService.All());
        }

        public RootFolder GetMappedRootFolder(Author author, List<RootFolder> rootFolders)
        {
            if (rootFolders == null || rootFolders.Empty())
            {
                return null;
            }

            var letter = GetLetter(author);

            if (letter == null)
            {
                return null;
            }

            // No fallback on purpose. An author whose letter nobody claims stays where
            // they are rather than being swept into whichever root happens to sort first.
            return rootFolders.FirstOrDefault(r => r.Letters != null &&
                                                   r.Letters.Any(l => l.IsNotNullOrWhiteSpace() &&
                                                                      l.Trim().ToUpperInvariant() == letter));
        }

        private static string Fold(string value)
        {
            var decomposed = value.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);

            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(c);
                }
            }

            return builder.ToString().Trim();
        }

        private static string InitialOf(string value)
        {
            if (value.IsNullOrWhiteSpace())
            {
                return null;
            }

            var letter = value.Substring(0, 1).ToUpperInvariant();

            return SingleLetter.IsMatch(letter) ? letter : null;
        }
    }
}
