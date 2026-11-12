using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;
using GapFinder.Data;

namespace GapFinder.Scanning
{
    /// <summary>
    /// Missing episodes/seasons. Same data source as Emby's /Shows/Missing (IProviderManager.GetAllEpisodes = first
    /// enabled series provider of the library), but series run in parallel, matching uses a HashSet instead of a
    /// linear search, and all missing episodes (including specials/upcoming) are determined once and saved.
    /// </summary>
    public class SeriesScanner
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly GapFinderLogger _logger;
        private readonly ProviderCache _cache;
        private readonly bool _useCache;

        /// <param name="cache">Saved provider answers (null = none); new answers are added to it.</param>
        /// <param name="useCache">false = ask the provider for every series ("Ignore cache").</param>
        public SeriesScanner(ILibraryManager libraryManager, IProviderManager providerManager, GapFinderLogger logger, ProviderCache cache = null, bool useCache = true)
        {
            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _logger = logger;
            _cache = cache;
            _useCache = useCache;
        }

        /// <summary>
        /// Hints for series libraries without a downloader that provides episode lists. Checked like the search, with
        /// a real series and its library options - Emby does not return the complete options for the library folder
        /// itself in mixed libraries. Libraries without series need no hint.
        /// </summary>
        public List<string> CheckProviders(List<LibraryInfo> libraries)
        {
            var warnings = new List<string>();
            foreach (var library in libraries.Where(l => LibraryHelper.CanContainSeries(l.CollectionType)))
            {
                var series = LibraryHelper.FirstItem(_libraryManager, library, nameof(Series));
                if (series == null)
                {
                    continue;
                }
                var options = _libraryManager.GetLibraryOptions(series);
                var enabled = _providerManager.GetEnabledMetadataProviders(series, options).OfType<ISeriesMetadataProvider>().Any();
                var warning = ProviderCheck.SeriesWarning(library.Name, enabled);
                if (warning != null)
                {
                    warnings.Add(warning);
                }
            }
            return warnings;
        }

