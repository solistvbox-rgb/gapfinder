using System;
using System.Collections.Generic;
using GapFinder.Data;

namespace GapFinder.Api
{
    /// <summary>
    /// Filter rules of the export. Must match isEpisodeVisible/isMovieVisible in gapfinder-app.js, so the export
    /// contains exactly what the page shows with the same switches.
    /// </summary>
    public static class ResultFilter
    {
        /// <summary>No date, or a date from tomorrow on = not aired/released yet.</summary>
        public static bool IsReleased(DateTimeOffset? premiereDate)
        {
            return premiereDate.HasValue && premiereDate.Value.UtcDateTime.Date <= DateTime.UtcNow.Date;
        }

        /// <summary>Aired: a date today or earlier, or no date but assumed aired (see SeriesScanner.MarkAssumedAired).</summary>
        public static bool IsAired(MissingEpisode episode) => IsReleased(episode.PremiereDate) || episode.AssumedAired;

        public static bool IsEpisodeVisible(SeriesResult series, MissingEpisode episode, HashSet<string> ignored, bool includeSpecials, bool includeUnaired, bool includeIgnored)
        {
            if (!includeSpecials && episode.SeasonNumber == 0)
            {
                return false;
            }
            if (!includeUnaired && !IsAired(episode))
            {
                return false;
            }
            return includeIgnored || !IsEpisodeIgnored(series, episode, ignored);
        }

        public static bool IsEpisodeIgnored(SeriesResult series, MissingEpisode episode, HashSet<string> ignored)
        {
            return ignored.Contains(IgnoreKeys.Series(series.Id))
                || ignored.Contains(IgnoreKeys.Season(series.Id, episode.SeasonNumber))
                || ignored.Contains(IgnoreKeys.Episode(series.Id, episode.SeasonNumber, episode.EpisodeNumber));
        }

        public static bool IsMovieVisible(CollectionResult collection, MissingMovie movie, HashSet<string> ignored, bool includeUnreleased, bool includeIgnored)
        {
            if (!includeUnreleased && !IsReleased(movie.PremiereDate))
            {
                return false;
            }
            return includeIgnored || !IsMovieIgnored(collection, movie, ignored);
        }

        /// <summary>
        /// Present movies of the collection including accepted assignments (ignored "wrongly assigned" entries).
        /// Must match presentWithAccepted in gapfinder-app.js.
        /// </summary>
        public static List<PresentMovie> PresentWithAccepted(CollectionResult collection, HashSet<string> ignored)
        {
            var list = new List<PresentMovie>(collection.Present ?? new List<PresentMovie>());
            foreach (var movie in collection.WronglyAssigned ?? new List<PresentMovie>())
            {
                if (ignored.Contains(IgnoreKeys.Assignment(movie.Id, collection.TmdbId)))
                {
                    list.Add(movie);
                }
            }
            return list;
        }

        public static bool IsMovieIgnored(CollectionResult collection, MissingMovie movie, HashSet<string> ignored)
        {
            return ignored.Contains(IgnoreKeys.Collection(collection.TmdbId))
                || (!string.IsNullOrEmpty(movie.TmdbId) && ignored.Contains(IgnoreKeys.Movie(movie.TmdbId)));
        }
    }
}
