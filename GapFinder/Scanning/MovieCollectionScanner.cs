using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Net;
using GapFinder.Data;

namespace GapFinder.Scanning
{
    /// <summary>
    /// Missing movies of movie collections. Unlike Emby's /Collections/{id}/Missing no Emby collections are
    /// needed. A movie's TMDB collections are combined from two sources: ImportedCollections (only filled when the
    /// library has "Import collection information" enabled, and only after a metadata refresh) and the TMDB provider.
    /// Both TMDB answers (a movie's collection, a collection's movies) are kept in the ProviderCache. Movies are
    /// matched against ALL movies of the server by TMDB/IMDb id.
    /// </summary>
    public class MovieCollectionScanner
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;
        private readonly GapFinderLogger _logger;
        private readonly ProviderCache _cache;
        private readonly bool _useCache;

        private readonly HashSet<string> _allTmdbIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _allImdbIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Movie> _moviesByTmdbId = new Dictionary<string, Movie>(StringComparer.OrdinalIgnoreCase);

        /// <param name="cache">Saved provider answers (null = none); new answers are added to it.</param>
        /// <param name="useCache">false = ask TMDB for every movie and collection ("Ignore cache").</param>
        public MovieCollectionScanner(ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem, GapFinderLogger logger, ProviderCache cache = null, bool useCache = true)
        {
            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
            _logger = logger;
            _cache = cache;
            _useCache = useCache;
        }

        public class CollectionCandidate
        {
            public string TmdbId { get; set; }
            public string Name { get; set; }
            public LibraryOptions LibraryOptions { get; set; }
            public List<Movie> LocalMembers { get; } = new List<Movie>();
        }

