# Changelog

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
