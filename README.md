<p align="center">
  <img src="assets/logo.jpg" alt="Gap Finder" width="560">
</p>

# Gap Finder

An [Emby Server](https://emby.media) plugin that finds the gaps in your library: **missing episodes and seasons**
of your series and **missing movies of movie collections** (TMDB collections).

Gap Finder is a dashboard page for administrators. You start a search, and the results appear in two tabs
(Series and Movie collections), with filters, an ignore list, CSV/JSON export and optional Sonarr/Radarr
integration.

## Requirements

- Emby Server **4.10** or newer (built against SDK 4.10.0.24)
- For series: TheTVDB or TheMovieDb enabled as metadata downloader for series in the library
- For movie collections: TheMovieDb enabled as metadata downloader for movies, or "Import collection
  information" enabled in the library

## Installation

1. Download `GapFinder.dll` from the [latest release](../../releases/latest). Optionally verify it with the
   SHA-256 checksum published with the release.
2. Copy `GapFinder.dll` into the `plugins` folder of your Emby Server (`<programdata>/plugins`).
3. Restart Emby Server.

The page appears in the dashboard under **Advanced → Gap Finder**.

## Features

### Search

- Search series, movie collections or both - all libraries or only selected ones
- Runs in the background with progress and a cancel button; 1-8 parallel provider requests
- Results are saved and are still there after a restart; "Clear search" deletes them (the ignore list is kept)
- If a library has no suitable metadata downloader, the tab shows a "Check your library settings" hint instead
  of silently empty or incomplete results

### Series

- Grouped by series → season → episode, with completeness in percent and completely missing seasons marked
  (100 % only when nothing is missing, 0 % only when nothing is present)
- Episodes without an air date (common for old series on TheTVDB) count as aired when the series has ended or a
  later episode has already aired - Emby's own missing episodes list hides them
- Filters for specials, upcoming episodes, continuing/ended series and completely missing seasons only;
  sorting by name, missing episodes, completeness or year
- The series name opens the series in Emby; a TVDB or TMDB link (depending on the provider that supplies the
  episode list in that library) opens it at the provider
- Uses the same data source as Emby's *Metadata Manager → Missing Episodes*: the first enabled series downloader
  of the library, matched by season and episode number

### Movie collections

- Missing movies with poster, release date and TMDB link; present movies as links to Emby
- No Emby collections are required. If one exists, the collection name opens it in Emby
- Movies are matched against **all** movies on the server (TMDB and IMDb id), not only the selected libraries
- Outdated collection information is listed separately instead of being counted as missing:
  - **Removed from TMDB** - the collection no longer exists on TMDB, but movies still name it
  - **Assigned to a collection they do not belong to** - e.g. after an earlier misidentification
- For these entries you can **Ignore** (hide, or accept the assignment) or **Remove**: the plugin removes the
  collection from the movie's NFO (with a backup), from the Emby collection and from the database, and deletes
  Emby collections that become empty

### Ignore list and export

- Ignore series, seasons, episodes, collections or single movies; takes effect immediately, "Show ignored"
  brings them back
- Export as **CSV** (UTF-8 with BOM, opens in Excel) or **JSON**, with the same filters as the page

### Sonarr and Radarr (optional)

Each service can be enabled separately in the **Options** tab, with one of two connection types:

- **Link** - "Find in Sonarr/Radarr" buttons open the add search of the service (`/add/new?term=tvdb:<id>` or
  `tmdb:<id>`). No API key needed.
- **API** (API key from *Settings → General* of the service):
  - **Radarr:** "Send to Radarr" per missing movie and "Send all to Radarr" per collection. Quality profile,
    root folder and minimum availability are chosen after "Test connection". Movies Radarr already has are
    reported, not added twice.
  - **Sonarr:** "Send to Sonarr" monitors the visible missing episodes of a series and optionally starts a
    search. It works for series that already exist in Sonarr; otherwise Sonarr's add search is offered. Since
    Sonarr numbers episodes like TheTVDB, episodes are matched by number only if date or title do not
    contradict, otherwise by a unique title or air date. Unclear episodes are listed, not sent.

### Logging

The **Log level** in the Options tab (*All / Errors / Nothing*) applies to all log entries of the plugin and
takes effect immediately.

## Data, privacy and uninstall

- Gap Finder only stores data in `<programdata>/data/GapFinder/` (results, ignore list, `secret.key`, NFO
  backups) and in its configuration `plugins/configurations/GapFinder.xml`.
- API keys are encrypted (AES-256 + HMAC) in `GapFinder.xml`; the random key is stored separately in
  `data/GapFinder/secret.key`. The page only shows "key saved" - the key is never sent back to the browser.
  This protects a shared or posted configuration file, not a server someone has full access to.
- The saved API key is only sent to the saved address. The plugin does not follow HTTP redirects (the key
  would go to the new host); it reports the target instead.
- Sonarr/Radarr over `http://` transmit the API key unencrypted - prefer the same host/Docker network or `https`.
- NFO backups are stored in `data/GapFinder/nfo-backups/<date>/` together with a `backup-index.txt` that lists
  the original path of each backup. To restore one, copy it back to that path.
- **Uninstalling removes all of this, including the NFO backups.** Copy `nfo-backups/` first if you want to
  keep them.

## Building from source

Requires the .NET SDK (8.0 or newer).

```bash
dotnet build GapFinder -c Release
```

The plugin is `GapFinder/bin/Release/netstandard2.0/GapFinder.dll`. With
`-p:EmbyPluginsPath=<programdata>\plugins` the build copies it straight into an Emby plugins folder.

The build is deterministic: the same sources produce a byte-identical DLL without local paths.

Run the tests:

```bash
dotnet test GapFinder.Tests
```

### Notes for contributors

- Emby serves plugin pages with `Cache-Control: public` and versions their URL only with the server version,
  so browsers keep them after a plugin update. `Web/gapfinder.html` and `Web/gapfinder.js` are therefore a
  **fixed loader that should not change**: it gets the build id from `/GapFinder/Settings` and loads
  `Web/gapfinder-ui.html` (UI) and `Web/gapfinder-app.js` (logic) with it. Page changes belong in those two files.
- The provider interfaces differ between Emby 4.10 and 4.11 (e.g. TheMovieDb's movie provider). Check changes
  against both versions.

## API

All routes require an administrator.

| Method | Route | Purpose |
|---|---|---|
| GET | `/GapFinder/Libraries` | Libraries that can contain series or movies |
| GET | `/GapFinder/Settings` | Last used search options |
| GET/POST | `/GapFinder/Options` | Options tab: log level, Sonarr/Radarr settings |
| POST | `/GapFinder/Arr/Test` | Test the connection to Sonarr/Radarr (Radarr: profiles, root folders) |
| POST | `/GapFinder/Radarr/Add` | Add `TmdbIds[]` to Radarr |
| POST | `/GapFinder/Sonarr/Search` | Monitor/search missing episodes of a series in Sonarr |
| POST | `/GapFinder/Scan` | Start a search (`ScanSeries`, `ScanMovies`, `LibraryIds`, `MaxParallelRequests`) |
| POST | `/GapFinder/Scan/Cancel` | Cancel the search |
| GET | `/GapFinder/Status` | Search progress |
| GET | `/GapFinder/Results/Series` | Last series result and ignore list |
| GET | `/GapFinder/Results/Movies` | Last movie collection result and ignore list |
| POST | `/GapFinder/Results/Clear` | Delete the saved results (the ignore list is kept) |
| POST | `/GapFinder/Collections/RemoveMovie` | Remove a movie from a collection (NFO, Emby collection, database) |
| POST | `/GapFinder/Collections/RemoveRemoved` | Remove a collection deleted on TMDB from all movies naming it |
| POST | `/GapFinder/Collections/EmbyCollections` | Look up the Emby collections of movie collections (read-only) |
| POST | `/GapFinder/Ignore` | Ignore `Keys[]` or show them again |
| GET | `/GapFinder/Export` | `Type=series\|movies`, `Format=csv\|json`, filter switches |

## License

[MIT](LICENSE) © SolisTVBox

## Built with AI assistance

Developed with the help of [Claude](https://claude.ai) (Anthropic).

## Support

If Gap Finder is useful to you, consider [buying me a coffee ☕](https://ko-fi.com/soliflix).

[![ko-fi](https://storage.ko-fi.com/cdn/kofi3.png?v=3)](https://ko-fi.com/soliflix)
