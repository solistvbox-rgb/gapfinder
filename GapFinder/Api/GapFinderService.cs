using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;
using GapFinder.Arr;
using GapFinder.Data;
using GapFinder.Scanning;

namespace GapFinder.Api
{
    /// <summary>
    /// All Gap Finder API routes. Access control: [Authenticated(Roles = "Admin")] on every request DTO (Dto.cs).
    /// </summary>
    public class GapFinderService : IService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;
        private readonly ICollectionManager _collectionManager;
        private readonly ILibraryMonitor _libraryMonitor;
        private readonly IJsonSerializer _json;
        private readonly GapFinderLogger _logger;
        private readonly ResultStore _store;

        public GapFinderService(ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem, ICollectionManager collectionManager, ILibraryMonitor libraryMonitor, IJsonSerializer json, ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
            _collectionManager = collectionManager;
            _libraryMonitor = libraryMonitor;
            _json = json;
            _logger = new GapFinderLogger(logManager);
            _store = new ResultStore(json, _logger);
        }

        public object Get(GetLibraries request)
        {
            return LibraryHelper.GetLibraries(_libraryManager)
                .Select(l => new LibraryDto
                {
                    Id = l.Id,
                    Name = l.Name,
                    CollectionType = l.CollectionType,
                    CanContainSeries = LibraryHelper.CanContainSeries(l.CollectionType),
                    CanContainMovies = LibraryHelper.CanContainMovies(l.CollectionType)
                })
                .ToList();
        }

        public object Get(GetSettings request)
        {
            var config = Plugin.Instance.Configuration;
            return new SettingsDto
            {
                ScanSeries = config.ScanSeries,
                ScanMovies = config.ScanMovies,
                LibraryIds = config.LibraryIds ?? new string[0],
                MaxParallelRequests = ClampParallel(config.MaxParallelRequests),
                CsvDelimiter = config.CsvDelimiter,
                Version = Plugin.Instance.DisplayVersion,
                BuildId = Plugin.Instance.BuildId
            };
        }

        public object Get(GetOptions request)
        {
            return ToOptionsDto(Plugin.Instance.Configuration);
        }

        public object Post(SaveOptions request)
        {
            EnsureNotUninstalling();

            var config = Plugin.Instance.Configuration;
            var sonarr = NormalizeService(request.Sonarr, "Sonarr");
            var radarr = NormalizeService(request.Radarr, "Radarr");

            // API keys: a new key is stored encrypted, empty keeps the old key, ClearApiKey removes it.
            var sonarrKey = NewProtectedKey(request.Sonarr, config.SonarrApiKey);
            var radarrKey = NewProtectedKey(request.Radarr, config.RadarrApiKey);
            ValidateApiMode("Sonarr", sonarr, sonarrKey);
            ValidateApiMode("Radarr", radarr, radarrKey);

            config.LogLevel = LogLevels.Normalize(request.LogLevel);
            config.SonarrEnabled = sonarr.Enabled;
            config.SonarrMode = sonarr.Mode;
            config.SonarrUrl = sonarr.Url;
            config.SonarrApiKey = sonarrKey;
            config.SonarrSearchNow = sonarr.SearchNow;
            config.RadarrEnabled = radarr.Enabled;
            config.RadarrMode = radarr.Mode;
            config.RadarrUrl = radarr.Url;
            config.RadarrApiKey = radarrKey;
            config.RadarrSearchNow = radarr.SearchNow;
            config.RadarrQualityProfileId = radarr.QualityProfileId;
            config.RadarrQualityProfileName = radarr.QualityProfileName ?? string.Empty;
            config.RadarrRootFolder = radarr.RootFolder ?? string.Empty;
            config.RadarrMinimumAvailability = radarr.MinimumAvailability;
            Plugin.Instance.SaveConfiguration();

            _logger.Info("[GapFinder] Options saved (logging: {0}, Sonarr: {1}, Radarr: {2}).",
                config.LogLevel, sonarr.Enabled ? sonarr.Mode : "off", radarr.Enabled ? radarr.Mode : "off");
            return ToOptionsDto(config);
        }

