using System.Collections.Generic;

// Responses and request bodies of the Sonarr/Radarr API v3. The properties are named exactly like the
// JSON fields (camelCase), so reading and writing via Emby's IJsonSerializer maps them unambiguously.
#pragma warning disable IDE1006 // naming: camelCase on purpose
namespace GapFinder.Arr
{
    public class ArrSystemStatus
    {
        public string appName { get; set; }
        public string version { get; set; }
    }

    public class ArrQualityProfile
    {
        public int id { get; set; }
        public string name { get; set; }
    }

    public class ArrRootFolder
    {
        public int id { get; set; }
        public string path { get; set; }
        public long? freeSpace { get; set; }
    }

    public class RadarrMovie
    {
        public int id { get; set; }
        public string title { get; set; }
        public int year { get; set; }
        public int tmdbId { get; set; }
        public bool hasFile { get; set; }
        public bool monitored { get; set; }
    }

    public class RadarrAddOptions
    {
        public bool searchForMovie { get; set; }
    }

    public class RadarrAddMovie
    {
        public string title { get; set; }
        public int year { get; set; }
        public int tmdbId { get; set; }
        public int qualityProfileId { get; set; }
        public string rootFolderPath { get; set; }
        public bool monitored { get; set; }
        public string minimumAvailability { get; set; }
        public RadarrAddOptions addOptions { get; set; }
    }

    public class SonarrSeries
    {
        public int id { get; set; }
        public string title { get; set; }
        public int tvdbId { get; set; }
        public string titleSlug { get; set; }
        public bool monitored { get; set; }
    }

    /// <summary>
    /// Sonarr's series editor (PUT series/editor): changes only the fields sent - unlike PUT series/{id}, which
    /// replaces the whole series and could overwrite profile, path etc.
    /// </summary>
    public class SonarrSeriesEditor
    {
        public List<int> seriesIds { get; set; }
        public bool monitored { get; set; }
    }

    public class SonarrEpisode
    {
        public int id { get; set; }
        public int seasonNumber { get; set; }
        public int episodeNumber { get; set; }
        public string title { get; set; }
        /// <summary>Calendar date "yyyy-MM-dd" (local to the network).</summary>
        public string airDate { get; set; }
        public bool hasFile { get; set; }
        public bool monitored { get; set; }
    }

    public class SonarrMonitorEpisodes
    {
        public List<int> episodeIds { get; set; }
        public bool monitored { get; set; }
    }

    public class SonarrCommand
    {
        public string name { get; set; }
        public List<int> episodeIds { get; set; }
    }
}
#pragma warning restore IDE1006
