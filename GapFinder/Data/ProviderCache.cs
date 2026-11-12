using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Model.Serialization;

namespace GapFinder.Data
{
    // =====================================================================
    // Provider answers kept between searches, so a search does not ask TheTVDB/TMDB again for data that does not
    // change: episode lists of ended series, the collection of a movie and the movies of a collection. Emby's own
    // library scan does not ask the providers for existing items, so without this cache every Gap Finder search
    // would send one request per series, movie and collection. See CachePolicy for when an entry is reused.
    // =====================================================================

    public class ProviderCacheData
    {
        public int Version { get; set; } = 1;
        /// <summary>Key: Emby series id (GetClientId).</summary>
        public Dictionary<string, CachedEpisodeList> Series { get; set; } = new Dictionary<string, CachedEpisodeList>();
        /// <summary>Key: "tmdb:{id}" or "imdb:{id}" of the movie.</summary>
        public Dictionary<string, CachedMovieCollections> Movies { get; set; } = new Dictionary<string, CachedMovieCollections>();
        /// <summary>Key: TMDB collection id.</summary>
        public Dictionary<string, CachedCollectionParts> Collections { get; set; } = new Dictionary<string, CachedCollectionParts>();
    }

    public class CachedEpisodeList
    {
        public DateTime FetchedUtc { get; set; }
        /// <summary>"Tvdb"/"Tmdb" and the series id at that provider - a re-identified series is fetched again.</summary>
        public string Provider { get; set; }
        public string ProviderSeriesId { get; set; }
        public string Language { get; set; }
        /// <summary>Series status when fetched; only lists of ended series are reused.</summary>
        public string Status { get; set; }
        public List<CachedEpisode> Episodes { get; set; } = new List<CachedEpisode>();
    }

    public class CachedEpisode
    {
        public int? Season { get; set; }
        public int? Number { get; set; }
        public int? NumberEnd { get; set; }
        public string Name { get; set; }
        public DateTimeOffset? PremiereDate { get; set; }
        /// <summary>Only kept for episodes that were missing when fetched - keeps the file small.</summary>
        public string Overview { get; set; }
        public Dictionary<string, string> ProviderIds { get; set; } = new Dictionary<string, string>();
    }

    public class CachedMovieCollections
    {
        public DateTime FetchedUtc { get; set; }
        public string Language { get; set; }
        public List<CollectionRef> Collections { get; set; } = new List<CollectionRef>();
    }

    public class CachedCollectionParts
    {
        public DateTime FetchedUtc { get; set; }
        public string Language { get; set; }
        /// <summary>TMDB answered 404: the collection no longer exists.</summary>
        public bool NotFound { get; set; }
        public List<CachedPart> Parts { get; set; } = new List<CachedPart>();
        /// <summary>TMDB/IMDb ids of the local movies that named the collection when fetched (incl. wrongly assigned ones).</summary>
        public List<string> LocalMemberIds { get; set; } = new List<string>();
    }

    public class CachedPart
    {
        public string TmdbId { get; set; }
        public string ImdbId { get; set; }
        public string Name { get; set; }
        public int? Year { get; set; }
        public DateTimeOffset? PremiereDate { get; set; }
        public string Overview { get; set; }
        public string ThumbnailUrl { get; set; }
    }

    /// <summary>A TMDB collection a movie belongs to.</summary>
    public class CollectionRef
    {
        public string TmdbId { get; set; }
        public string Name { get; set; }
    }

    /// <summary>When a cached provider answer may be reused. Pure functions, so they can be tested.</summary>
    public static class CachePolicy
    {
        /// <summary>Ended series: the episode list is fetched again after 60-74 days (corrections, new specials).</summary>
        public static readonly TimeSpan EndedSeriesMaxAge = TimeSpan.FromDays(60);
        /// <summary>A movie's collection: TMDB creates collections when a sequel is announced - checked every 30-44 days.</summary>
        public static readonly TimeSpan MovieCollectionsMaxAge = TimeSpan.FromDays(30);
        /// <summary>Movies of a collection with only released movies: every 14-28 days.</summary>
        public static readonly TimeSpan CollectionPartsMaxAge = TimeSpan.FromDays(14);
        /// <summary>Collections with announced or undated movies change often (dates, new parts): every 3 days.</summary>
        public static readonly TimeSpan OpenCollectionPartsMaxAge = TimeSpan.FromDays(3);

        public static bool IsEnded(string status) => string.Equals(status, "Ended", StringComparison.OrdinalIgnoreCase);