        /// <summary>Movies of the selected libraries (without duplicates) and the stock of all movies for matching.</summary>
        public List<Movie> CollectMovies(List<LibraryInfo> libraries)
        {
            // Stock: all movies of the server, not just the selected libraries - a movie in another library is not missing.
            foreach (var movie in _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Movie) },
                Recursive = true,
                IsVirtualItem = false
            }).OfType<Movie>())
            {
                var tmdb = movie.GetProviderId(MetadataProviders.Tmdb);
                if (!string.IsNullOrEmpty(tmdb))
                {
                    _allTmdbIds.Add(tmdb);
                    if (!_moviesByTmdbId.ContainsKey(tmdb))
                    {
                        _moviesByTmdbId[tmdb] = movie;
                    }
                }
                var imdb = movie.GetProviderId(MetadataProviders.Imdb);
                if (!string.IsNullOrEmpty(imdb))
                {
                    _allImdbIds.Add(imdb);
                }
            }

            var seenMovies = new HashSet<long>();
            var result = new List<Movie>();
            foreach (var library in libraries.Where(l => LibraryHelper.CanContainMovies(l.CollectionType)))
            {
                foreach (var movie in _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { nameof(Movie) },
                    Parent = library.Folder,
                    Recursive = true,
                    IsVirtualItem = false
                }).OfType<Movie>())
                {
                    if (seenMovies.Add(movie.InternalId))
                    {
                        result.Add(movie);
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Hints when collections cannot be found (no import, no TheMovieDb in the movie library) or their movies cannot
        /// be loaded (no BoxSet provider). Uses the same selection as the actual search.
        /// </summary>
        public List<string> CheckProviders(List<LibraryInfo> libraries)
        {
            var warnings = new List<string>();
            LibraryOptions firstMovieOptions = null;
            foreach (var library in libraries.Where(l => LibraryHelper.CanContainMovies(l.CollectionType)))
            {
                // Like the search: with a real movie and its options (see SeriesScanner.CheckProviders).
                var movie = LibraryHelper.FirstItem(_libraryManager, library, nameof(Movie)) as Movie;
                if (movie == null)
                {
                    continue;
                }
                var options = _libraryManager.GetLibraryOptions(movie);
                firstMovieOptions = firstMovieOptions ?? options;
                var movieDb = FindMovieDbProvider(movie, options) != null;
                var warning = ProviderCheck.MovieWarning(library.Name, options != null && options.ImportCollections, movieDb);
                if (warning != null)
                {
                    warnings.Add(warning);
                }
            }
            if (firstMovieOptions != null)
            {
                var boxSetOptions = GetBoxSetLibraryOptions(firstMovieOptions);
                var parts = _providerManager.GetEnabledMetadataProviders(new BoxSet(), boxSetOptions).OfType<IBoxSetMetadataProvider>().Any();
                var warning = ProviderCheck.CollectionPartsWarning(_boxSetOptionsFromLibrary != null, _boxSetLibraryName, parts);
                if (warning != null)
                {
                    warnings.Add(warning);
                }
            }
            return warnings;
        }

        /// <summary>Determines the TMDB collection(s) of each movie and groups the movies by them.</summary>
        public async Task<List<CollectionCandidate>> CollectCollectionsAsync(List<Movie> movies, int maxParallel, Action<string, bool> report, CancellationToken token)
        {
            var candidates = new Dictionary<string, CollectionCandidate>(StringComparer.OrdinalIgnoreCase);
            var sync = new object();

            await ScanManager.ForEachParallelAsync(movies, maxParallel, token, async movie =>
            {
                var libraryOptions = _libraryManager.GetLibraryOptions(movie);
                var collections = new List<CollectionRef>();
                var error = false;

                // Both sources: Emby's imported entry is missing when the movie's metadata was never refreshed, and
                // the provider finds nothing without a TMDB/IMDb id. Duplicates are merged by TMDB collection id.
                if (libraryOptions != null && libraryOptions.ImportCollections)
                {
                    try
                    {
                        Merge(collections, GetImportedCollections(movie, token));
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn("[GapFinder] The imported collections of '{0}' could not be read: {1}", movie.Name, ex.Message);
                        error = true;
                    }
                }
                try
                {
                    Merge(collections, await GetCollectionsFromProviderAsync(movie, libraryOptions, token).ConfigureAwait(false));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Warn("[GapFinder] The collection of '{0}' could not be looked up at TheMovieDb: {1}", movie.Name, ex.Message);
                    error = true;
                }
                // Only a failure if no source delivered anything - one working source is enough.
                var failed = error && collections.Count == 0;

                lock (sync)
                {
                    foreach (var collection in collections)
                    {
                        if (!candidates.TryGetValue(collection.TmdbId, out var candidate))
                        {
                            candidate = new CollectionCandidate { TmdbId = collection.TmdbId, Name = collection.Name, LibraryOptions = libraryOptions };
                            candidates[collection.TmdbId] = candidate;
                        }
                        candidate.LocalMembers.Add(movie);
                    }
                }
                report(movie.Name, failed);
            }).ConfigureAwait(false);

            return candidates.Values.ToList();
        }

        private List<CollectionRef> GetImportedCollections(Movie movie, CancellationToken token)
        {
            var list = new List<CollectionRef>();
            var imported = _libraryManager.GetImportedCollections(movie.InternalId, token);
            if (imported != null)
            {
                foreach (var collection in imported)
                {
                    AddRef(list, collection.Name, collection.ProviderIds);
                }
            }
            return list;
        }

        /// <summary>
        /// Active TheMovieDb downloader for movies. Uses the base interface on purpose: Emby 4.10 does not offer the
        /// variant with options (IRemoteMetadataProviderWithOptions), so it would never be found there.
        /// </summary>
        private IRemoteMetadataProvider<Movie, MovieInfo> FindMovieDbProvider(Movie movie, LibraryOptions libraryOptions) =>
            _providerManager.GetEnabledMetadataProviders(movie, libraryOptions)
                .OfType<IRemoteMetadataProvider<Movie, MovieInfo>>()
                .FirstOrDefault(p => p.Name != null && p.Name.IndexOf("MovieDb", StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>
        /// Asks the TMDB provider for the movie's metadata; the result contains belongs_to_collection as a
        /// Collections entry. The provider reads its cached JSON file as long as it is not outdated.
        /// </summary>
        private async Task<List<CollectionRef>> GetCollectionsFromProviderAsync(Movie movie, LibraryOptions libraryOptions, CancellationToken token)
        {
            var list = new List<CollectionRef>();
            if (string.IsNullOrEmpty(movie.GetProviderId(MetadataProviders.Tmdb)) && string.IsNullOrEmpty(movie.GetProviderId(MetadataProviders.Imdb)))
            {
                return list;
            }

            var provider = FindMovieDbProvider(movie, libraryOptions);
            if (provider == null)
            {
                return list;
            }

            // The provider downloads the complete movie (cast, images, trailers, reviews) - only the collection is
            // needed, so it is kept in the cache and asked again only every 30-44 days (see CachePolicy).
            var cacheKey = ProviderCache.MovieKey(movie.GetProviderId(MetadataProviders.Tmdb), movie.GetProviderId(MetadataProviders.Imdb));
            var language = SeriesScanner.LanguageKey(libraryOptions);
            if (_cache != null && _useCache && _cache.Movies.TryGetValue(cacheKey, out var cached)
                && CachePolicy.CanReuseMovieCollections(cached, language, cacheKey, DateTime.UtcNow))
            {
                Interlocked.Increment(ref _cache.MoviesReused);
                return cached.Collections.Select(c => new CollectionRef { TmdbId = c.TmdbId, Name = c.Name }).ToList();
            }

            // Emby 4.11: MovieDb implements only the variant with options (GetMetadata(MovieInfo) throws
            // NotImplementedException). Emby 4.10: only GetMetadata(MovieInfo) exists.
            var result = provider is IRemoteMetadataProviderWithOptions<Movie, MovieInfo> withOptions
                ? await withOptions.GetMetadata(new RemoteMetadataFetchOptions<MovieInfo>
                {
                    SearchInfo = movie.GetLookupInfo(libraryOptions),
                    DirectoryService = new DirectoryService(_fileSystem)
                }, token).ConfigureAwait(false)
                : await provider.GetMetadata(movie.GetLookupInfo(libraryOptions), token).ConfigureAwait(false);
            if (_cache != null)
            {
                Interlocked.Increment(ref _cache.MoviesFetched);
            }
            if (result?.Item == null)
            {
                // Not found at TMDB (e.g. wrong id) - not cached, so a corrected id is found at the next search.
                return list;
            }
            foreach (var collection in result.Item.Collections)
            {
                AddRef(list, collection.Name, collection.ProviderIds);
            }
            if (_cache != null)
            {
                _cache.Movies[cacheKey] = new CachedMovieCollections
                {
                    FetchedUtc = DateTime.UtcNow,
                    Language = language,
                    Collections = list.Select(c => new CollectionRef { TmdbId = c.TmdbId, Name = c.Name }).ToList()
                };
            }
            return list;
        }

        private static void Merge(List<CollectionRef> target, List<CollectionRef> source)
        {
            foreach (var collection in source)
            {
                if (target.All(c => c.TmdbId != collection.TmdbId))
                {
                    target.Add(collection);
                }
            }
        }

        private static void AddRef(List<CollectionRef> list, string name, IEnumerable<KeyValuePair<string, string>> providerIds)
        {
            SeriesScanner.CopyIds(providerIds).TryGetValue(nameof(MetadataProviders.Tmdb), out var tmdbId);
            if (!string.IsNullOrEmpty(tmdbId) && list.All(c => c.TmdbId != tmdbId))
            {
                list.Add(new CollectionRef { TmdbId = tmdbId, Name = name });
            }
        }

        public async Task<MovieResults> ScanAsync(List<CollectionCandidate> candidates, int maxParallel, Action<string, bool> report, CancellationToken token)
        {
            var bag = new ConcurrentBag<CollectionResult>();

            await ScanManager.ForEachParallelAsync(candidates, maxParallel, token, async candidate =>
            {
                var result = await ScanCollectionAsync(candidate, token).ConfigureAwait(false);
                bag.Add(result);
                report(candidate.Name, result.Error != null);
            }).ConfigureAwait(false);

            return new MovieResults
            {
                ScanDateUtc = DateTime.UtcNow,
                Collections = bag.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList()
            };
        }

        private async Task<CollectionResult> ScanCollectionAsync(CollectionCandidate candidate, CancellationToken token)
        {
            var result = new CollectionResult { TmdbId = candidate.TmdbId, Name = candidate.Name };

            List<CachedPart> parts;
            var boxSetOptions = GetBoxSetLibraryOptions(candidate.LibraryOptions);
            var language = SeriesScanner.LanguageKey(boxSetOptions);
            var memberIds = candidate.LocalMembers
                .SelectMany(m => new[] { m.GetProviderId(MetadataProviders.Tmdb), m.GetProviderId(MetadataProviders.Imdb) })
                .Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (_cache != null && _useCache && _cache.Collections.TryGetValue(candidate.TmdbId, out var cached)
                && CachePolicy.CanReuseCollectionParts(cached, memberIds, language, candidate.TmdbId, DateTime.UtcNow, CalendarDate.Today))
            {
                Interlocked.Increment(ref _cache.CollectionsReused);
                if (cached.NotFound)
                {
                    return RemovedFromTmdb(result, candidate);
                }
                parts = cached.Parts;
            }
            else
            {
                try
                {
                    // Temporary, unsaved BoxSet that only carries the TMDB collection id.
                    var boxSet = new BoxSet { Name = candidate.Name };
                    boxSet.SetProviderId(MetadataProviders.Tmdb, candidate.TmdbId);
                    var fetched = await _providerManager.GetAllItems(boxSet, boxSetOptions, token).ConfigureAwait(false);
                    parts = (fetched ?? new MediaBrowser.Model.Providers.RemoteSearchResult[0]).Select(ToCachedPart).ToList();
                    CountFetchedCollection();
                    if (_cache != null && parts.Count > 0)
                    {
                        _cache.Collections[candidate.TmdbId] = new CachedCollectionParts { FetchedUtc = DateTime.UtcNow, Language = language, Parts = parts, LocalMemberIds = memberIds };
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (IsNotFound(ex))
                {
                    CountFetchedCollection();
                    if (_cache != null)
                    {
                        _cache.Collections[candidate.TmdbId] = new CachedCollectionParts { FetchedUtc = DateTime.UtcNow, Language = language, NotFound = true, LocalMemberIds = memberIds };
                    }
                    return RemovedFromTmdb(result, candidate);
                }
                catch (Exception ex)
                {
                    CountFetchedCollection();
                    _logger.Warn("[GapFinder] Collection '{0}' could not be loaded: {1}", candidate.Name, ex.Message);
                    result.Error = ex.Message;
                    parts = null;
                }
            }

            if (result.Error == null && (parts == null || parts.Count == 0))
            {
                // No provider (e.g. TheMovieDb not enabled for collections) or an empty collection: do not count it as
                // complete, show it under "could not be loaded" instead.
                _logger.Warn("[GapFinder] Collection '{0}' ({1}): the provider returned no movies.", candidate.Name, candidate.TmdbId);
                result.Error = "The metadata provider returned no movies for this collection.";
            }

            var partTmdbIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var partImdbIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var presentIds = new HashSet<long>();
            foreach (var part in parts ?? new List<CachedPart>())
            {
                var tmdb = part.TmdbId;
                var imdb = part.ImdbId;
                if (string.IsNullOrEmpty(tmdb) && string.IsNullOrEmpty(imdb))
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(tmdb))
                {
                    partTmdbIds.Add(tmdb);
                }
                if (!string.IsNullOrEmpty(imdb))
                {
                    partImdbIds.Add(imdb);
                }

                var isPresent = (!string.IsNullOrEmpty(tmdb) && _allTmdbIds.Contains(tmdb))
                    || (!string.IsNullOrEmpty(imdb) && _allImdbIds.Contains(imdb));
                if (isPresent)
                {
                    if (!string.IsNullOrEmpty(tmdb) && _moviesByTmdbId.TryGetValue(tmdb, out var local) && presentIds.Add(local.InternalId))
                    {
                        result.Present.Add(ToPresent(local));
                    }
                    continue;
                }

                result.Missing.Add(new MissingMovie
                {
                    TmdbId = tmdb,
                    Name = part.Name,
                    Year = part.Year,
                    PremiereDate = part.PremiereDate,
                    Overview = part.Overview,
                    ThumbnailUrl = part.ThumbnailUrl
                });
            }

            // Local movies whose metadata names the collection: they are only checked against TMDB's parts if TMDB
            // returned parts - otherwise (provider error/no parts) they count as present.
            var canVerify = partTmdbIds.Count > 0 || partImdbIds.Count > 0;
            foreach (var movie in candidate.LocalMembers)
            {
                if (presentIds.Contains(movie.InternalId))
                {
                    continue;
                }
                if (canVerify && !IsPartOf(movie, partTmdbIds, partImdbIds))
                {
                    result.WronglyAssigned.Add(ToPresent(movie));
                    continue;
                }
                presentIds.Add(movie.InternalId);
                result.Present.Add(ToPresent(movie));
            }

            if (result.WronglyAssigned.Count > 0)
            {
                _logger.Info("[GapFinder] Collection '{0}': {1} movie(s) name the collection but do not belong to it on TMDB: {2}",
                    candidate.Name, result.WronglyAssigned.Count, string.Join(", ", result.WronglyAssigned.Select(m => m.Name)));
            }

            result.Present = result.Present.OrderBy(m => m.Year ?? int.MaxValue).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
            result.Missing = result.Missing.OrderBy(m => m.PremiereDate ?? DateTimeOffset.MaxValue).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
            return result;
        }

        private LibraryOptions _boxSetOptionsFromLibrary;
        private string _boxSetLibraryName;
        private bool _boxSetOptionsResolved;

        /// <summary>
        /// GetAllItems(BoxSet) only asks providers that are enabled for the "BoxSet" type in the given library options,
        /// and movie libraries have no such entry. Therefore: use the options of the Emby "Collections" library
        /// (boxsets) if a provider is enabled there, otherwise own options with TheMovieDb and the metadata language
        /// of the movie library. The options cached by Emby are never changed.
        /// </summary>
        private LibraryOptions GetBoxSetLibraryOptions(LibraryOptions movieLibraryOptions)
        {
            lock (_moviesByTmdbId)
            {
                if (!_boxSetOptionsResolved)
                {
                    _boxSetOptionsResolved = true;
                    foreach (var vf in _libraryManager.GetVirtualFolders())
                    {
                        if (!string.Equals(vf.CollectionType, "boxsets", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(vf.ItemId))
                        {
                            continue;
                        }
                        var folder = _libraryManager.GetItemById(vf.ItemId);
                        var options = folder == null ? null : _libraryManager.GetLibraryOptions(folder);
                        var typeOptions = options?.GetTypeOptions(nameof(BoxSet));
                        if (typeOptions?.MetadataFetchers != null && typeOptions.MetadataFetchers.Length > 0)
                        {
                            _boxSetOptionsFromLibrary = options;
                            _boxSetLibraryName = vf.Name;
                        }
                        break;
                    }
                }
            }

            if (_boxSetOptionsFromLibrary != null)
            {
                return _boxSetOptionsFromLibrary;
            }

            return new LibraryOptions
            {
                PreferredMetadataLanguage = movieLibraryOptions?.PreferredMetadataLanguage,
                MetadataCountryCode = movieLibraryOptions?.MetadataCountryCode,
                TypeOptions = new[]
                {
                    new TypeOptions
                    {
                        Type = nameof(BoxSet),
                        MetadataFetchers = new[] { "TheMovieDb" },
                        MetadataFetcherOrder = new[] { "TheMovieDb" }
                    }
                }
            };
        }

        private static bool IsPartOf(Movie movie, HashSet<string> partTmdbIds, HashSet<string> partImdbIds)
        {
            var tmdb = movie.GetProviderId(MetadataProviders.Tmdb);
            var imdb = movie.GetProviderId(MetadataProviders.Imdb);
            return (!string.IsNullOrEmpty(tmdb) && partTmdbIds.Contains(tmdb))
                || (!string.IsNullOrEmpty(imdb) && partImdbIds.Contains(imdb));
        }

        /// <summary>HTTP 404 from TMDB (Emby's HttpClient throws HttpException with StatusCode NotFound).</summary>
        private static bool IsNotFound(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is HttpException http && http.StatusCode == HttpStatusCode.NotFound)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// TMDB deleted the collection (e.g. one with only a single released movie). The movies that still name it have
        /// outdated metadata (ImportedCollections/NFO) - not a search error.
        /// </summary>
        private CollectionResult RemovedFromTmdb(CollectionResult result, CollectionCandidate candidate)
        {
            _logger.Info("[GapFinder] Collection '{0}' ({1}) no longer exists on TMDB.", candidate.Name, candidate.TmdbId);
            result.RemovedFromTmdb = true;
            result.Present = candidate.LocalMembers.Select(ToPresent).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
            return result;
        }

        private void CountFetchedCollection()
        {
            if (_cache != null)
            {
                Interlocked.Increment(ref _cache.CollectionsFetched);
            }
        }

        private static CachedPart ToCachedPart(MediaBrowser.Model.Providers.RemoteSearchResult part) => new CachedPart
        {
            TmdbId = part.GetProviderId(MetadataProviders.Tmdb),
            ImdbId = part.GetProviderId(MetadataProviders.Imdb),
            Name = part.Name,
            Year = part.ProductionYear ?? (part.PremiereDate.HasValue ? CalendarDate.Of(part.PremiereDate.Value).Year : (int?)null),
            PremiereDate = CalendarDate.Normalize(part.PremiereDate),
            Overview = SeriesScanner.Truncate(part.Overview, 400),
            ThumbnailUrl = part.ThumbnailUrl
        };

        private static PresentMovie ToPresent(Movie movie) =>
            new PresentMovie { Id = movie.GetClientId(), Name = movie.Name, Year = movie.ProductionYear };
    }
}
