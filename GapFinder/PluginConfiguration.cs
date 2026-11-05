using MediaBrowser.Model.Plugins;

namespace GapFinder
{
    /// <summary>
    /// Plugin settings (stored by Emby in GapFinder.xml). The last search options are kept so the page can
    /// preselect them.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>Last choice: search for missing episodes/seasons.</summary>
        public bool ScanSeries { get; set; } = true;

        /// <summary>Last choice: search for missing movies of movie collections.</summary>
        public bool ScanMovies { get; set; } = true;

        /// <summary>Last selected libraries (empty = all suitable libraries).</summary>
        public string[] LibraryIds { get; set; } = new string[0];

        /// <summary>Concurrent provider requests during a search (1-8).</summary>
        public int MaxParallelRequests { get; set; } = 3;

        /// <summary>CSV delimiter of the export ("semicolon" or "comma").</summary>
        public string CsvDelimiter { get; set; } = "semicolon";

        // ---- Options tab (see Api/Dto.cs OptionsDto) ----

        /// <summary>What is written to the Emby log: "All", "Errors" (including warnings) or "Nothing".</summary>
        public string LogLevel { get; set; } = "All";

        public bool SonarrEnabled { get; set; }

        /// <summary>"Link" (opens the add search in Sonarr) or "Api" (direct connection).</summary>
        public string SonarrMode { get; set; } = "Link";

        /// <summary>Base address, e.g. http://nas:8989 (without trailing slash).</summary>
        public string SonarrUrl { get; set; } = string.Empty;

        public bool RadarrEnabled { get; set; }

        /// <summary>"Link" (opens the add search in Radarr) or "Api" (direct connection).</summary>
        public string RadarrMode { get; set; } = "Link";

        /// <summary>Base address, e.g. http://nas:7878 (without trailing slash).</summary>
        public string RadarrUrl { get; set; } = string.Empty;

        // ---- "Api" connection (API keys encrypted, see Data/SecretProtector) ----

        /// <summary>"emby-encrypted:v1:..." - never in plain text and never sent to the browser.</summary>
        public string SonarrApiKey { get; set; } = string.Empty;

        /// <summary>Set the episodes to monitored in Sonarr and start a search right away.</summary>
        public bool SonarrSearchNow { get; set; } = true;

        /// <summary>"emby-encrypted:v1:..." - never in plain text and never sent to the browser.</summary>
        public string RadarrApiKey { get; set; } = string.Empty;

        public int RadarrQualityProfileId { get; set; }

        /// <summary>Profile name, for display only (shown without a "Test connection").</summary>
        public string RadarrQualityProfileName { get; set; } = string.Empty;

        public string RadarrRootFolder { get; set; } = string.Empty;

        /// <summary>"announced", "inCinemas" or "released".</summary>
        public string RadarrMinimumAvailability { get; set; } = "released";

        /// <summary>Start a search right after adding.</summary>
        public bool RadarrSearchNow { get; set; } = true;
    }
}
