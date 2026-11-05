using System.Collections.Generic;
using MediaBrowser.Model.Services;
using GapFinder.Data;
using GapFinder.Scanning;
// [Authenticated] lives in one of these two namespaces, depending on the Emby version.
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Authentication;

namespace GapFinder.Api
{
    // =====================================================================
    // Response DTOs
    // =====================================================================

    public class LibraryDto
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string CollectionType { get; set; }
        public bool CanContainSeries { get; set; }
        public bool CanContainMovies { get; set; }
    }

    public class SettingsDto
    {
        public bool ScanSeries { get; set; }
        public bool ScanMovies { get; set; }
        public string[] LibraryIds { get; set; }
        public int MaxParallelRequests { get; set; }
        public string CsvDelimiter { get; set; }
        public string Version { get; set; }
        /// <summary>Changes with every build - cache key for the page files loaded by the loader.</summary>
        public string BuildId { get; set; }
    }

    /// <summary>Connection to Sonarr or Radarr (Options tab).</summary>
    public class ArrServiceDto
    {
        public bool Enabled { get; set; }
        /// <summary>"Link" or "Api".</summary>
        public string Mode { get; set; }
        /// <summary>Base address, e.g. http://nas:7878.</summary>
        public string Url { get; set; }

        /// <summary>Save only: new API key. Empty = keep the saved key. Never returned.</summary>
        public string ApiKey { get; set; }
        /// <summary>Save only: remove the saved API key.</summary>
        public bool ClearApiKey { get; set; }
        /// <summary>Response only: a readable API key is saved.</summary>
        public bool HasApiKey { get; set; }
        /// <summary>Response only: a key is saved but cannot be decrypted (secret.key is missing).</summary>
        public bool ApiKeyUnreadable { get; set; }

        /// <summary>Sonarr: monitor the episodes and search right away. Radarr: search right after adding.</summary>
        public bool SearchNow { get; set; }
        /// <summary>Radarr only.</summary>
        public int QualityProfileId { get; set; }
        /// <summary>Radarr only: display name of the profile.</summary>
        public string QualityProfileName { get; set; }
        /// <summary>Radarr only.</summary>
        public string RootFolder { get; set; }
        /// <summary>Radarr only: "announced", "inCinemas" or "released".</summary>
        public string MinimumAvailability { get; set; }
    }

    public class OptionsDto
    {
        /// <summary>"All", "Errors" (including warnings) or "Nothing".</summary>
        public string LogLevel { get; set; }
        public ArrServiceDto Sonarr { get; set; }
        public ArrServiceDto Radarr { get; set; }
    }

    public class SeriesResultsDto
    {
        public SeriesResults Results { get; set; }
        public List<string> IgnoredKeys { get; set; }
    }

    public class MovieResultsDto
    {
        public MovieResults Results { get; set; }
        public List<string> IgnoredKeys { get; set; }
    }

    public class IgnoreResultDto
    {
        public List<string> IgnoredKeys { get; set; }
    }

    public class RemoveFromCollectionResultDto
    {
        public string MovieName { get; set; }
        /// <summary>RemoveRemovedCollection only: this movie could not be cleaned up.</summary>
        public string Error { get; set; }
        /// <summary>Emby collections that became empty and were deleted.</summary>
        public List<string> DeletedBoxSets { get; set; }
        /// <summary>Collection entry removed from the Emby database (ImportedCollections/Collections).</summary>
        public bool DatabaseUpdated { get; set; }
        /// <summary>Emby collections (BoxSets) the movie was removed from.</summary>
        public List<string> RemovedFromBoxSets { get; set; }
        /// <summary>NFO files found for the movie and what happened to them (empty = no NFO).</summary>
        public List<NfoCleaner.FileResult> NfoFiles { get; set; }
    }

    public class RemoveCollectionResultDto
    {
        public List<RemoveFromCollectionResultDto> Movies { get; set; }
        /// <summary>Emby collections that became empty and were deleted.</summary>
        public List<string> DeletedBoxSets { get; set; }
    }

    /// <summary>A collection of the search result to look up the Emby collection for.</summary>
    public class CollectionRefDto
    {
        public string TmdbId { get; set; }
        public string Name { get; set; }
    }

    /// <summary>Emby collection of a movie collection (link in the UI).</summary>
    public class EmbyCollectionDto
    {
        public string TmdbId { get; set; }
        /// <summary>Item id of the Emby collection (to open it in Emby).</summary>
        public string Id { get; set; }
        public string Name { get; set; }
        /// <summary>Number of matching collections (more than 1 = duplicates).</summary>
        public int MatchCount { get; set; }
    }

    public class ExportResultDto
    {
        public string FileName { get; set; }
        public string ContentType { get; set; }
        public string ContentBase64 { get; set; }
        public int RowCount { get; set; }
    }

    // =====================================================================
    // Request DTOs / routes
    // =====================================================================

    [Route("/GapFinder/Libraries", "GET", Summary = "Libraries that can contain series or movies")]
    [Authenticated(Roles = "Admin")]
    public class GetLibraries : IReturn<List<LibraryDto>>
    {
    }

    [Route("/GapFinder/Settings", "GET", Summary = "Last used search options")]
    [Authenticated(Roles = "Admin")]
    public class GetSettings : IReturn<SettingsDto>
    {
    }

    [Route("/GapFinder/Options", "GET", Summary = "Settings of the Options tab")]
    [Authenticated(Roles = "Admin")]
    public class GetOptions : IReturn<OptionsDto>
    {
    }

    [Route("/GapFinder/Options", "POST", Summary = "Save the settings of the Options tab")]
    [Authenticated(Roles = "Admin")]
    public class SaveOptions : OptionsDto, IReturn<OptionsDto>
    {
    }

    [Route("/GapFinder/Arr/Test", "POST", Summary = "Test the connection to Sonarr/Radarr (Radarr: load profiles and root folders)")]
    [Authenticated(Roles = "Admin")]
    public class TestArrConnection : IReturn<GapFinder.Arr.ArrTestResult>
    {
        /// <summary>"Sonarr" or "Radarr".</summary>
        public string Service { get; set; }
        public string Url { get; set; }
        /// <summary>Empty = use the saved key.</summary>
        public string ApiKey { get; set; }
    }

    [Route("/GapFinder/Radarr/Add", "POST", Summary = "Add missing movies to Radarr")]
    [Authenticated(Roles = "Admin")]
    public class AddToRadarr : IReturn<List<GapFinder.Arr.RadarrAddResult>>
    {
        public string[] TmdbIds { get; set; }
    }

    [Route("/GapFinder/Sonarr/Search", "POST", Summary = "Monitor and search missing episodes of a series in Sonarr")]
    [Authenticated(Roles = "Admin")]
    public class SearchInSonarr : IReturn<GapFinder.Arr.SonarrSearchResult>
    {
        public string TvdbId { get; set; }
        public string SeriesName { get; set; }
        public List<GapFinder.Arr.EpisodeRef> Episodes { get; set; }
    }

    [Route("/GapFinder/Scan", "POST", Summary = "Start a search (runs in the background)")]
    [Authenticated(Roles = "Admin")]
    public class StartScan : IReturn<ScanStatus>
    {
        public bool ScanSeries { get; set; }
        public bool ScanMovies { get; set; }
        public string[] LibraryIds { get; set; }
        public int? MaxParallelRequests { get; set; }
    }

    [Route("/GapFinder/Scan/Cancel", "POST", Summary = "Cancel the running search")]
    [Authenticated(Roles = "Admin")]
    public class CancelScan : IReturn<ScanStatus>
    {
    }

    [Route("/GapFinder/Status", "GET", Summary = "Search progress")]
    [Authenticated(Roles = "Admin")]
    public class GetStatus : IReturn<ScanStatus>
    {
    }

    [Route("/GapFinder/Results/Series", "GET", Summary = "Last result: missing episodes/seasons")]
    [Authenticated(Roles = "Admin")]
    public class GetSeriesResults : IReturn<SeriesResultsDto>
    {
    }

    [Route("/GapFinder/Results/Movies", "GET", Summary = "Last result: missing movies of movie collections")]
    [Authenticated(Roles = "Admin")]
    public class GetMovieResults : IReturn<MovieResultsDto>
    {
    }

    [Route("/GapFinder/Results/Clear", "POST", Summary = "Delete the saved search results (the ignore list is kept)")]
    [Authenticated(Roles = "Admin")]
    public class ClearResults : IReturnVoid
    {
    }

    [Route("/GapFinder/Collections/RemoveMovie", "POST", Summary = "Remove a movie from a movie collection (Emby collection, database and NFO)")]
    [Authenticated(Roles = "Admin")]
    public class RemoveMovieFromCollection : IReturn<RemoveFromCollectionResultDto>
    {
        public string MovieId { get; set; }
        public string CollectionTmdbId { get; set; }
        /// <summary>Collection name - matches old NFO entries without a TMDB id.</summary>
        public string CollectionName { get; set; }
    }

    [Route("/GapFinder/Collections/RemoveRemoved", "POST", Summary = "Remove a collection deleted on TMDB from all movies (NFO, database, Emby collection; deletes the empty collection)")]
    [Authenticated(Roles = "Admin")]
    public class RemoveRemovedCollection : IReturn<RemoveCollectionResultDto>
    {
        public string CollectionTmdbId { get; set; }
    }

    [Route("/GapFinder/Collections/EmbyCollections", "POST", Summary = "Look up the Emby collections of movie collections (read-only, for links)")]
    [Authenticated(Roles = "Admin")]
    public class LookupEmbyCollections : IReturn<List<EmbyCollectionDto>>
    {
        public List<CollectionRefDto> Collections { get; set; }
    }

    [Route("/GapFinder/Ignore", "POST", Summary = "Ignore entries or show them again")]
    [Authenticated(Roles = "Admin")]
    public class SetIgnored : IReturn<IgnoreResultDto>
    {
        public string[] Keys { get; set; }
        public bool Ignore { get; set; }
    }

    [Route("/GapFinder/Export", "GET", Summary = "Export the result as CSV or JSON")]
    [Authenticated(Roles = "Admin")]
    public class ExportResults : IReturn<ExportResultDto>
    {
        /// <summary>"series" or "movies".</summary>
        public string Type { get; set; }
        /// <summary>"csv" or "json".</summary>
        public string Format { get; set; }
        /// <summary>"semicolon" or "comma" (CSV only).</summary>
        public string Delimiter { get; set; }
        public bool IncludeSpecials { get; set; }
        public bool IncludeUnaired { get; set; }
        public bool IncludeIgnored { get; set; }
    }
}
