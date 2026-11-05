using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using GapFinder.Data;

namespace GapFinder.Scanning
{
    /// <summary>
    /// Runs the manually started search in the background (the HTTP request returns at once, the page polls
    /// /Status for the progress). At most one search runs at a time.
    /// </summary>
    public static class ScanManager
    {
        private static readonly object Sync = new object();
        private static ScanStatus _status = new ScanStatus();
        private static CancellationTokenSource _cts;
        private static int _processed;
        private static int _itemErrors;

        public class ScanRequest
        {
            public bool ScanSeries { get; set; }
            public bool ScanMovies { get; set; }
            /// <summary>Selected libraries (empty = all suitable ones).</summary>
            public string[] LibraryIds { get; set; }
            public int MaxParallel { get; set; }
        }

        public static ScanStatus GetStatus()
        {
            lock (Sync)
            {
                return new ScanStatus
                {
                    IsRunning = _status.IsRunning,
                    Phase = _status.Phase,
                    Processed = Volatile.Read(ref _processed),
                    Total = _status.Total,
                    CurrentItem = _status.CurrentItem,
                    StartedUtc = _status.StartedUtc,
                    FinishedUtc = _status.FinishedUtc,
                    WasCancelled = _status.WasCancelled,
                    LastError = _status.LastError,
                    ItemErrors = Volatile.Read(ref _itemErrors),
                    ScanSeries = _status.ScanSeries,
                    ScanMovies = _status.ScanMovies
                };
            }
        }

        /// <returns>false if a search is already running.</returns>
        public static bool TryStart(ScanRequest request, ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem, ResultStore store, GapFinderLogger logger)
        {
            CancellationTokenSource cts;
            lock (Sync)
            {
                if (_status.IsRunning)
                {
                    return false;
                }

                cts = new CancellationTokenSource();
                _cts = cts;
                _processed = 0;
                _itemErrors = 0;
                _status = new ScanStatus
                {
                    IsRunning = true,
                    StartedUtc = DateTime.UtcNow,
                    ScanSeries = request.ScanSeries,
                    ScanMovies = request.ScanMovies
                };
            }

            Task.Run(() => RunAsync(request, libraryManager, providerManager, fileSystem, store, logger, cts.Token));
            return true;
        }

        public static void Cancel()
        {
            lock (Sync)
            {
                _cts?.Cancel();
            }
        }

        private static async Task RunAsync(ScanRequest request, ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem, ResultStore store, GapFinderLogger logger, CancellationToken token)
        {
            var libraries = LibraryHelper.ResolveLibraries(libraryManager, request.LibraryIds);
            try
            {
                logger.Info("[GapFinder] Search started (series: {0}, movie collections: {1}).", request.ScanSeries, request.ScanMovies);

                // Save both results only after everything has finished - a cancel leaves the previous results untouched.
                SeriesResults seriesResults = null;
                MovieResults movieResults = null;

                if (request.ScanSeries)
                {
                    var scanner = new SeriesScanner(libraryManager, providerManager, logger);
                    var seriesWarnings = scanner.CheckProviders(libraries);
                    var series = scanner.CollectSeries(libraries);
                    BeginPhase("Series", series.Count);
                    seriesResults = await scanner.ScanAsync(series, request.MaxParallel, ReportItem, token).ConfigureAwait(false);
                    seriesResults.LibraryNames = libraries.Where(l => LibraryHelper.CanContainSeries(l.CollectionType)).Select(l => l.Name).ToArray();
                    seriesResults.Warnings = seriesWarnings;
                    seriesWarnings.ForEach(w => logger.Warn("[GapFinder] {0}", w));
                }

                if (request.ScanMovies)
                {
                    var scanner = new MovieCollectionScanner(libraryManager, providerManager, fileSystem, logger);
                    var movieWarnings = scanner.CheckProviders(libraries);
                    var movies = scanner.CollectMovies(libraries);
                    BeginPhase("MovieInfo", movies.Count);
                    var collections = await scanner.CollectCollectionsAsync(movies, request.MaxParallel, ReportItem, token).ConfigureAwait(false);
                    BeginPhase("Movies", collections.Count);
                    movieResults = await scanner.ScanAsync(collections, request.MaxParallel, ReportItem, token).ConfigureAwait(false);
                    movieResults.LibraryNames = libraries.Where(l => LibraryHelper.CanContainMovies(l.CollectionType)).Select(l => l.Name).ToArray();
                    movieResults.Warnings = movieWarnings;
                    movieWarnings.ForEach(w => logger.Warn("[GapFinder] {0}", w));
                }

                if (seriesResults != null)
                {
                    store.SaveSeries(seriesResults);
                }
                if (movieResults != null)
                {
                    store.SaveMovies(movieResults);
                }

                logger.Info("[GapFinder] Search finished ({0} error(s) on single items).", Volatile.Read(ref _itemErrors));
                Finish(false, null);
            }
            catch (OperationCanceledException)
            {
                logger.Info("[GapFinder] Search cancelled.");
                Finish(true, null);
            }
            catch (Exception ex)
            {
                logger.ErrorException("[GapFinder] Search failed", ex);
                Finish(false, ex.Message);
            }
        }

        private static void BeginPhase(string phase, int total)
        {
            lock (Sync)
            {
                _status.Phase = phase;
                _status.Total = total;
                _status.CurrentItem = null;
                Volatile.Write(ref _processed, 0);
            }
        }

        private static void ReportItem(string name, bool failed)
        {
            Interlocked.Increment(ref _processed);
            if (failed)
            {
                Interlocked.Increment(ref _itemErrors);
            }
            lock (Sync)
            {
                _status.CurrentItem = name;
            }
        }

        private static void Finish(bool cancelled, string error)
        {
            lock (Sync)
            {
                _status.IsRunning = false;
                _status.FinishedUtc = DateTime.UtcNow;
                _status.WasCancelled = cancelled;
                _status.LastError = error;
                _status.CurrentItem = null;
                _cts?.Dispose();
                _cts = null;
            }
        }

        /// <summary>Runs body for all items, at most maxParallel at a time.</summary>
        internal static async Task ForEachParallelAsync<T>(IList<T> items, int maxParallel, CancellationToken token, Func<T, Task> body)
        {
            using (var semaphore = new SemaphoreSlim(Math.Max(1, maxParallel)))
            {
                var tasks = items.Select(async item =>
                {
                    await semaphore.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        await body(item).ConfigureAwait(false);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }).ToList();

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
        }
    }
}
