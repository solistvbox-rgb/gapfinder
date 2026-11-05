using System;
using System.Collections.Generic;

namespace GapFinder.Data
{
    // =====================================================================
    // Search results, stored as JSON and served by the API unchanged. Specials, upcoming episodes/movies and
    // ignored entries are NOT filtered out during the search, only marked - so the filters on the page apply
    // immediately, without a new search.
    // =====================================================================

    public class SeriesResults
    {
        public DateTime? ScanDateUtc { get; set; }
        public string[] LibraryNames { get; set; } = new string[0];
        /// <summary>Libraries without a suitable metadata downloader (see ProviderCheck), shown as a hint.</summary>
        public List<string> Warnings { get; set; } = new List<string>();
        public List<SeriesResult> Series { get; set; } = new List<SeriesResult>();
    }

    public class SeriesResult
    {
        /// <summary>Emby item id (GetClientId) - for the image URL, link and ignore key.</summary>
        public string Id { get; set; }
        public string Name { get; set; }
        public int? Year { get; set; }
        /// <summary>"Continuing", "Ended" or null (unknown).</summary>
        public string Status { get; set; }
        public string LibraryName { get; set; }
        public Dictionary<string, string> ProviderIds { get; set; } = new Dictionary<string, string>();
        /// <summary>Provider of the episode list: "Tvdb", "Tmdb" or null (none active/unknown) - for the link in the UI.</summary>
        public string EpisodeProvider { get; set; }
        /// <summary>The provider returned no episode list (e.g. no TVDB/TMDB id).</summary>
        public bool NoProviderData { get; set; }
        public string Error { get; set; }
        public List<SeasonResult> Seasons { get; set; } = new List<SeasonResult>();
    }

    public class SeasonResult
    {
        public int SeasonNumber { get; set; }
        /// <summary>Provider episodes of this season that are present locally.</summary>
        public int PresentCount { get; set; }
        public List<MissingEpisode> Missing { get; set; } = new List<MissingEpisode>();
    }

    public class MissingEpisode
    {
        public int SeasonNumber { get; set; }
        public int EpisodeNumber { get; set; }
        public int? EpisodeNumberEnd { get; set; }
        public string Name { get; set; }
        public DateTimeOffset? PremiereDate { get; set; }
        /// <summary>No air date, but counted as aired: the series has ended or a later episode has already aired.</summary>
        public bool AssumedAired { get; set; }
        public string Overview { get; set; }
        public Dictionary<string, string> ProviderIds { get; set; } = new Dictionary<string, string>();
    }

    public class MovieResults
    {
        public DateTime? ScanDateUtc { get; set; }
        public string[] LibraryNames { get; set; } = new string[0];
        /// <summary>Libraries without a suitable metadata downloader (see ProviderCheck), shown as a hint.</summary>
        public List<string> Warnings { get; set; } = new List<string>();
        public List<CollectionResult> Collections { get; set; } = new List<CollectionResult>();
    }

    public class CollectionResult
    {
        /// <summary>TMDB collection id - also the ignore key.</summary>
        public string TmdbId { get; set; }
        public string Name { get; set; }
        public string Error { get; set; }
        /// <summary>TMDB no longer knows the collection (HTTP 404). Present = the movies that still name it.</summary>
        public bool RemovedFromTmdb { get; set; }
        public List<PresentMovie> Present { get; set; } = new List<PresentMovie>();
        public List<MissingMovie> Missing { get; set; } = new List<MissingMovie>();
        /// <summary>
        /// Local movies whose metadata (ImportedCollections/NFO) names this collection although they do not belong
        /// to it on TMDB - e.g. after an earlier misidentification.
        /// </summary>
        public List<PresentMovie> WronglyAssigned { get; set; } = new List<PresentMovie>();
    }

    public class PresentMovie
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public int? Year { get; set; }
    }

    public class MissingMovie
    {
        public string TmdbId { get; set; }
        public string Name { get; set; }
        public int? Year { get; set; }
        public DateTimeOffset? PremiereDate { get; set; }
        public string Overview { get; set; }
        public string ThumbnailUrl { get; set; }
    }

    /// <summary>
    /// Ignored entries as keys: "series:{id}", "season:{id}:{s}", "episode:{id}:{s}:{e}",
    /// "collection:{tmdbId}", "movie:{tmdbId}", "assignment:{movieId}:{collectionTmdbId}" (see IgnoreKeys).
    /// </summary>
    public class IgnoreList
    {
        public List<string> Keys { get; set; } = new List<string>();
    }

    public static class IgnoreKeys
    {
        public static string Series(string seriesId) => "series:" + seriesId;
        public static string Season(string seriesId, int season) => "season:" + seriesId + ":" + season;
        public static string Episode(string seriesId, int season, int episode) => "episode:" + seriesId + ":" + season + ":" + episode;
        public static string Collection(string tmdbId) => "collection:" + tmdbId;
        public static string Movie(string tmdbId) => "movie:" + tmdbId;

        /// <summary>
        /// Ignoring a wrong assignment ("movie names the collection but does not belong to it on TMDB") accepts it:
        /// the movie then counts as a present part of the collection.
        /// </summary>
        public static string Assignment(string movieId, string collectionTmdbId) => "assignment:" + movieId + ":" + collectionTmdbId;

        public static bool IsValid(string key)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > 200)
            {
                return false;
            }
            var prefix = key.Split(':')[0];
            return prefix == "series" || prefix == "season" || prefix == "episode" || prefix == "collection" || prefix == "movie" || prefix == "assignment";
        }
    }
}
