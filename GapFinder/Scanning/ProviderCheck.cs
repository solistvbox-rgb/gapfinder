namespace GapFinder.Scanning
{
    /// <summary>
    /// Hints when a library has no suitable metadata downloader. Without them the admin would only see empty
    /// results (no collections) or collections that look complete. The scanners collect the facts from Emby;
    /// this class only evaluates them.
    /// </summary>
    public static class ProviderCheck
    {
        /// <summary>Series: only TheTVDB/TheMovieDb provide episode lists (ISeriesMetadataProvider).</summary>
        public static string SeriesWarning(string libraryName, bool episodeProviderEnabled) =>
            episodeProviderEnabled
                ? null
                : "Library '" + libraryName + "': neither TheTVDB nor TheMovieDb is enabled as metadata downloader for series - missing episodes cannot be determined.";

        /// <summary>
        /// Movies: a movie's collection comes from Emby's imported collection information or, if the import is off,
        /// from the TheMovieDb downloader of the library.
        /// </summary>
        public static string MovieWarning(string libraryName, bool importCollections, bool movieDbEnabled) =>
            importCollections || movieDbEnabled
                ? null
                : "Library '" + libraryName + "': TheMovieDb is not enabled as metadata downloader for movies and collection information is not imported - movie collections cannot be found.";

        /// <summary>Movies of a collection: the Emby "Collections" library, or own options with TheMovieDb.</summary>
        public static string CollectionPartsWarning(bool fromCollectionsLibrary, string collectionsLibraryName, bool partsProviderAvailable)
        {
            if (partsProviderAvailable)
            {
                return null;
            }
            return fromCollectionsLibrary
                ? "Library '" + collectionsLibraryName + "': TheMovieDb is not enabled as metadata downloader for collections - the movies of a collection cannot be loaded."
                : "TheMovieDb is not available on this server - the movies of a collection cannot be loaded.";
        }
    }
}
