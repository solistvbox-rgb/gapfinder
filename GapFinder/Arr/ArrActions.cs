using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Serialization;
using GapFinder.Data;

namespace GapFinder.Arr
{
    public class ArrTestResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; }
        public string Version { get; set; }
        public List<ArrQualityProfile> QualityProfiles { get; set; } = new List<ArrQualityProfile>();
        public List<ArrRootFolder> RootFolders { get; set; } = new List<ArrRootFolder>();
    }

    public class RadarrAddResult
    {
        public string TmdbId { get; set; }
        public string Title { get; set; }
        /// <summary>"added", "exists" or "error".</summary>
        public string Status { get; set; }
        public string Message { get; set; }
    }

    /// <summary>A missing episode as sent by the page (only the currently visible ones).</summary>
    public class EpisodeRef
    {
        public int SeasonNumber { get; set; }
        public int EpisodeNumber { get; set; }
        public int? EpisodeNumberEnd { get; set; }
        public string Name { get; set; }
        public DateTimeOffset? PremiereDate { get; set; }
    }

    public class SonarrSearchResult
    {
        /// <summary>"done", "notInSonarr" or "error".</summary>
        public string Status { get; set; }
        public string Message { get; set; }
        /// <summary>For notInSonarr: add search in Sonarr (link).</summary>
        public string AddUrl { get; set; }
        public int Requested { get; set; }
        /// <summary>Found in Sonarr without a file - monitored (and searched if enabled).</summary>
        public int Monitored { get; set; }
        /// <summary>Found in Sonarr, but Sonarr already has the file (e.g. not yet scanned into Emby).</summary>
        public int AlreadyDownloaded { get; set; }
        /// <summary>Matched by date/title instead of number (different numbering).</summary>
        public int MatchedByDateOrTitle { get; set; }
        public bool SearchStarted { get; set; }
        /// <summary>The series was not monitored in Sonarr and was set to monitored - otherwise Sonarr does not
        /// download the monitored episodes automatically (RSS).</summary>
        public bool SeriesMonitored { get; set; }
        /// <summary>Not uniquely matched - deliberately not sent.</summary>
        public List<string> Unmatched { get; set; } = new List<string>();
    }

    /// <summary>
    /// Actions against Sonarr/Radarr ("Api" connection). Radarr: add movies. Sonarr: series that already exist
    /// there - monitor and search the missing episodes; otherwise a link to add the series.
    /// </summary>
    public class ArrActions
    {
        /// <summary>Maximum parts per multi-episode file (E01-E03 = 3).</summary>
        private const int MaxEpisodesPerEntry = 20;

        private readonly IJsonSerializer _json;
        private readonly GapFinderLogger _logger;

        public ArrActions(IJsonSerializer json, GapFinderLogger logger)
        {
            _json = json;
            _logger = logger;
        }

        public async Task<ArrTestResult> TestAsync(string service, string url, string apiKey, CancellationToken token)
        {
            var result = new ArrTestResult();
            try
            {
                var client = new ArrClient(service, url, apiKey, _json);
                var status = await client.GetAsync<ArrSystemStatus>("system/status", token).ConfigureAwait(false);
                if (status == null || string.IsNullOrEmpty(status.version))
                {
                    throw new ArrException(service + " returned no version - is the address correct?");
                }
                if (!string.IsNullOrEmpty(status.appName) && status.appName.IndexOf(service, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    throw new ArrException("The address points to " + status.appName + ", not " + service + ".");
                }
                result.Version = status.version;
                if (service == "Radarr")
                {
                    result.QualityProfiles = await client.GetAsync<List<ArrQualityProfile>>("qualityprofile", token).ConfigureAwait(false) ?? new List<ArrQualityProfile>();
                    result.RootFolders = await client.GetAsync<List<ArrRootFolder>>("rootfolder", token).ConfigureAwait(false) ?? new List<ArrRootFolder>();
                }
                result.Ok = true;
                result.Message = service + " " + status.version + " connected.";
            }
            catch (ArrException ex)
            {
                result.Message = ex.Message;
            }
            return result;
        }

        public async Task<List<RadarrAddResult>> AddMoviesToRadarrAsync(PluginConfiguration config, string apiKey, IEnumerable<string> tmdbIds, CancellationToken token)
        {
            var client = new ArrClient("Radarr", config.RadarrUrl, apiKey, _json);
            var results = new List<RadarrAddResult>();
            foreach (var id in tmdbIds.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct())
            {
                var result = new RadarrAddResult { TmdbId = id };
                try
                {
                    if (!int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdb))
                    {
                        throw new ArrException("Invalid TMDB id " + id + ".");
                    }

                    var existing = (await client.GetAsync<List<RadarrMovie>>("movie?tmdbId=" + tmdb, token).ConfigureAwait(false) ?? new List<RadarrMovie>())
                        .FirstOrDefault(m => m.tmdbId == tmdb);
                    if (existing != null)
                    {
                        result.Title = existing.title;
                        result.Status = "exists";
                        result.Message = existing.hasFile ? "already in Radarr (downloaded)" : (existing.monitored ? "already in Radarr (monitored)" : "already in Radarr (not monitored)");
                        results.Add(result);
                        continue;
                    }

                    var lookup = await client.GetAsync<RadarrMovie>("movie/lookup/tmdb?tmdbId=" + tmdb, token).ConfigureAwait(false);
                    if (lookup == null || string.IsNullOrEmpty(lookup.title))
                    {
                        throw new ArrException("Radarr could not find TMDB id " + tmdb + ".");
                    }
                    result.Title = lookup.title;

                    await client.PostAsync<RadarrMovie>("movie", new RadarrAddMovie
                    {
                        title = lookup.title,
                        year = lookup.year,
                        tmdbId = tmdb,
                        qualityProfileId = config.RadarrQualityProfileId,
                        rootFolderPath = config.RadarrRootFolder,
                        monitored = true,
                        minimumAvailability = string.IsNullOrEmpty(config.RadarrMinimumAvailability) ? "released" : config.RadarrMinimumAvailability,
                        addOptions = new RadarrAddOptions { searchForMovie = config.RadarrSearchNow }
                    }, token).ConfigureAwait(false);

                    result.Status = "added";
                    result.Message = config.RadarrSearchNow ? "added, search started" : "added";
                    _logger.Info("[GapFinder] Radarr: '{0}' (TMDB {1}) added.", lookup.title, tmdb);
                }
                catch (ArrException ex)
                {
                    result.Status = "error";
                    result.Message = ex.Message;
                    _logger.Warn("[GapFinder] Radarr: TMDB {0} could not be added: {1}", id, ex.Message);
                }
                results.Add(result);
            }
            return results;
        }

        public async Task<SonarrSearchResult> SearchEpisodesInSonarrAsync(PluginConfiguration config, string apiKey, string tvdbId, string seriesName, List<EpisodeRef> episodes, CancellationToken token)
        {
            var result = new SonarrSearchResult { Requested = episodes?.Count ?? 0 };
            try
            {
                var client = new ArrClient("Sonarr", config.SonarrUrl, apiKey, _json);
                var addTerm = !string.IsNullOrEmpty(tvdbId) ? "tvdb:" + tvdbId : seriesName;
                result.AddUrl = config.SonarrUrl.TrimEnd('/') + "/add/new?term=" + Uri.EscapeDataString(addTerm ?? string.Empty);

                if (!int.TryParse(tvdbId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tvdb))
                {
                    result.Status = "notInSonarr";
                    result.Message = "The series has no TheTVDB id - Sonarr can only be matched by TheTVDB id.";
                    return result;
                }

                var series = (await client.GetAsync<List<SonarrSeries>>("series?tvdbId=" + tvdb, token).ConfigureAwait(false) ?? new List<SonarrSeries>())
                    .FirstOrDefault(s => s.tvdbId == tvdb);
                if (series == null)
                {
                    result.Status = "notInSonarr";
                    result.Message = "The series is not in Sonarr yet.";
                    return result;
                }

                var sonarrEpisodes = await client.GetAsync<List<SonarrEpisode>>("episode?seriesId=" + series.id, token).ConfigureAwait(false) ?? new List<SonarrEpisode>();
                var toMonitor = new List<int>();
                foreach (var episode in episodes ?? new List<EpisodeRef>())
                {
                    var last = Math.Max(episode.EpisodeNumber, episode.EpisodeNumberEnd ?? episode.EpisodeNumber);
                    if (last - (long)episode.EpisodeNumber >= MaxEpisodesPerEntry)
                    {
                        // Implausible range (only from a hand-crafted request): first number only. Without a limit "n++" would
                        // overflow at int.MaxValue and the loop would never end.
                        last = episode.EpisodeNumber;
                    }
                    for (var n = episode.EpisodeNumber; n <= last; n++)
                    {
                        var match = Match(sonarrEpisodes, episode, n, out var byDateOrTitle);
                        if (match == null)
                        {
                            result.Unmatched.Add(string.Format(CultureInfo.InvariantCulture, "S{0:00}E{1:00} {2}", episode.SeasonNumber, n, episode.Name));
                            continue;
                        }
                        if (byDateOrTitle)
                        {
                            result.MatchedByDateOrTitle++;
                        }
                        if (match.hasFile)
                        {
                            result.AlreadyDownloaded++;
                        }
                        else if (!toMonitor.Contains(match.id))
                        {
                            toMonitor.Add(match.id);
                        }
                    }
                }

                if (toMonitor.Count > 0)
                {
                    // Sonarr only downloads episodes of monitored series via RSS - so monitor the series as well.
                    if (!series.monitored)
                    {
                        await client.PutAsync<string>("series/editor", new SonarrSeriesEditor { seriesIds = new List<int> { series.id }, monitored = true }, token).ConfigureAwait(false);
                        result.SeriesMonitored = true;
                    }
                    await client.PutAsync<string>("episode/monitor", new SonarrMonitorEpisodes { episodeIds = toMonitor, monitored = true }, token).ConfigureAwait(false);
                    result.Monitored = toMonitor.Count;
                    if (config.SonarrSearchNow)
                    {
                        await client.PostAsync<string>("command", new SonarrCommand { name = "EpisodeSearch", episodeIds = toMonitor }, token).ConfigureAwait(false);
                        result.SearchStarted = true;
                    }
                }

                result.Status = "done";
                _logger.Info("[GapFinder] Sonarr '{0}': {1} episode(s) monitored{2}, {3} already present, {4} not matched.",
                    series.title, result.Monitored, result.SearchStarted ? " and searched" : string.Empty, result.AlreadyDownloaded, result.Unmatched.Count);
            }
            catch (ArrException ex)
            {
                result.Status = "error";
                result.Message = ex.Message;
                _logger.Warn("[GapFinder] Sonarr '{0}': {1}", seriesName, ex.Message);
            }
            return result;
        }

        /// <summary>
        /// Sonarr numbers episodes like TheTVDB; the Emby library may differ (TMDB, DVD, absolute). Therefore:
        /// 1. same season/number - but only if date or title do not contradict;
        /// 2. otherwise a unique episode with the same title, or the same date (+/- 1 day) in the same season.
        /// Not unique => null (not sent).
        /// </summary>
        private static SonarrEpisode Match(List<SonarrEpisode> sonarr, EpisodeRef episode, int number, out bool byDateOrTitle)
        {
            byDateOrTitle = false;
            var date = episode.PremiereDate.HasValue ? CalendarDate.Of(episode.PremiereDate.Value) : (DateTime?)null;
            var title = NormalizeTitle(episode.Name);

            var sameNumber = sonarr.FirstOrDefault(e => e.seasonNumber == episode.SeasonNumber && e.episodeNumber == number);
            if (sameNumber != null && !Contradicts(sameNumber, date, title))
            {
                return sameNumber;
            }

            // For multi-episode files (E01-E02) only the first number is matched by date/title.
            if (number != episode.EpisodeNumber)
            {
                return null;
            }

            // Date alone only within the same season - otherwise a random date pair could match across seasons
            // (e.g. when the right episode is missing in Sonarr). Titles match across seasons.
            var candidates = sonarr.Where(e => (title.Length >= 4 && NormalizeTitle(e.title) == title)
                || (date.HasValue && e.seasonNumber == episode.SeasonNumber && SameDay(e, date.Value, 1))).ToList();
            if (candidates.Count == 1)
            {
                byDateOrTitle = true;
                return candidates[0];
            }
            return null;
        }

        private static bool Contradicts(SonarrEpisode e, DateTime? date, string title)
        {
            var dateKnown = date.HasValue && ParseAirDate(e).HasValue;
            var dateDiffers = dateKnown && !SameDay(e, date.Value, 2);
            var titleKnown = title.Length >= 4 && NormalizeTitle(e.title).Length >= 4;
            var titleDiffers = titleKnown && NormalizeTitle(e.title) != title;
            // A contradiction only if the date clearly differs and the title does not match (titles alone often differ by language).
            return dateDiffers && (titleDiffers || !titleKnown);
        }

        private static bool SameDay(SonarrEpisode e, DateTime date, int toleranceDays)
        {
            var air = ParseAirDate(e);
            return air.HasValue && Math.Abs((air.Value - date).TotalDays) <= toleranceDays;
        }

        private static DateTime? ParseAirDate(SonarrEpisode e)
        {
            if (DateTime.TryParseExact(e.airDate ?? string.Empty, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            {
                return d.Date;
            }
            return null;
        }

        private static string NormalizeTitle(string title)
        {
            if (string.IsNullOrEmpty(title))
            {
                return string.Empty;
            }
            var sb = new StringBuilder();
            foreach (var c in title.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }
    }
}
