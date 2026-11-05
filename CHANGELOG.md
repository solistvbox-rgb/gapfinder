# Changelog

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
