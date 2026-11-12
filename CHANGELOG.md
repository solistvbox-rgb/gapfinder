# Changelog

## 1.0.0.4

- Air and release dates were shown one day too early on servers east of UTC (e.g. Europe), and episodes counted as
  aired one day too early. Emby stores these dates as midnight in the server's time zone; Gap Finder now uses that
  calendar day everywhere (page, CSV export, Sonarr matching). Saved results are corrected without a new search.
- Far fewer requests to TheTVDB/TMDB. Emby's library scan does not ask the providers for existing items, but every
  Gap Finder search asked again for every series, movie and collection. Answers that rarely change are now kept in
  `provider-cache.json` and reused: episode lists of ended series (60-74 days, or until the series continues), the
  collection of each movie (30-44 days - TMDB's movie request includes cast, images and trailers, of which only the
  collection is needed) and the movies of each collection (14-28 days, 3 days while a movie is announced or
  undated, at once when a new movie of the collection is added). The checkbox "Ask TheTVDB/TMDB for everything
  again" ignores the saved answers for one search. The log shows how many requests were sent and reused.

## 1.0.0.3

- Confirmations and messages (e.g. "Clear search", "Remove collection", Sonarr/Radarr) use Emby's own dialogs instead
  of the browser's, which some browsers and embedded views block.
- Series cards: year and status next to the title, the episode count on its own line. "Library: <name>" is shown
  only when the listed series come from more than one library.
- Cards: numbers on top, actions below. The order is the same everywhere: metadata provider, Sonarr/Radarr, Ignore.
- Movie collections: a movie's collection is now taken from Emby's imported collection information and from
  TheMovieDb together. Before, libraries with "Import collection information" used only Emby's entry, so movies
  whose metadata had not been refreshed yet were not found in their collection. Searches in such libraries can take
  a little longer.

## 1.0.0.2

- Series: a "+N upcoming" button per series shows its upcoming episodes and episodes without an air date (only when
  it has any). They stay hidden until clicked; the numbers in the card still count aired episodes only.
- The date column now shows "upcoming <date>" (movies: "unreleased <date>") and "no date" in orange instead of a
  label after the title.
- The tagline "Find the missing pieces of your media library." is shown under the page title.
- The expand arrows of the cards use Emby's own icons.

## 1.0.0.1

- Missing episodes without an air date (common for old series on TheTVDB) are now shown as aired when the series
  has ended or a later episode has already aired. Before, they were only visible with "Upcoming / no air date" -
  the same gap as in Emby's own missing episodes list. Takes effect after a new search.

## 1.0.0.0

First release.

- Search for missing episodes/seasons of series and missing movies of movie collections
- Ignore list, filters, sorting, CSV/JSON export
- Hints for outdated collection information (collections removed from TMDB, wrong assignments) with
  ignore/remove actions
- Optional Sonarr/Radarr integration (link or API)
- Hint when a library has no suitable metadata downloader
- Supports Emby Server 4.10 and newer
