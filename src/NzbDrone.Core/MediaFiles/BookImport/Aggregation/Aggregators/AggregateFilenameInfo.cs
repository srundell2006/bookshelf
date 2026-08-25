using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.BookImport.Aggregation.Aggregators
{
    public class AggregateFilenameInfo : IAggregate<LocalEdition>
    {
        private readonly Logger _logger;

        private static readonly List<Tuple<string, string>> CharsAndSeps = new List<Tuple<string, string>>
        {
            Tuple.Create(@"a-z0-9,\(\)\.&'’\s", @"\s_-"),
            Tuple.Create(@"a-z0-9,\(\)\.\&'’_", @"\s-")
        };

        private static Regex[] Patterns(string chars, string sep)
        {
            var sep1 = $@"(?<sep>[{sep}]+)";
            var sepn = @"\k<sep>";
            var author = $@"(?<author>[{chars}]+)";
            var track = $@"(?<track>\d+)";
            var title = $@"(?<title>[{chars}]+)";
            var tag = $@"(?<tag>[{chars}]+)";

            return new[]
            {
                new Regex($@"^{track}{sep1}{author}{sepn}{title}{sepn}{tag}$", RegexOptions.IgnoreCase),
                new Regex($@"^{track}{sep1}{author}{sepn}{tag}{sepn}{title}$", RegexOptions.IgnoreCase),
                new Regex($@"^{track}{sep1}{author}{sepn}{title}$", RegexOptions.IgnoreCase),

                new Regex($@"^{author}{sep1}{tag}{sepn}{track}{sepn}{title}$", RegexOptions.IgnoreCase),
                new Regex($@"^{author}{sep1}{track}{sepn}{title}{sepn}{tag}$", RegexOptions.IgnoreCase),
                new Regex($@"^{author}{sep1}{track}{sepn}{title}$", RegexOptions.IgnoreCase),

                new Regex($@"^{author}{sep1}{title}{sepn}{tag}$", RegexOptions.IgnoreCase),
                new Regex($@"^{author}{sep1}{tag}{sepn}{title}$", RegexOptions.IgnoreCase),
                new Regex($@"^{author}{sep1}{title}$", RegexOptions.IgnoreCase),

                new Regex($@"^{track}{sep1}{title}$", RegexOptions.IgnoreCase),
                new Regex($@"^{track}{sep1}{tag}{sepn}{title}$", RegexOptions.IgnoreCase),
                new Regex($@"^{track}{sep1}{title}{sepn}{tag}$", RegexOptions.IgnoreCase),

                new Regex($@"^{title}$", RegexOptions.IgnoreCase),
            };
        }

        private static readonly Regex TrailingYearRegex = new Regex(@"[\s_-]*\(\d{3,4}\)\s*$", RegexOptions.Compiled);

        private static readonly Regex LeadingTrackRegex = new Regex(@"^\s*\d+[\s._-]+", RegexOptions.Compiled);

        public AggregateFilenameInfo(Logger logger)
        {
            _logger = logger;
        }

        public LocalEdition Aggregate(LocalEdition release, bool others)
        {
            var tracks = release.LocalBooks;
            if (tracks.Any(x => x.FileTrackInfo.BookTitle.IsNullOrWhiteSpace())
                || tracks.Any(x => x.FileTrackInfo.AuthorTitle.IsNullOrWhiteSpace()))
            {
                _logger.Debug("Missing data in tags, trying filename augmentation");
                foreach (var charSep in CharsAndSeps)
                {
                    foreach (var pattern in Patterns(charSep.Item1, charSep.Item2))
                    {
                        var matches = AllMatches(tracks, pattern);
                        if (matches != null)
                        {
                            ApplyMatches(matches, pattern);
                        }
                    }
                }
            }

            return release;
        }

        private Dictionary<LocalBook, Match> AllMatches(List<LocalBook> tracks, Regex pattern)
        {
            var matches = new Dictionary<LocalBook, Match>();
            foreach (var track in tracks)
            {
                var filename = Path.GetFileNameWithoutExtension(track.Path).RemoveAccent();
                var match = pattern.Match(filename);
                _logger.Trace("Matching '{0}' against regex {1}", filename, pattern);
                if (match.Success && match.Groups[0].Success)
                {
                    matches[track] = match;
                }
                else
                {
                    return null;
                }
            }

            return matches;
        }

        private bool EqualFields(IEnumerable<Match> matches, string field)
        {
            return matches.Select(x => x.Groups[field].Value).Distinct().Count() == 1;
        }

        private void ApplyMatches(Dictionary<LocalBook, Match> matches, Regex pattern)
        {
            _logger.Debug("Got filename match with regex {0}", pattern);

            var keys = pattern.GetGroupNames();
            var someMatch = matches.First().Value;

            // only proceed if the 'tag' field is equal across all filenames
            if (keys.Contains("tag") && !EqualFields(matches.Values, "tag"))
            {
                _logger.Trace("Abort - 'tag' varies between matches");
                return;
            }

            // Given both an "author" and "title" field, assume that one is
            // *actually* the author, which must be uniform, and use the other
            // for the title. This, of course, won't work for VA books.
            string titleField;
            string author;
            if (keys.Contains("author"))
            {
                var authorUniform = EqualFields(matches.Values, "author");
                var titleUniform = keys.Contains("title") && EqualFields(matches.Values, "title");

                if (authorUniform && titleUniform)
                {
                    // Both fields are uniform, so uniformity cannot tell us which one is the
                    // author. This is always the case for a single-file release, where each
                    // field trivially has one distinct value. Falling back to "leftmost wins"
                    // silently swaps author and title for libraries named "Title - Author".
                    // The folder the file sits in is usually named after the author, so use
                    // that to break the tie.
                    titleField = PreferFieldByFolder(matches);
                    author = someMatch.Groups[titleField == "title" ? "author" : "title"].Value.Trim();
                }
                else if (authorUniform)
                {
                    author = someMatch.Groups["author"].Value.Trim();
                    titleField = "title";
                }
                else if (titleUniform)
                {
                    author = someMatch.Groups["title"].Value.Trim();
                    titleField = "author";
                }
                else
                {
                    _logger.Trace("Abort - both author and title vary between matches");

                    // both vary, abort
                    return;
                }

                _logger.Debug("Got author from filename: {0}", author);

                foreach (var track in matches.Keys)
                {
                    if (track.FileTrackInfo.AuthorTitle.IsNullOrWhiteSpace())
                    {
                        track.FileTrackInfo.Authors = new List<string> { author };
                    }
                }
            }
            else
            {
                // no author - remaining field is the title
                titleField = "title";
            }

            // Apply the title and track
            foreach (var track in matches.Keys)
            {
                if (track.FileTrackInfo.BookTitle.IsNullOrWhiteSpace())
                {
                    var title = matches[track].Groups[titleField].Value.Trim();
                    _logger.Debug("Got title from filename: {0}", title);
                    track.FileTrackInfo.BookTitle = title;
                }

                var trackNums = track.FileTrackInfo.TrackNumbers;
                if (keys.Contains("track") && (trackNums.Count() == 0 || trackNums.First() == 0))
                {
                    var tracknum = Convert.ToInt32(matches[track].Groups["track"].Value);
                    if (tracknum > 100)
                    {
                        track.FileTrackInfo.DiscNumber = tracknum / 100;
                        _logger.Debug("Got disc number from filename: {0}", tracknum / 100);
                        tracknum = tracknum % 100;
                    }

                    _logger.Debug("Got track number from filename: {0}", tracknum);
                    track.FileTrackInfo.TrackNumbers = new[] { tracknum };
                }
            }
        }

        /// <summary>
        /// Decide which regex group holds the book title when both "author" and "title" are
        /// uniform across the release. Compares each candidate against the names of the
        /// directories containing the files - libraries almost always file a book under a
        /// folder named for its author. Returns the group name to use as the title, keeping
        /// the historical "leftmost is the author" answer when the folders tell us nothing.
        /// </summary>
        private string PreferFieldByFolder(Dictionary<LocalBook, Match> matches)
        {
            const double minScore = 0.8;

            double authorScore = 0;
            double titleScore = 0;

            foreach (var pair in matches)
            {
                var folders = FolderNames(pair.Key.Path);
                if (!folders.Any())
                {
                    continue;
                }

                authorScore += BestFolderScore(folders, pair.Value.Groups["author"].Value);
                titleScore += BestFolderScore(folders, pair.Value.Groups["title"].Value);
            }

            // The field that looks like the folder name is the author, so the OTHER one is
            // the title. Only override the default when the evidence is clear.
            if (titleScore > authorScore && titleScore >= minScore)
            {
                _logger.Debug("Folder names suggest the trailing field is the author (title {0:0.00} vs author {1:0.00}); reading filenames as 'Title - Author'", titleScore, authorScore);
                return "author";
            }

            return "title";
        }

        private static List<string> FolderNames(string path)
        {
            var names = new List<string>();
            var dir = Path.GetDirectoryName(path);

            // the author folder is usually the parent, but a series subfolder is common too
            for (var i = 0; i < 2 && dir.IsNotNullOrWhiteSpace(); i++)
            {
                var name = Path.GetFileName(dir);
                if (name.IsNotNullOrWhiteSpace())
                {
                    names.Add(name);
                }

                dir = Path.GetDirectoryName(dir);
            }

            return names;
        }

        private static double BestFolderScore(List<string> folders, string candidate)
        {
            var cleaned = LeadingTrackRegex.Replace(TrailingYearRegex.Replace(candidate ?? string.Empty, string.Empty), string.Empty).Trim().RemoveAccent();

            if (cleaned.IsNullOrWhiteSpace())
            {
                return 0;
            }

            double best = 0;
            foreach (var folder in folders)
            {
                var score = folder.RemoveAccent().FuzzyMatch(cleaned, 0.6).Item3;
                if (score > best)
                {
                    best = score;
                }
            }

            return best;
        }

    }
}
