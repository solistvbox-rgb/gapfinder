using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace GapFinder.Data
{
    /// <summary>
    /// Reads/writes the search results and the ignore list as JSON in the plugin data folder. Results are also
    /// kept in memory so filtering/paging on the page does not re-read the file. All access uses one lock.
    /// </summary>
    public class ResultStore
    {
        private static readonly object Sync = new object();
        private static SeriesResults _seriesCache;
        private static MovieResults _movieCache;

        private readonly IJsonSerializer _json;
        private readonly GapFinderLogger _logger;

        public ResultStore(IJsonSerializer json, GapFinderLogger logger)
        {
            _json = json;
            _logger = logger;
        }

        /// <summary>The serializer, also used for the provider cache file.</summary>
        public IJsonSerializer Json => _json;

        public SeriesResults LoadSeries()
        {
            lock (Sync)
            {
                if (_seriesCache == null)
                {
                    _seriesCache = Read<SeriesResults>(Plugin.Instance.SeriesResultsFilePath) ?? new SeriesResults();
                    CalendarDate.NormalizeDates(_seriesCache);
                }
                return _seriesCache;
            }
        }

        public void SaveSeries(SeriesResults results)
        {
            lock (Sync)
            {
                Write(Plugin.Instance.SeriesResultsFilePath, results);
                _seriesCache = results;
            }
        }

        public MovieResults LoadMovies()
        {
            lock (Sync)
            {
                if (_movieCache == null)
                {
                    _movieCache = Read<MovieResults>(Plugin.Instance.MovieResultsFilePath) ?? new MovieResults();
                    CalendarDate.NormalizeDates(_movieCache);
                }
                return _movieCache;
            }
        }

        public void SaveMovies(MovieResults results)
        {
            lock (Sync)
            {
                Write(Plugin.Instance.MovieResultsFilePath, results);
                _movieCache = results;
            }
        }

        /// <summary>Deletes the saved results of both tabs; the ignore list is kept.</summary>
        public void ClearResults()
        {
            lock (Sync)
            {
                foreach (var path in new[] { Plugin.Instance.SeriesResultsFilePath, Plugin.Instance.MovieResultsFilePath })
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                _seriesCache = new SeriesResults();
                _movieCache = new MovieResults();
            }
        }

        /// <summary>
        /// After "Remove from collection": remove the movie from the wrong assignments of this collection, so the
        /// page no longer shows it without a new search.
        /// </summary>
        public void RemoveWrongAssignment(string movieId, string collectionTmdbId)
        {
            lock (Sync)
            {
                var results = LoadMovies();
                var changed = false;
                foreach (var collection in results.Collections ?? new List<CollectionResult>())
                {
                    if (!string.Equals(collection.TmdbId, collectionTmdbId, StringComparison.OrdinalIgnoreCase) || collection.WronglyAssigned == null)
                    {
                        continue;
                    }
                    changed |= collection.WronglyAssigned.RemoveAll(m => string.Equals(m.Id, movieId, StringComparison.OrdinalIgnoreCase)) > 0;
                }
                if (changed)
                {
                    SaveMovies(results);
                }
            }
        }

        /// <summary>
        /// After cleaning up a collection deleted on TMDB: remove the cleaned movies from it; if none are left, the
        /// collection is dropped from the result.
        /// </summary>
        public void RemoveCollectionMovies(string collectionTmdbId, IEnumerable<string> movieIds)
        {
            var ids = new HashSet<string>(movieIds.Where(id => id != null), StringComparer.OrdinalIgnoreCase);
            lock (Sync)
            {
                var results = LoadMovies();
                var collections = results.Collections ?? new List<CollectionResult>();
                var changed = false;
                foreach (var collection in collections.Where(c => string.Equals(c.TmdbId, collectionTmdbId, StringComparison.OrdinalIgnoreCase)))
                {
                    changed |= collection.Present != null && collection.Present.RemoveAll(m => ids.Contains(m.Id)) > 0;
                }
                changed |= collections.RemoveAll(c => c.RemovedFromTmdb && string.Equals(c.TmdbId, collectionTmdbId, StringComparison.OrdinalIgnoreCase) &&
                    (c.Present == null || c.Present.Count == 0)) > 0;
                if (changed)
                {
                    SaveMovies(results);
                }
            }
        }

        public HashSet<string> LoadIgnored()
        {
            lock (Sync)
            {
                var list = Read<IgnoreList>(Plugin.Instance.IgnoreListFilePath) ?? new IgnoreList();
                return new HashSet<string>(list.Keys ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            }
        }

        public HashSet<string> SetIgnored(IEnumerable<string> keys, bool ignore)
        {
            lock (Sync)
            {
                var set = LoadIgnored();
                foreach (var key in keys)
                {
                    if (ignore)
                    {
                        set.Add(key);
                    }
                    else
                    {
                        set.Remove(key);
                    }
                }
                Write(Plugin.Instance.IgnoreListFilePath, new IgnoreList { Keys = new List<string>(set) });
                return set;
            }
        }

        private T Read<T>(string path) where T : class
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }
                return _json.DeserializeFromFile<T>(path);
            }
            catch (Exception ex)
            {
                _logger.Error("[GapFinder] Could not read {0}: {1}", path, ex.Message);
                return null;
            }
        }

        // Write a temp file and replace afterwards, so a crash while writing never leaves half a JSON file. The data
        // folder is created only here (see Plugin.DataFolder); nothing is written during/after an uninstall.
        private void Write(string path, object value)
        {
            if (Plugin.IsUninstalling)
            {
                _logger.Info("[GapFinder] The plugin is being uninstalled - {0} is not saved.", path);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            _json.SerializeToFile(value, tmp);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(tmp, path);
        }
    }
}