        private static OptionsDto ToOptionsDto(PluginConfiguration config)
        {
            return new OptionsDto
            {
                LogLevel = LogLevels.Normalize(config.LogLevel),
                Sonarr = new ArrServiceDto
                {
                    Enabled = config.SonarrEnabled,
                    Mode = NormalizeMode(config.SonarrMode),
                    Url = config.SonarrUrl ?? string.Empty,
                    HasApiKey = SecretProtector.Unprotect(config.SonarrApiKey) != null,
                    ApiKeyUnreadable = SecretProtector.IsProtected(config.SonarrApiKey) && SecretProtector.Unprotect(config.SonarrApiKey) == null,
                    SearchNow = config.SonarrSearchNow
                },
                Radarr = new ArrServiceDto
                {
                    Enabled = config.RadarrEnabled,
                    Mode = NormalizeMode(config.RadarrMode),
                    Url = config.RadarrUrl ?? string.Empty,
                    HasApiKey = SecretProtector.Unprotect(config.RadarrApiKey) != null,
                    ApiKeyUnreadable = SecretProtector.IsProtected(config.RadarrApiKey) && SecretProtector.Unprotect(config.RadarrApiKey) == null,
                    SearchNow = config.RadarrSearchNow,
                    QualityProfileId = config.RadarrQualityProfileId,
                    QualityProfileName = config.RadarrQualityProfileName ?? string.Empty,
                    RootFolder = config.RadarrRootFolder ?? string.Empty,
                    MinimumAvailability = NormalizeAvailability(config.RadarrMinimumAvailability)
                }
            };
        }

        private static string NormalizeMode(string mode) =>
            string.Equals(mode, "Api", StringComparison.OrdinalIgnoreCase) ? "Api" : "Link";

        private static string NormalizeAvailability(string value)
        {
            foreach (var v in new[] { "announced", "inCinemas", "released" })
            {
                if (string.Equals(value, v, StringComparison.OrdinalIgnoreCase))
                {
                    return v;
                }
            }
            return "released";
        }

