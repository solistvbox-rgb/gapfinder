using System;
using System.Collections.Generic;
using System.Linq;

namespace GapFinder.Api
{
    /// <summary>
    /// Maps TMDB collections to Emby collections (BoxSets), using the same rule as removing from a collection:
    /// a BoxSet with a TMDB id must match it; otherwise (e.g. created by hand) the exact same name.
    /// </summary>
    public static class CollectionLinker
    {
        /// <summary>Maximum number of collections per request.</summary>
        public const int MaxRequests = 5000;

        public class EmbyCollection
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string TmdbId { get; set; }
        }

        public static bool Matches(string itemTmdbId, string itemName, string tmdbId, string name)
        {
            if (!string.IsNullOrEmpty(itemTmdbId))
            {
                return string.Equals(itemTmdbId.Trim(), (tmdbId ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
            }
            return !string.IsNullOrWhiteSpace(name) && string.Equals(itemName?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The matching Emby collection for each requested collection. A TMDB id match wins over a name match;
        /// MatchCount gives the number of matches. Collections without a match are left out.
        /// </summary>
        public static List<EmbyCollectionDto> Match(IEnumerable<CollectionRefDto> wanted, IReadOnlyCollection<EmbyCollection> boxSets)
        {
            var result = new List<EmbyCollectionDto>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var w in (wanted ?? Enumerable.Empty<CollectionRefDto>()).Take(MaxRequests))
            {
                if (w == null || string.IsNullOrWhiteSpace(w.TmdbId) || !seen.Add(w.TmdbId.Trim()))
                {
                    continue;
                }
                var matches = boxSets
                    .Where(b => !string.IsNullOrEmpty(b.Id) && Matches(b.TmdbId, b.Name, w.TmdbId, w.Name))
                    .OrderBy(b => string.IsNullOrEmpty(b.TmdbId) ? 1 : 0)
                    .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (matches.Count > 0)
                {
                    result.Add(new EmbyCollectionDto { TmdbId = w.TmdbId.Trim(), Id = matches[0].Id, Name = matches[0].Name, MatchCount = matches.Count });
                }
            }
            return result;
        }
    }
}