        public static bool CanReuseEpisodes(CachedEpisodeList entry, string status, string provider, string providerSeriesId, string language, string key, DateTime nowUtc) =>
            entry != null
            && IsEnded(status) && IsEnded(entry.Status)
            && string.Equals(entry.Provider, provider, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(providerSeriesId) && string.Equals(entry.ProviderSeriesId, providerSeriesId, StringComparison.OrdinalIgnoreCase)
            && SameLanguage(entry.Language, language)
            && IsFresh(entry.FetchedUtc, EndedSeriesMaxAge, key, nowUtc);

        public static bool CanReuseMovieCollections(CachedMovieCollections entry, string language, string key, DateTime nowUtc) =>
            entry != null && SameLanguage(entry.Language, language) && IsFresh(entry.FetchedUtc, MovieCollectionsMaxAge, key, nowUtc);

        /// <param name="localMemberIds">TMDB/IMDb ids of the local movies that name the collection.</param>
        public static bool CanReuseCollectionParts(CachedCollectionParts entry, IEnumerable<string> localMemberIds, string language, string key, DateTime nowUtc, DateTime today)
        {
            if (entry == null || !SameLanguage(entry.Language, language))
            {
                return false;
            }
            // A new local movie that names the collection may be a new part of it - ask TMDB again.
            var known = new HashSet<string>((entry.Parts ?? new List<CachedPart>()).SelectMany(p => new[] { p.TmdbId, p.ImdbId })
                .Concat(entry.LocalMemberIds ?? new List<string>()).Where(id => !string.IsNullOrEmpty(id)), StringComparer.OrdinalIgnoreCase);
            if (localMemberIds.Any(id => !string.IsNullOrEmpty(id) && !known.Contains(id)))
            {
                return false;
            }
            var open = (entry.Parts ?? new List<CachedPart>()).Any(p => !p.PremiereDate.HasValue || p.PremiereDate.Value.UtcDateTime.Date > today.Date);
            return IsFresh(entry.FetchedUtc, open ? OpenCollectionPartsMaxAge : CollectionPartsMaxAge, key, nowUtc);
        }

        /// <summary>
        /// Younger than maxAge plus a spread of up to two weeks (less for short ages) taken from the key, so entries
        /// fetched in the same search do not all expire on the same day.
        /// </summary>
        public static bool IsFresh(DateTime fetchedUtc, TimeSpan maxAge, string key, DateTime nowUtc)
        {
            var spreadDays = Math.Min(14, (int)(maxAge.TotalDays / 2));
            var spread = spreadDays <= 0 ? 0 : StableHash(key) % (spreadDays + 1);
            var age = nowUtc - fetchedUtc;
            return age >= TimeSpan.Zero && age < maxAge + TimeSpan.FromDays(spread);
        }

        private static bool SameLanguage(string a, string b) =>
            string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        // string.GetHashCode differs per process - this one is stable.
        private static int StableHash(string key)
        {
            unchecked
            {
                var h = 17;
                foreach (var c in key ?? string.Empty)
                {
                    h = h * 31 + c;
                }
                return h & 0x7fffffff;
            }
        }
    }

    /// <summary>
    /// The cache in memory during a search, read from and saved to provider-cache.json in the plugin data folder.
    /// Thread-safe for the parallel scanners.
    /// </summary>
    public class ProviderCache
    {
        private static readonly object FileSync = new object();

        public ConcurrentDictionary<string, CachedEpisodeList> Series { get; }
        public ConcurrentDictionary<string, CachedMovieCollections> Movies { get; }
        public ConcurrentDictionary<string, CachedCollectionParts> Collections { get; }

        /// <summary>Reused / fetched counters for the log line at the end of a search.</summary>
        public int SeriesReused;
        public int SeriesFetched;
        public int MoviesReused;
        public int MoviesFetched;
        public int CollectionsReused;
        public int CollectionsFetched;

        public ProviderCache(ProviderCacheData data)
        {
            data = data ?? new ProviderCacheData();
            Series = new ConcurrentDictionary<string, CachedEpisodeList>(data.Series ?? new Dictionary<string, CachedEpisodeList>(), StringComparer.OrdinalIgnoreCase);
            Movies = new ConcurrentDictionary<string, CachedMovieCollections>(data.Movies ?? new Dictionary<string, CachedMovieCollections>(), StringComparer.OrdinalIgnoreCase);
            Collections = new ConcurrentDictionary<string, CachedCollectionParts>(data.Collections ?? new Dictionary<string, CachedCollectionParts>(), StringComparer.OrdinalIgnoreCase);
        }

        public static string MovieKey(string tmdbId, string imdbId) =>
            !string.IsNullOrEmpty(tmdbId) ? "tmdb:" + tmdbId : !string.IsNullOrEmpty(imdbId) ? "imdb:" + imdbId : null;

        public static ProviderCache Load(IJsonSerializer json, GapFinderLogger logger)
        {
            lock (FileSync)
            {
                var path = Plugin.Instance.ProviderCacheFilePath;
                try
                {
                    if (File.Exists(path))
                    {
                        return new ProviderCache(json.DeserializeFromFile<ProviderCacheData>(path));
                    }
                }
                catch (Exception ex)
                {
                    logger.Warn("[GapFinder] The provider cache could not be read, all data is fetched again: {0}", ex.Message);
                }
                return new ProviderCache(null);
            }
        }

        /// <summary>Saves the cache; entries older than a year (series/movies no longer in the library) are dropped.</summary>
        public void Save(IJsonSerializer json, GapFinderLogger logger)
        {
            if (Plugin.IsUninstalling)
            {
                return;
            }
            var limit = DateTime.UtcNow.AddDays(-365);
            var data = new ProviderCacheData
            {
                Series = Series.Where(e => e.Value.FetchedUtc > limit).ToDictionary(e => e.Key, e => e.Value),
                Movies = Movies.Where(e => e.Value.FetchedUtc > limit).ToDictionary(e => e.Key, e => e.Value),
                Collections = Collections.Where(e => e.Value.FetchedUtc > limit).ToDictionary(e => e.Key, e => e.Value)
            };
            lock (FileSync)
            {
                var path = Plugin.Instance.ProviderCacheFilePath;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    var tmp = path + ".tmp";
                    json.SerializeToFile(data, tmp);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                    File.Move(tmp, path);
                }
                catch (Exception ex)
                {
                    logger.Warn("[GapFinder] The provider cache could not be saved: {0}", ex.Message);
                }
            }
        }

        /// <summary>Deletes the cache file (the next search asks the providers for everything).</summary>
        public static void Delete()
        {
            lock (FileSync)
            {
                var path = Plugin.Instance.ProviderCacheFilePath;
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }
}