        /// <summary>
        /// Validates the address: required when the service is enabled, http/https only, no trailing slash. No user
        /// info, query or fragment: the plugin appends "/api/v3/...", so a "?" or "#" would move the API path into the
        /// query/fragment, and credentials in the address would not be used but would show up in error messages.
        /// </summary>
        private static ArrServiceDto NormalizeService(ArrServiceDto service, string name)
        {
            var url = (service?.Url ?? string.Empty).Trim().TrimEnd('/');
            var enabled = service != null && service.Enabled;
            if (url.Length > 0)
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    throw new ArgumentException(name + ": please enter a full address starting with http:// or https://, e.g. http://nas:" + (name == "Sonarr" ? "8989" : "7878"));
                }
                if (uri.UserInfo.Length > 0 || url.IndexOf('?') >= 0 || url.IndexOf('#') >= 0)
                {
                    throw new ArgumentException(name + ": please enter only the address without user name, password, \"?\" or \"#\", e.g. http://nas:" + (name == "Sonarr" ? "8989" : "7878"));
                }
            }
            else if (enabled)
            {
                throw new ArgumentException(name + ": an address is required when the link is enabled.");
            }
            return new ArrServiceDto
            {
                Enabled = enabled,
                Mode = NormalizeMode(service?.Mode),
                Url = url,
                SearchNow = service != null && service.SearchNow,
                QualityProfileId = service?.QualityProfileId ?? 0,
                QualityProfileName = (service?.QualityProfileName ?? string.Empty).Trim(),
                RootFolder = (service?.RootFolder ?? string.Empty).Trim(),
                MinimumAvailability = NormalizeAvailability(service?.MinimumAvailability)
            };
        }

        private static string NewProtectedKey(ArrServiceDto service, string current)
        {
            if (service != null && service.ClearApiKey)
            {
                return string.Empty;
            }
            var key = (service?.ApiKey ?? string.Empty).Trim();
            return key.Length > 0 ? SecretProtector.Protect(key) : (current ?? string.Empty);
        }

        private static void ValidateApiMode(string name, ArrServiceDto service, string protectedKey)
        {
            if (!service.Enabled || service.Mode != "Api")
            {
                return;
            }
            if (SecretProtector.Unprotect(protectedKey) == null)
            {
                throw new ArgumentException(name + ": an API key is required for the API connection (" + name + " > Settings > General > API Key).");
            }
            if (name == "Radarr" && (service.QualityProfileId <= 0 || string.IsNullOrEmpty(service.RootFolder)))
            {
                throw new ArgumentException("Radarr: please click \"Test connection\" and choose a quality profile and a root folder.");
            }
        }

        // =================================================================
        // Sonarr/Radarr ("Api" connection)
        // =================================================================

        public object Post(TestArrConnection request)
        {
            var name = string.Equals(request.Service, "Sonarr", StringComparison.OrdinalIgnoreCase) ? "Sonarr" : "Radarr";
            var config = Plugin.Instance.Configuration;
            var savedUrl = name == "Sonarr" ? config.SonarrUrl : config.RadarrUrl;
            var url = NormalizeService(new ArrServiceDto { Url = request.Url }, name).Url;
            if (string.IsNullOrEmpty(url))
            {
                url = savedUrl;
            }
            var key = (request.ApiKey ?? string.Empty).Trim();
            if (key.Length == 0)
            {
                // The saved key is only sent to the saved address - otherwise a request with another address could
                // obtain the encrypted key in plain text.
                if (!SameAddress(url, savedUrl))
                {
                    return new ArrTestResult { Message = name + ": the address differs from the saved one - please enter the API key again to test the new address." };
                }
                key = SecretProtector.Unprotect(name == "Sonarr" ? config.SonarrApiKey : config.RadarrApiKey);
            }
            return new ArrActions(_json, _logger).TestAsync(name, url, key, CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>Same address despite different spelling (case, default port, trailing slash).</summary>
        private static bool SameAddress(string a, string b)
        {
            if (!Uri.TryCreate((a ?? string.Empty).TrimEnd('/') + "/", UriKind.Absolute, out var x) ||
                !Uri.TryCreate((b ?? string.Empty).TrimEnd('/') + "/", UriKind.Absolute, out var y))
            {
                return false;
            }
            return Uri.Compare(x, y, UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;
        }

        public object Post(AddToRadarr request)
        {
            EnsureNotUninstalling();
            var config = Plugin.Instance.Configuration;
            var key = RequireApi("Radarr", config.RadarrEnabled, config.RadarrMode, config.RadarrApiKey);
            return new ArrActions(_json, _logger).AddMoviesToRadarrAsync(config, key, request.TmdbIds ?? new string[0], CancellationToken.None).GetAwaiter().GetResult();
        }

        public object Post(SearchInSonarr request)
        {
            EnsureNotUninstalling();
            var config = Plugin.Instance.Configuration;
            var key = RequireApi("Sonarr", config.SonarrEnabled, config.SonarrMode, config.SonarrApiKey);
            return new ArrActions(_json, _logger).SearchEpisodesInSonarrAsync(config, key, request.TvdbId, request.SeriesName, request.Episodes, CancellationToken.None).GetAwaiter().GetResult();
        }

        private static string RequireApi(string name, bool enabled, string mode, string protectedKey)
        {
            if (!enabled || NormalizeMode(mode) != "Api")
            {
                throw new ArgumentException(name + " API connection is not enabled (Options tab).");
            }
            var key = SecretProtector.Unprotect(protectedKey);
            if (key == null)
            {
                throw new ArgumentException(name + ": the saved API key cannot be read - please enter it again in the Options tab.");
            }
            return key;
        }

        public object Post(StartScan request)
        {
            EnsureNotUninstalling();
            if (!request.ScanSeries && !request.ScanMovies)
            {
                throw new ArgumentException("Select series, movie collections or both.");
            }

            var maxParallel = ClampParallel(request.MaxParallelRequests ?? Plugin.Instance.Configuration.MaxParallelRequests);

            // Remember the choice so the page can preselect it next time.
            var config = Plugin.Instance.Configuration;
            config.ScanSeries = request.ScanSeries;
            config.ScanMovies = request.ScanMovies;
            config.LibraryIds = request.LibraryIds ?? new string[0];
            config.MaxParallelRequests = maxParallel;
            Plugin.Instance.SaveConfiguration();

            var started = ScanManager.TryStart(new ScanManager.ScanRequest
            {
                ScanSeries = request.ScanSeries,
                ScanMovies = request.ScanMovies,
                LibraryIds = request.LibraryIds,
                MaxParallel = maxParallel
            }, _libraryManager, _providerManager, _fileSystem, _store, _logger);

            if (!started)
            {
                throw new ArgumentException("A search is already running.");
            }
            return ScanManager.GetStatus();
        }

        public object Post(CancelScan request)
        {
            ScanManager.Cancel();
            return ScanManager.GetStatus();
        }

        public object Get(GetStatus request)
        {
            return ScanManager.GetStatus();
        }

        public object Get(GetSeriesResults request)
        {
            return new SeriesResultsDto { Results = _store.LoadSeries(), IgnoredKeys = _store.LoadIgnored().ToList() };
        }

        public object Get(GetMovieResults request)
        {
            return new MovieResultsDto { Results = _store.LoadMovies(), IgnoredKeys = _store.LoadIgnored().ToList() };
        }

        public void Post(ClearResults request)
        {
            EnsureNotUninstalling();
            // Otherwise the running search would write the results again right away.
            if (ScanManager.GetStatus().IsRunning)
            {
                throw new ArgumentException("A search is running. Cancel it first.");
            }
            _store.ClearResults();
            _logger.Info("[GapFinder] Search results deleted.");
        }

        /// <summary>
        /// Removes a movie from a collection it does not belong to on TMDB: 1. the &lt;set&gt; from the NFO (with
        /// backup), 2. from matching Emby collections via Emby's own RemoveFromCollection (which also removes the
        /// ImportedCollections entry), 3. remaining collection entries in the database. The NFO comes first: if the
        /// library saves NFOs, Emby would otherwise rewrite it with the old &lt;set&gt; in step 2/3.
        /// </summary>
        public object Post(RemoveMovieFromCollection request)
        {
            EnsureNotUninstalling();
            var tmdbId = RequireCollectionTmdbId(request.CollectionTmdbId);
            var movie = string.IsNullOrEmpty(request.MovieId) ? null : _libraryManager.GetItemById(request.MovieId) as Movie;
            if (movie == null)
            {
                throw new ArgumentException("Movie not found.");
            }

            var touchedBoxSets = new Dictionary<long, BoxSet>();
            var result = RemoveFromCollectionCore(movie, tmdbId, request.CollectionName, touchedBoxSets);
            result.DeletedBoxSets = DeleteEmptyBoxSets(touchedBoxSets.Values);

            _store.RemoveWrongAssignment(request.MovieId, tmdbId);
            _store.SetIgnored(new[] { IgnoreKeys.Assignment(request.MovieId, tmdbId) }, false);
            return result;
        }

        /// <summary>
        /// Cleans up a collection that was deleted on TMDB: every movie that still names it according to the last
        /// search is cleaned up like "Remove from collection" (NFO, Emby collection, database). Emby collections of it
        /// that are empty afterwards are deleted. The movies come from the saved result, not from the browser.
        /// </summary>
        public object Post(RemoveRemovedCollection request)
        {
            EnsureNotUninstalling();
            var tmdbId = RequireCollectionTmdbId(request.CollectionTmdbId);
            var collection = (_store.LoadMovies().Collections ?? new List<CollectionResult>())
                .FirstOrDefault(c => c.RemovedFromTmdb && string.Equals(c.TmdbId, tmdbId, StringComparison.OrdinalIgnoreCase));
            if (collection == null)
            {
                throw new ArgumentException("This collection is not in the list of collections removed from TMDB (start a new search).");
            }

            var result = new RemoveCollectionResultDto { Movies = new List<RemoveFromCollectionResultDto>() };
            var touchedBoxSets = new Dictionary<long, BoxSet>();
            var done = new List<string>();
            foreach (var present in collection.Present ?? new List<PresentMovie>())
            {
                var movie = string.IsNullOrEmpty(present.Id) ? null : _libraryManager.GetItemById(present.Id) as Movie;
                if (movie == null)
                {
                    // No longer in the library - nothing to clean up.
                    result.Movies.Add(new RemoveFromCollectionResultDto { MovieName = present.Name, Error = "movie is no longer in the library" });
                    done.Add(present.Id);
                    continue;
                }
                try
                {
                    result.Movies.Add(RemoveFromCollectionCore(movie, tmdbId, collection.Name, touchedBoxSets));
                    done.Add(present.Id);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("[GapFinder] '{0}' could not be removed from collection '{1}'.", ex, movie.Name, collection.Name);
                    result.Movies.Add(new RemoveFromCollectionResultDto { MovieName = movie.Name, Error = ex.Message });
                }
            }

            result.DeletedBoxSets = DeleteEmptyBoxSets(touchedBoxSets.Values);
            _store.RemoveCollectionMovies(tmdbId, done);
            return result;
        }

        private static string RequireCollectionTmdbId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("CollectionTmdbId is required.");
            }
            return value.Trim();
        }

        /// <summary>
        /// The three steps for one movie (see Post(RemoveMovieFromCollection)). The Emby collections touched are
        /// collected in touchedBoxSets so the caller can delete the empty ones afterwards.
        /// </summary>
        private RemoveFromCollectionResultDto RemoveFromCollectionCore(Movie movie, string tmdbId, string name, Dictionary<long, BoxSet> touchedBoxSets)
        {
            var result = new RemoveFromCollectionResultDto { MovieName = movie.Name, RemovedFromBoxSets = new List<string>() };

            result.NfoFiles = new NfoCleaner(_libraryMonitor, _logger).RemoveCollection(movie, tmdbId, name);

            foreach (var link in movie.Collections.Where(c => c.Id != 0).ToArray())
            {
                var boxSet = _libraryManager.GetItemById(link.Id) as BoxSet;
                if (boxSet != null && MatchesCollection(boxSet.ProviderIds, boxSet.Name, tmdbId, name))
                {
                    _collectionManager.RemoveFromCollection(boxSet, new[] { movie.InternalId });
                    result.RemovedFromBoxSets.Add(boxSet.Name);
                    result.DatabaseUpdated = true;
                    touchedBoxSets[boxSet.InternalId] = boxSet;
                }
            }

            // Reload: RemoveFromCollection has already changed and saved the movie.
            movie = _libraryManager.GetItemById(movie.InternalId) as Movie ?? movie;
            var imported = _libraryManager.GetImportedCollections(movie.InternalId, CancellationToken.None) ?? new LinkedItemInfo[0];
            var keptImported = imported.Where(c => !MatchesCollection(c.ProviderIds, c.Name, tmdbId, name)).ToArray();
            var collections = movie.Collections;
            // Links to Emby collections (Id != 0) were handled above via RemoveFromCollection.
            var keptCollections = collections.Where(c => c.Id != 0 || !MatchesCollection(c.ProviderIds, c.Name, tmdbId, name)).ToArray();
            if (keptImported.Length != imported.Length || keptCollections.Length != collections.Length)
            {
                movie.ImportedCollections = keptImported;
                movie.Collections = keptCollections;
                movie.UpdateToRepository(ItemUpdateType.MetadataEdit);
                result.DatabaseUpdated = true;
            }

            _logger.Info("[GapFinder] '{0}' removed from collection '{1}' ({2}): database {3}, Emby collections [{4}], NFO [{5}].",
                movie.Name, name, tmdbId, result.DatabaseUpdated ? "changed" : "no entry",
                string.Join(", ", result.RemovedFromBoxSets), string.Join(", ", result.NfoFiles.Select(f => f.Status + " " + f.Path)));
            return result;
        }

        /// <summary>
        /// Deletes Emby collections that movies were just removed from and that are now empty. Collections with
        /// other content stay. The collection folder is deleted too, otherwise the next library scan recreates it.
        /// </summary>
        private List<string> DeleteEmptyBoxSets(IEnumerable<BoxSet> boxSets)
        {
            var deleted = new List<string>();
            foreach (var boxSet in boxSets)
            {
                var remaining = _libraryManager.GetItemList(new InternalItemsQuery { CollectionIds = new[] { boxSet.InternalId } });
                if (remaining != null && remaining.Length > 0)
                {
                    _logger.Info("[GapFinder] Emby collection '{0}' kept: it still contains {1} item(s).", boxSet.Name, remaining.Length);
                    continue;
                }
                try
                {
                    _libraryManager.DeleteItem(boxSet, new DeleteOptions { DeleteFileLocation = true });
                    deleted.Add(boxSet.Name);
                    _logger.Info("[GapFinder] Empty Emby collection '{0}' deleted.", boxSet.Name);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("[GapFinder] Empty Emby collection '{0}' could not be deleted.", ex, boxSet.Name);
                }
            }
            return deleted;
        }

        /// <summary>
        /// Change nothing once an uninstall has started (the plugin stays loaded until Emby restarts); otherwise the
        /// data folder, configuration or NFO backups would be recreated.
        /// </summary>
        private static void EnsureNotUninstalling()
        {
            if (Plugin.IsUninstalling)
            {
                throw new ArgumentException("Gap Finder is being uninstalled. Restart Emby Server to finish.");
            }
        }

        private static bool MatchesCollection(IEnumerable<KeyValuePair<string, string>> providerIds, string itemName, string tmdbId, string name)
        {
            SeriesScanner.CopyIds(providerIds).TryGetValue(nameof(MetadataProviders.Tmdb), out var itemTmdbId);
            return CollectionLinker.Matches(itemTmdbId, itemName, tmdbId, name);
        }

        /// <summary>
        /// Emby collections of the movie collections on the page. Looked up on every display, so links stay correct
        /// after "Remove collection" or a collection deleted in Emby. Read-only.
        /// </summary>
        public object Post(LookupEmbyCollections request)
        {
            var boxSets = _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { nameof(BoxSet) }, Recursive = true })
                .OfType<BoxSet>()
                .Select(b =>
                {
                    SeriesScanner.CopyIds(b.ProviderIds).TryGetValue(nameof(MetadataProviders.Tmdb), out var tmdb);
                    return new CollectionLinker.EmbyCollection { Id = b.GetClientId(), Name = b.Name, TmdbId = tmdb };
                })
                .ToList();
            return CollectionLinker.Match(request.Collections, boxSets);
        }

        public object Post(SetIgnored request)
        {
            EnsureNotUninstalling();
            var keys = (request.Keys ?? new string[0]).Where(IgnoreKeys.IsValid).ToList();
            if (keys.Count == 0)
            {
                throw new ArgumentException("No valid keys.");
            }
            return new IgnoreResultDto { IgnoredKeys = _store.SetIgnored(keys, request.Ignore).ToList() };
        }

        public object Get(ExportResults request)
        {
            var isMovies = string.Equals(request.Type, "movies", StringComparison.OrdinalIgnoreCase);
            var isJson = string.Equals(request.Format, "json", StringComparison.OrdinalIgnoreCase);
            var delimiter = CsvUtil.DelimiterFromName(request.Delimiter ?? Plugin.Instance.Configuration.CsvDelimiter);
            var ignored = _store.LoadIgnored();
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);

            byte[] content;
            int rowCount;
            string baseName;

            if (isMovies)
            {
                baseName = "missing-movies-" + stamp;
                var collections = FilterMovies(_store.LoadMovies(), ignored, request.IncludeUnaired, request.IncludeIgnored);
                rowCount = collections.Sum(c => c.Missing.Count);
                content = isJson
                    ? Encoding.UTF8.GetBytes(_json.SerializeToString(collections))
                    : CsvUtil.BuildCsvFile(delimiter,
                        new[] { "Collection", "CollectionTmdbId", "InLibrary", "Movie", "Year", "ReleaseDate", "Released", "TmdbId" },
                        collections.SelectMany(c => c.Missing.Select(m => new[]
                        {
                            c.Name,
                            c.TmdbId,
                            c.Present.Count.ToString(CultureInfo.InvariantCulture),
                            m.Name,
                            m.Year?.ToString(CultureInfo.InvariantCulture),
                            FormatDate(m.PremiereDate),
                            ResultFilter.IsReleased(m.PremiereDate) ? "yes" : "no",
                            m.TmdbId
                        })));
            }
            else
            {
                baseName = "missing-episodes-" + stamp;
                var series = FilterSeries(_store.LoadSeries(), ignored, request.IncludeSpecials, request.IncludeUnaired, request.IncludeIgnored);
                rowCount = series.Sum(s => s.Seasons.Sum(x => x.Missing.Count));
                content = isJson
                    ? Encoding.UTF8.GetBytes(_json.SerializeToString(series))
                    : CsvUtil.BuildCsvFile(delimiter,
                        new[] { "Series", "Year", "Status", "Library", "Season", "Episode", "EpisodeEnd", "Code", "Title", "AirDate", "Aired", "SeasonCompletelyMissing", "SeriesTvdbId", "SeriesTmdbId", "SeriesImdbId" },
                        series.SelectMany(s => s.Seasons.SelectMany(season => season.Missing.Select(e => new[]
                        {
                            s.Name,
                            s.Year?.ToString(CultureInfo.InvariantCulture),
                            s.Status,
                            s.LibraryName,
                            e.SeasonNumber.ToString(CultureInfo.InvariantCulture),
                            e.EpisodeNumber.ToString(CultureInfo.InvariantCulture),
                            e.EpisodeNumberEnd?.ToString(CultureInfo.InvariantCulture),
                            FormatCode(e),
                            e.Name,
                            FormatDate(e.PremiereDate),
                            ResultFilter.IsAired(e) ? "yes" : "no",
                            season.PresentCount == 0 ? "yes" : "no",
                            GetId(s.ProviderIds, "Tvdb"),
                            GetId(s.ProviderIds, "Tmdb"),
                            GetId(s.ProviderIds, "Imdb")
                        }))));
            }

            return new ExportResultDto
            {
                FileName = baseName + (isJson ? ".json" : ".csv"),
                ContentType = isJson ? "application/json" : "text/csv",
                ContentBase64 = Convert.ToBase64String(content),
                RowCount = rowCount
            };
        }

        /// <summary>Copy of the series with only the visible missing episodes; series without any are dropped.</summary>
        private static List<SeriesResult> FilterSeries(SeriesResults results, HashSet<string> ignored, bool includeSpecials, bool includeUnaired, bool includeIgnored)
        {
            var list = new List<SeriesResult>();
            foreach (var s in results.Series ?? new List<SeriesResult>())
            {
                var seasons = s.Seasons
                    .Select(season => new SeasonResult
                    {
                        SeasonNumber = season.SeasonNumber,
                        PresentCount = season.PresentCount,
                        Missing = season.Missing.Where(e => ResultFilter.IsEpisodeVisible(s, e, ignored, includeSpecials, includeUnaired, includeIgnored)).ToList()
                    })
                    .Where(season => season.Missing.Count > 0)
                    .ToList();

                if (seasons.Count > 0)
                {
                    list.Add(new SeriesResult
                    {
                        Id = s.Id,
                        Name = s.Name,
                        Year = s.Year,
                        Status = s.Status,
                        LibraryName = s.LibraryName,
                        ProviderIds = s.ProviderIds,
                        Seasons = seasons
                    });
                }
            }
            return list;
        }

        private static List<CollectionResult> FilterMovies(MovieResults results, HashSet<string> ignored, bool includeUnreleased, bool includeIgnored)
        {
            // Collections without a movie that really belongs to them on TMDB (or was accepted as assignment) are
            // hidden on the page too - see buildMoviesView in gapfinder-app.js.
            return (results.Collections ?? new List<CollectionResult>())
                .Where(c => !c.RemovedFromTmdb)
                .Select(c => new { Collection = c, Present = ResultFilter.PresentWithAccepted(c, ignored) })
                .Where(x => x.Present.Count > 0)
                .Select(x => x.Collection)
                .Select(c => new CollectionResult
                {
                    TmdbId = c.TmdbId,
                    Name = c.Name,
                    Present = ResultFilter.PresentWithAccepted(c, ignored),
                    Missing = c.Missing.Where(m => ResultFilter.IsMovieVisible(c, m, ignored, includeUnreleased, includeIgnored)).ToList()
                })
                .Where(c => c.Missing.Count > 0)
                .ToList();
        }

        private static string FormatCode(MissingEpisode e)
        {
            var code = string.Format(CultureInfo.InvariantCulture, "S{0:00}E{1:00}", e.SeasonNumber, e.EpisodeNumber);
            if (e.EpisodeNumberEnd.HasValue && e.EpisodeNumberEnd.Value > e.EpisodeNumber)
            {
                code += string.Format(CultureInfo.InvariantCulture, "-E{0:00}", e.EpisodeNumberEnd.Value);
            }
            return code;
        }

        private static string FormatDate(DateTimeOffset? date) =>
            date?.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private static string GetId(Dictionary<string, string> ids, string key) =>
            ids != null && ids.TryGetValue(key, out var value) ? value : null;

        private static int ClampParallel(int value) => Math.Max(1, Math.Min(8, value));
    }
}