        public List<KeyValuePair<Series, LibraryInfo>> CollectSeries(List<LibraryInfo> libraries)
        {
            var seen = new HashSet<long>();
            var result = new List<KeyValuePair<Series, LibraryInfo>>();
            foreach (var library in libraries.Where(l => LibraryHelper.CanContainSeries(l.CollectionType)))
            {
                var items = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { nameof(Series) },
                    Parent = library.Folder,
                    Recursive = true
                });
                foreach (var series in items.OfType<Series>())
                {
                    if (seen.Add(series.InternalId))
                    {
                        result.Add(new KeyValuePair<Series, LibraryInfo>(series, library));
                    }
                }
            }
            return result;
        }

        public async Task<SeriesResults> ScanAsync(List<KeyValuePair<Series, LibraryInfo>> seriesList, int maxParallel, Action<string, bool> report, CancellationToken token)
        {
            var bag = new ConcurrentBag<SeriesResult>();

            await ScanManager.ForEachParallelAsync(seriesList, maxParallel, token, async pair =>
            {
                var result = await ScanSeriesAsync(pair.Key, pair.Value, token).ConfigureAwait(false);
                bag.Add(result);
                report(pair.Key.Name, result.Error != null);
            }).ConfigureAwait(false);

            return new SeriesResults
            {
                ScanDateUtc = DateTime.UtcNow,
                Series = bag.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList()
            };
        }

        /// <summary>Provider name ("TheTVDB", "TheMovieDb") -> ProviderIds key ("Tvdb", "Tmdb"); otherwise null.</summary>
        public static string EpisodeProviderKey(string providerName)
        {
            if (string.IsNullOrEmpty(providerName))
            {
                return null;
            }
            if (providerName.IndexOf("TVDB", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Tvdb";
            }
            if (providerName.IndexOf("MovieDb", StringComparison.OrdinalIgnoreCase) >= 0 || providerName.IndexOf("TMDB", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Tmdb";
            }
            return null;
        }

        private async Task<SeriesResult> ScanSeriesAsync(Series series, LibraryInfo library, CancellationToken token)
        {
            var result = new SeriesResult
            {
                Id = series.GetClientId(),
                Name = series.Name,
                Year = series.ProductionYear,
                Status = series.Status?.ToString(),
                LibraryName = library.Name,
                ProviderIds = CopyIds(series.ProviderIds)
            };

            List<CachedEpisode> remote;
            CachedEpisodeList cacheEntry = null;
            try
            {
                var libraryOptions = _libraryManager.GetLibraryOptions(series);
                // Same selection as GetAllEpisodes: first enabled ISeriesMetadataProvider of the library.
                var provider = _providerManager.GetEnabledMetadataProviders(series, libraryOptions).OfType<ISeriesMetadataProvider>().FirstOrDefault();
                result.EpisodeProvider = EpisodeProviderKey(provider?.Name);
                var providerSeriesId = result.EpisodeProvider == null ? null : series.GetProviderId(result.EpisodeProvider);
                var language = LanguageKey(libraryOptions);

                // Ended series: reuse the saved episode list (see CachePolicy) instead of asking the provider again.
                if (_cache != null && _useCache && _cache.Series.TryGetValue(result.Id, out var cached)
                    && CachePolicy.CanReuseEpisodes(cached, result.Status, result.EpisodeProvider, providerSeriesId, language, result.Id, DateTime.UtcNow))
                {
                    remote = cached.Episodes;
                    Interlocked.Increment(ref _cache.SeriesReused);
                }
                else
                {
                    var fetched = await _providerManager.GetAllEpisodes(series, libraryOptions, token).ConfigureAwait(false);
                    remote = (fetched ?? new RemoteSearchResult[0]).Select(ToCached).ToList();
                    if (_cache != null)
                    {
                        Interlocked.Increment(ref _cache.SeriesFetched);
                        // Only lists of ended series are reused; others would only fill the file.
                        if (CachePolicy.IsEnded(result.Status) && remote.Count > 0 && !string.IsNullOrEmpty(providerSeriesId))
                        {
                            cacheEntry = new CachedEpisodeList
                            {
                                FetchedUtc = DateTime.UtcNow,
                                Provider = result.EpisodeProvider,
                                ProviderSeriesId = providerSeriesId,
                                Language = language,
                                Status = result.Status,
                                Episodes = remote
                            };
                        }
                        else
                        {
                            _cache.Series.TryRemove(result.Id, out _);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warn("[GapFinder] Episode list of '{0}' could not be loaded: {1}", series.Name, ex.Message);
                result.Error = ex.Message;
                return result;
            }

            if (remote.Count == 0)
            {
                result.NoProviderData = true;
                return result;
            }

            var present = GetLocalEpisodeKeys(series);

            // Providers return duplicates (e.g. the same number in several languages) - count each season/episode once.
            var seasons = new SortedDictionary<int, SeasonResult>();
            var seenKeys = new HashSet<long>();
            foreach (var episode in remote)
            {
                if (!episode.Number.HasValue)
                {
                    continue;
                }
                var seasonNumber = episode.Season ?? 0;
                var first = episode.Number.Value;
                var last = Math.Max(first, episode.NumberEnd ?? first);
                if (!seenKeys.Add(Key(seasonNumber, first)))
                {
                    continue;
                }

                if (!seasons.TryGetValue(seasonNumber, out var season))
                {
                    season = new SeasonResult { SeasonNumber = seasonNumber };
                    seasons[seasonNumber] = season;
                }

                var isPresent = true;
                for (var n = first; n <= last; n++)
                {
                    if (!present.Contains(Key(seasonNumber, n)))
                    {
                        isPresent = false;
                        break;
                    }
                }

                if (isPresent)
                {
                    season.PresentCount++;
                    if (cacheEntry != null)
                    {
                        // The overview is only shown for missing episodes - not kept for present ones.
                        episode.Overview = null;
                    }
                }
                else
                {
                    season.Missing.Add(new MissingEpisode
                    {
                        SeasonNumber = seasonNumber,
                        EpisodeNumber = first,
                        EpisodeNumberEnd = episode.NumberEnd,
                        Name = episode.Name,
                        PremiereDate = episode.PremiereDate,
                        Overview = episode.Overview,
                        ProviderIds = CopyIds(episode.ProviderIds)
                    });
                }
            }

            foreach (var season in seasons.Values)
            {
                season.Missing = season.Missing.OrderBy(e => e.EpisodeNumber).ToList();
            }
            result.Seasons = seasons.Values.ToList();
            MarkAssumedAired(result.Seasons, string.Equals(result.Status, "Ended", StringComparison.OrdinalIgnoreCase),
                remote.Where(e => e.Number.HasValue).Select(e => (e.Season ?? 0, e.Number.Value, e.PremiereDate)),
                CalendarDate.Today);

            if (cacheEntry != null)
            {
                _cache.Series[result.Id] = cacheEntry;
            }
            return result;
        }

        /// <summary>The provider answer in the form kept in the cache: dates as calendar days, overview shortened.</summary>
        private static CachedEpisode ToCached(RemoteSearchResult episode) => new CachedEpisode
        {
            Season = episode.ParentIndexNumber,
            Number = episode.IndexNumber,
            NumberEnd = episode.IndexNumberEnd,
            Name = episode.Name,
            PremiereDate = CalendarDate.Normalize(episode.PremiereDate),
            Overview = Truncate(episode.Overview, 400),
            ProviderIds = CopyIds(episode.ProviderIds)
        };

        /// <summary>Metadata language and country - a changed language fetches names again.</summary>
        internal static string LanguageKey(LibraryOptions options) =>
            (options?.PreferredMetadataLanguage ?? string.Empty) + "-" + (options?.MetadataCountryCode ?? string.Empty);

        /// <summary>
        /// Old series often lack air dates on the provider; Emby then never lists those episodes as missing. A missing
        /// episode without a date counts as aired if the series has ended or a later regular episode (by season and
        /// number) has already aired. Specials (season 0) only via "ended", since their order is not meaningful.
        /// </summary>
        public static void MarkAssumedAired(IEnumerable<SeasonResult> seasons, bool ended,
            IEnumerable<(int Season, int Episode, DateTimeOffset? Date)> providerEpisodes, DateTime today)
        {
            long? latestAired = null;
            foreach (var e in providerEpisodes)
            {
                // Dates are normalized calendar days (UTC midnight, see CalendarDate); today is server-local.
                if (e.Season > 0 && e.Date.HasValue && e.Date.Value.UtcDateTime.Date <= today.Date)
                {
                    var key = Key(e.Season, e.Episode);
                    if (!latestAired.HasValue || key > latestAired.Value)
                    {
                        latestAired = key;
                    }
                }
            }
            foreach (var season in seasons)
            {
                foreach (var episode in season.Missing.Where(m => !m.PremiereDate.HasValue))
                {
                    episode.AssumedAired = ended
                        || (episode.SeasonNumber > 0 && latestAired.HasValue && Key(episode.SeasonNumber, episode.EpisodeNumber) < latestAired.Value);
                }
            }
        }

        /// <summary>Season/episode of all local episodes, multi-episode files (E01-E02) expanded.</summary>
        private HashSet<long> GetLocalEpisodeKeys(Series series)
        {
            var keys = new HashSet<long>();
            var episodes = series.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Episode) },
                Recursive = true,
                IsVirtualItem = false
            });

            foreach (var episode in episodes.OfType<Episode>())
            {
                if (!episode.IndexNumber.HasValue)
                {
                    continue;
                }
                var seasonNumber = episode.ParentIndexNumber ?? episode.Season?.IndexNumber ?? 0;
                var first = episode.IndexNumber.Value;
                var last = Math.Max(first, episode.IndexNumberEnd ?? first);
                for (var n = first; n <= last; n++)
                {
                    keys.Add(Key(seasonNumber, n));
                }
            }
            return keys;
        }

        private static long Key(int season, int episode) => ((long)season << 32) | (uint)episode;

        internal static Dictionary<string, string> CopyIds(IEnumerable<KeyValuePair<string, string>> ids)
        {
            var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (ids != null)
            {
                foreach (var pair in ids)
                {
                    if (!string.IsNullOrEmpty(pair.Value))
                    {
                        copy[pair.Key] = pair.Value;
                    }
                }
            }
            return copy;
        }

        internal static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max)
            {
                return text;
            }
            return text.Substring(0, max).TrimEnd() + "...";
        }
    }
}
