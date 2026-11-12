define([], function () {
    'use strict';

    // Logic of the Gap Finder page. Loaded by the loader (gapfinder.js) with the build id in the URL after it
    // has inserted the UI (gapfinder-ui.html). Provides onResume/onPause, called by the loader from the
    // BaseView life cycle.
    return function GapFinderApp(view) {
        var page = view;

        // Cards per "Show more" step - avoids huge DOMs with thousands of series.
        var PAGE_SIZE = 100;

        // =================================================================
        // State
        // =================================================================
        var state = {
            libraries: [],
            mode: 'both',
            selectedLibraryIds: new Set(),
            tab: 'series',
            status: null,
            pollTimer: null,
            // showUpcoming: series whose upcoming/undated episodes are shown via "+N upcoming" (this page view only).
            series: { results: null, ignored: new Set(), open: new Set(), showUpcoming: new Set(), limit: PAGE_SIZE },
            // openDetails: expanded hint lists at the bottom of the collections tab ("removed"/"wrong") stay open after re-rendering.
            // embyCollections: TMDB id of the collection -> Emby collection ({ Id, Name, MatchCount }) for the links.
            movies: { results: null, ignored: new Set(), open: new Set(), openDetails: new Set(), limit: PAGE_SIZE, embyCollections: {} },
            // Cards rendered last (for "Expand all").
            visibleSeriesIds: [],
            visibleCollectionIds: [],
            // Emby's "Date/time locale" setting (resolved, also for "Auto"); null = browser default.
            dateTimeLocale: null,
            // Options tab (GET /GapFinder/Options); null until loaded.
            options: null,
            // Radarr: profiles/root folders from "Test connection"; result per TMDB id after "Add to Radarr".
            arrMeta: { Radarr: null },
            radarrStatus: {}
        };

        // =================================================================
        // Helpers
        // =================================================================
        function $(sel) { return page.querySelector(sel); }

        function esc(s) {
            if (s === null || s === undefined) { return ''; }
            return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
                .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
        }

        function pad2(n) { return n < 10 ? '0' + n : String(n); }

        function episodeCode(e) {
            var code = 'S' + pad2(e.SeasonNumber) + 'E' + pad2(e.EpisodeNumber);
            if (e.EpisodeNumberEnd && e.EpisodeNumberEnd > e.EpisodeNumber) { code += '-E' + pad2(e.EpisodeNumberEnd); }
            return code;
        }

        function formatDate(value) {
            if (!value) { return ''; }
            var d = new Date(value);
            if (isNaN(d.getTime())) { return ''; }
            // The server sends calendar days at UTC midnight (CalendarDate.Normalize) - show them in UTC, otherwise
            // the day shifts back by one west of UTC.
            return d.toLocaleDateString(state.dateTimeLocale || undefined, { timeZone: 'UTC', year: 'numeric', month: '2-digit', day: '2-digit' });
        }

        function formatDateTime(value) {
            if (!value) { return ''; }
            var d = new Date(value);
            return isNaN(d.getTime()) ? '' : d.toLocaleString(state.dateTimeLocale || undefined, { dateStyle: 'medium', timeStyle: 'short' });
        }

        function isValidLocale(value) {
            if (!value || typeof value !== 'string') { return false; }
            try { new Intl.DateTimeFormat(value); return true; } catch (e) { return false; }
        }

        /// Gets Emby's "Date/time locale" via globalize.getCurrentDateTimeLocale() (also resolves "Auto");
        /// fallback: the value saved in localStorage. Re-renders when it changes.
        function loadDateTimeLocale() {
            function apply(value) {
                var locale = isValidLocale(value) ? value : null;
                if (locale === state.dateTimeLocale) { return; }
                state.dateTimeLocale = locale;
                renderSeries();
                renderMovies();
                if (state.status) { applyStatus(state.status); }
            }
            function fromStorage() {
                try { return localStorage.getItem('datetimelocale'); } catch (e) { return null; }
            }
            try {
                Emby.importModule('./modules/common/globalize.js').then(function (m) {
                    var globalize = m.default || m;
                    apply(globalize.getCurrentDateTimeLocale ? globalize.getCurrentDateTimeLocale() : fromStorage());
                }).catch(function () { apply(fromStorage()); });
            } catch (e) {
                apply(fromStorage());
            }
        }

        // Same rule as ResultFilter.IsReleased: the calendar day is today or earlier. The server sends dates as
        // calendar days at UTC midnight (CalendarDate.Normalize); "today" is the browser's local day.
        function isReleased(value) {
            if (!value) { return false; }
            var d = new Date(value);
            if (isNaN(d.getTime())) { return false; }
            var now = new Date();
            var today = now.getFullYear() + '-' + String(now.getMonth() + 1).padStart(2, '0') + '-' + String(now.getDate()).padStart(2, '0');
            return d.toISOString().slice(0, 10) <= today;
        }

        // Rounded normally, but 100 % only when nothing is missing (399 of 401 would be 100 % otherwise),
        // and 0 % only when nothing is present (4 of 401 would be 0 % otherwise).
        function pct(present, missing) {
            var total = present + missing;
            if (total <= 0) { return 100; }
            var p = Math.round(present * 100 / total);
            if (missing > 0 && p > 99) { p = 99; }
            if (present > 0 && p < 1) { p = 1; }
            return p;
        }

        function progressClass(p) { return p >= 90 ? 'ok' : (p < 50 ? 'warn' : ''); }

        function matches(text, query) {
            return !!text && text.toLowerCase().indexOf(query) >= 0;
        }

        function compareNames(a, b) {
            return (a || '').localeCompare(b || '', undefined, { sensitivity: 'base', numeric: true });
        }

        var keys = {
            series: function (id) { return 'series:' + id; },
            season: function (id, s) { return 'season:' + id + ':' + s; },
            episode: function (id, s, e) { return 'episode:' + id + ':' + s + ':' + e; },
            collection: function (id) { return 'collection:' + id; },
            movie: function (id) { return 'movie:' + id; },
            assignment: function (movieId, collectionTmdbId) { return 'assignment:' + movieId + ':' + collectionTmdbId; }
        };

        function showItem(itemId) {
            try {
                Emby.importModule('./modules/approuter.js').then(function (m) {
                    (m.default || m).showItem(itemId, ApiClient.serverId());
                });
            } catch (e) { /* router not available - the link does nothing */ }
        }

        function imageUrl(itemId, maxHeight) {
            return ApiClient.getImageUrl(itemId, { type: 'Primary', maxHeight: maxHeight, quality: 90 });
        }

        // =================================================================
        // API wrapper
        // =================================================================
        function apiGet(path, params) {
            return ApiClient.getJSON(ApiClient.getUrl(path, params));
        }

        function apiPost(path, body) {
            return ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl(path),
                data: JSON.stringify(body || {}),
                contentType: 'application/json',
                dataType: 'json'
            });
        }

        // Error text from the server response (ArgumentException -> HTTP 400 with text).
        function errorText(err) {
            if (err && typeof err.text === 'function') {
                return err.text().then(function (t) { return t || ('HTTP ' + err.status); }).catch(function () { return 'HTTP ' + err.status; });
            }
            return Promise.resolve((err && err.message) || String(err || 'Unknown error'));
        }

        function setScanMessage(html, isError) {
            var el = $('#gfScanStatus');
            el.innerHTML = html;
            el.classList.toggle('gf-error', !!isError);
            // Truncated text (ellipsis) - full text on hover.
            el.title = el.textContent;
        }

        // =================================================================
        // Search (options, start, progress)
        // =================================================================
        function librariesForMode() {
            return state.libraries.filter(function (l) {
                if (state.mode === 'series') { return l.CanContainSeries; }
                if (state.mode === 'movies') { return l.CanContainMovies; }
                return true;
            });
        }

        function libraryTypeLabel(l) {
            if (l.CollectionType === 'tvshows') { return 'TV'; }
            if (l.CollectionType === 'movies') { return 'Movies'; }
            return 'Mixed';
        }

        function renderSearchOptions() {
            page.querySelectorAll('#gfModeSeg button').forEach(function (b) {
                b.classList.toggle('active', b.getAttribute('data-mode') === state.mode);
            });

            var libs = librariesForMode();
            var visibleIds = new Set(libs.map(function (l) { return l.Id; }));
            var anySelected = libs.some(function (l) { return state.selectedLibraryIds.has(l.Id); });

            var html = '<span class="gf-chip' + (anySelected ? '' : ' active') + '" data-lib="">All</span>';
            libs.forEach(function (l) {
                html += '<span class="gf-chip' + (state.selectedLibraryIds.has(l.Id) ? ' active' : '') + '" data-lib="' + esc(l.Id) + '">' +
                    esc(l.Name) + ' <span class="gf-chip-type">' + libraryTypeLabel(l) + '</span></span>';
            });
            if (!libs.length) {
                html = '<span class="gf-muted">No matching libraries found.</span>';
            }
            $('#gfLibraries').innerHTML = html;

            // Do not send selected libraries that do not fit the current mode.
            state.selectedLibraryIds.forEach(function (id) {
                if (!visibleIds.has(id)) { state.selectedLibraryIds.delete(id); }
            });
        }

        function startScan() {
            var body = {
                ScanSeries: state.mode !== 'movies',
                ScanMovies: state.mode !== 'series',
                LibraryIds: Array.from(state.selectedLibraryIds),
                MaxParallelRequests: parseInt($('#gfParallel').value, 10) || 3,
                IgnoreCache: $('#gfIgnoreCache').checked
            };
            $('#gfStartBtn').disabled = true;
            apiPost('GapFinder/Scan', body).then(function (status) {
                // A one-off choice: the next search uses the saved answers again.
                $('#gfIgnoreCache').checked = false;
                applyStatus(status);
                startPolling();
            }).catch(function (err) {
                $('#gfStartBtn').disabled = false;
                errorText(err).then(function (t) { setScanMessage(esc(t), true); });
            });
        }

        // Emby's own dialogs instead of window.confirm/alert, which some browsers and embedded views block.
        // Text is escaped and line breaks become <br>; without the Emby modules the browser dialogs are used.
        function dialogHtml(text) {
            return esc(String(text)).replace(/\n/g, '<br>');
        }

        function embyConfirm(text, title, confirmText) {
            return new Promise(function (resolve) {
                try {
                    require(['confirm'], function (confirm) {
                        var fn = confirm.default || confirm;
                        fn({ html: dialogHtml(text), title: title || 'Please confirm', confirmText: confirmText, primary: 'delete' })
                            .then(function () { resolve(true); }, function () { resolve(false); });
                    }, function () { resolve(window.confirm(text)); });
                } catch (e) {
                    resolve(window.confirm(text));
                }
            });
        }

        function embyAlert(text, title) {
            try {
                require(['alert'], function (alert) {
                    var fn = alert.default || alert;
                    fn({ html: dialogHtml(text), title: title || 'Gap Finder' }).catch(function () { });
                }, function () { window.alert(text); });
            } catch (e) {
                window.alert(text);
            }
        }

        // Deletes the saved results of both tabs (ignore list and search settings stay).
        function clearResults(confirmed) {
            if (confirmed !== true) {
                embyConfirm('Delete the saved results of both tabs (series and movie collections)?\nThe ignore list is kept.', 'Clear search', 'Delete')
                    .then(function (ok) { if (ok) { clearResults(true); } });
                return;
            }
            $('#gfClearBtn').disabled = true;
            ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('GapFinder/Results/Clear') }).then(function () {
                $('#gfClearBtn').disabled = false;
                state.series.open.clear();
                state.movies.open.clear();
                setScanMessage('Search results cleared.', false);
                return Promise.all([loadSeries(), loadMovies()]);
            }).catch(function (err) {
                $('#gfClearBtn').disabled = false;
                errorText(err).then(function (t) { setScanMessage(esc(t), true); });
            });
        }

        function cancelScan() {
            $('#gfCancelBtn').disabled = true;
            apiPost('GapFinder/Scan/Cancel').then(applyStatus).catch(function () { $('#gfCancelBtn').disabled = false; });
        }

        function startPolling() {
            stopPolling();
            state.pollTimer = setInterval(pollStatus, 1000);
        }

        function stopPolling() {
            if (state.pollTimer) {
                clearInterval(state.pollTimer);
                state.pollTimer = null;
            }
        }

        function pollStatus() {
            apiGet('GapFinder/Status').then(function (status) {
                var wasRunning = state.status && state.status.IsRunning;
                applyStatus(status);
                if (!status.IsRunning) {
                    stopPolling();
                    if (wasRunning && !status.WasCancelled && !status.LastError) {
                        reloadAfterScan(status);
                    }
                }
            }).catch(function () { /* next attempt on the next tick */ });
        }

        function applyStatus(status) {
            state.status = status;
            var running = !!status.IsRunning;
            $('#gfStartBtn').disabled = running;
            $('#gfClearBtn').disabled = running;
            $('#gfCancelBtn').style.display = running ? '' : 'none';
            $('#gfCancelBtn').disabled = false;

            var bar = $('#gfScanProgress');
            bar.style.display = running ? '' : 'none';

            if (running) {
                var phase = status.Phase === 'Movies' ? 'Movie collections'
                    : (status.Phase === 'MovieInfo' ? 'Reading movie collection info' : 'Series');
                var total = status.Total || 0;
                var done = Math.min(status.Processed || 0, total);
                bar.firstElementChild.style.width = (total ? Math.round(done * 100 / total) : 0) + '%';
                var text = total
                    ? phase + ': ' + done + ' / ' + total
                    : phase + ': collecting items...';
                if (status.CurrentItem) { text += ' &middot; <span class="gf-muted">' + esc(status.CurrentItem) + '</span>'; }
                setScanMessage(text, false);
            } else if (status.LastError) {
                setScanMessage('Search failed: ' + esc(status.LastError), true);
            } else if (status.WasCancelled) {
                setScanMessage('Search cancelled - previous results were kept.', false);
            } else if (status.FinishedUtc) {
                var msg = 'Last search finished ' + esc(formatDateTime(status.FinishedUtc));
                if (status.ItemErrors) { msg += ' &middot; <span class="gf-error">' + status.ItemErrors + ' item(s) could not be loaded from the provider</span>'; }
                setScanMessage(msg, false);
            } else {
                setScanMessage('', false);
            }
        }

        function reloadAfterScan(status) {
            var loads = [];
            if (status.ScanSeries) { loads.push(loadSeries()); }
            if (status.ScanMovies) { loads.push(loadMovies()); }
            Promise.all(loads).then(function () {
                if (status.ScanSeries !== status.ScanMovies) { switchTab(status.ScanSeries ? 'series' : 'movies'); }
            });
        }

        // =================================================================
        // Loading results
        // =================================================================
        function loadSeries() {
            return apiGet('GapFinder/Results/Series').then(function (dto) {
                state.series.results = dto.Results;
                state.series.ignored = new Set(dto.IgnoredKeys || []);
                state.series.limit = PAGE_SIZE;
                renderSeries();
            });
        }

        function loadMovies() {
            return apiGet('GapFinder/Results/Movies').then(function (dto) {
                state.movies.results = dto.Results;
                state.movies.ignored = new Set(dto.IgnoredKeys || []);
                state.movies.limit = PAGE_SIZE;
                renderMovies();
                loadEmbyCollections();
            });
        }

        // Look up the Emby collections of all collections in the result; without an answer there are simply no links.
        function loadEmbyCollections() {
            var res = state.movies.results;
            var list = (res && res.Collections || []).map(function (c) { return { TmdbId: c.TmdbId, Name: c.Name }; });
            if (!list.length) { state.movies.embyCollections = {}; return Promise.resolve(); }
            return apiPost('GapFinder/Collections/EmbyCollections', { Collections: list }).then(function (found) {
                var map = {};
                (found || []).forEach(function (e) { map[e.TmdbId] = e; });
                state.movies.embyCollections = map;
                renderMovies();
            }).catch(function () { /* links are optional */ });
        }

        // Name of a collection; a link that opens it in Emby if an Emby collection exists.
        function collectionNameHtml(tmdbId, name) {
            var e = state.movies.embyCollections[tmdbId];
            if (!e || !e.Id) { return esc(name); }
            var title = 'Open collection "' + e.Name + '" in Emby' + (e.MatchCount > 1 ? ' (' + e.MatchCount + ' matching collections - showing the first)' : '');
            return '<span class="gf-col-link" role="link" tabindex="0" data-open-item="' + esc(e.Id) + '" title="' + esc(title) + '">' + esc(name) + '</span>';
        }

        function setIgnored(key, ignore) {
            return apiPost('GapFinder/Ignore', { Keys: [key], Ignore: ignore }).then(function (dto) {
                var set = new Set(dto.IgnoredKeys || []);
                state.series.ignored = set;
                state.movies.ignored = set;
                renderSeries();
                renderMovies();
            }).catch(function (err) {
                errorText(err).then(function (t) { embyAlert(t); });
            });
        }

        function findCollection(tmdbId) {
            var res = state.movies.results;
            return res && res.Collections ? res.Collections.find(function (c) { return c.TmdbId === tmdbId; }) : null;
        }

        // "Remove from collection": the server cleans up the NFO (with backup), the Emby collection and the database.
        function removeFromCollection(movieId, collectionTmdbId, confirmed) {
            var c = findCollection(collectionTmdbId);
            var movie = c && (c.WronglyAssigned || []).find(function (m) { return m.Id === movieId; });
            if (!c || !movie) { return; }
            var question = 'Remove "' + movie.Name + '" from "' + c.Name + '"?\n\n' +
                '- removes the movie from the Emby collection (if any) and the collection info in the Emby database\n' +
                '- removes the matching <set> entry from the movie\'s NFO file (a backup is kept in the plugin data folder)';
            if (confirmed !== true) {
                embyConfirm(question, 'Remove from collection', 'Remove')
                    .then(function (ok) { if (ok) { removeFromCollection(movieId, collectionTmdbId, true); } });
                return;
            }

            apiPost('GapFinder/Collections/RemoveMovie', { MovieId: movieId, CollectionTmdbId: collectionTmdbId, CollectionName: c.Name }).then(function (r) {
                c.WronglyAssigned = (c.WronglyAssigned || []).filter(function (m) { return m.Id !== movieId; });
                state.movies.ignored.delete(keys.assignment(movieId, collectionTmdbId));
                renderMovies();
                // A collection that became empty was deleted - look up the links again.
                loadEmbyCollections();
                embyAlert(removeResultText(movie.Name, c.Name, r));
            }).catch(function (err) {
                errorText(err).then(function (t) { embyAlert('Remove failed: ' + t); });
            });
        }

        function removeResultText(movieName, collectionName, r) {
            var lines = ['"' + movieName + '" removed from "' + collectionName + '".', ''];
            lines.push('Emby database: ' + (r.DatabaseUpdated ? 'collection info removed' : 'no collection info found'));
            movieResultLines(r, '').forEach(function (l) { lines.push(l); });
            deletedBoxSetLines(r.DeletedBoxSets).forEach(function (l) { lines.push(l); });
            return lines.join('\n');
        }

        function movieResultLines(r, indent) {
            var lines = [];
            if (r.RemovedFromBoxSets && r.RemovedFromBoxSets.length) {
                lines.push(indent + 'Emby collection(s): ' + r.RemovedFromBoxSets.join(', '));
            }
            var files = r.NfoFiles || [];
            if (!files.length) { lines.push(indent + 'NFO: no NFO file found'); }
            files.forEach(function (f) {
                var status = f.Status === 'removed' ? 'entry removed' : (f.Status === 'nomatch' ? 'no matching <set> entry' : 'ERROR: ' + (f.Message || 'unknown'));
                lines.push(indent + 'NFO ' + f.Path + ': ' + status);
            });
            return lines;
        }

        function deletedBoxSetLines(names) {
            return names && names.length ? ['', 'Deleted empty Emby collection(s): ' + names.join(', ')] : [];
        }

        // "Remove collection" for a collection deleted on TMDB: clean up all movies naming it, delete the empty Emby collection.
        function removeRemovedCollection(collectionTmdbId, confirmed) {
            var c = findCollection(collectionTmdbId);
            if (!c) { return; }
            var names = (c.Present || []).map(function (m) { return m.Name + (m.Year ? ' (' + m.Year + ')' : ''); });
            var question = 'Remove "' + c.Name + '" from ' + names.length + ' movie(s)?\n\n' + names.join('\n') + '\n\n' +
                '- removes the movies from the Emby collection and the collection info in the Emby database\n' +
                '- removes the matching <set> entry from each NFO file (a backup is kept in the plugin data folder)\n' +
                '- DELETES the Emby collection "' + c.Name + '" if it is empty afterwards (cannot be undone)';
            if (confirmed !== true) {
                embyConfirm(question, 'Remove collection', 'Remove')
                    .then(function (ok) { if (ok) { removeRemovedCollection(collectionTmdbId, true); } });
                return;
            }

            apiPost('GapFinder/Collections/RemoveRemoved', { CollectionTmdbId: collectionTmdbId }).then(function (r) {
                var lines = ['"' + c.Name + '":', ''];
                (r.Movies || []).forEach(function (m) {
                    if (m.Error) { lines.push(m.MovieName + ': NOT CHANGED - ' + m.Error); return; }
                    lines.push(m.MovieName + ': database ' + (m.DatabaseUpdated ? 'collection info removed' : 'no collection info found'));
                    movieResultLines(m, '   ').forEach(function (l) { lines.push(l); });
                });
                deletedBoxSetLines(r.DeletedBoxSets).forEach(function (l) { lines.push(l); });
                embyAlert(lines.join('\n'));
                return loadMovies();
            }).catch(function (err) {
                errorText(err).then(function (t) { embyAlert('Remove failed: ' + t); });
            });
        }

        // =================================================================
        // Options tab
        // =================================================================
        function loadOptions() {
            return apiGet('GapFinder/Options').then(function (o) {
                state.options = o;
                renderOptionsForm();
                renderSeries();
                renderMovies();
                // Options tab opened before the options arrived: load the lists now.
                if (state.tab === 'options') { autoLoadRadarrLists(); }
            }).catch(function () { /* without options there are no Sonarr/Radarr buttons */ });
        }

        function arrPanel(name) { return page.querySelector('#gfOptionsTab [data-arr="' + name + '"]'); }

        function renderOptionsForm() {
            var o = state.options;
            if (!o) { return; }
            $('#gfOptLogLevel').value = o.LogLevel || 'All';
            ['Sonarr', 'Radarr'].forEach(function (name) {
                var svc = o[name] || {};
                var panel = arrPanel(name);
                panel.querySelector('.gfOptEnabled').checked = !!svc.Enabled;
                panel.querySelector('.gfOptUrl').value = svc.Url || '';
                var radio = panel.querySelector('input[type="radio"][value="' + (svc.Mode || 'Link') + '"]');
                if (radio && !radio.disabled) { radio.checked = true; }
                panel.querySelector('.gfOptKey').value = '';
                panel.querySelector('.gfOptKeyClear').checked = false;
                panel.querySelector('.gfOptKeyClearWrap').style.display = svc.HasApiKey || svc.ApiKeyUnreadable ? '' : 'none';
                panel.querySelector('.gfOptKeyInfo').textContent = svc.ApiKeyUnreadable
                    ? 'The saved key cannot be read (key file missing in the plugin data folder) - please enter it again.'
                    : (svc.HasApiKey ? 'A key is saved. Leave the field empty to keep it.' : 'No key saved yet.');
                panel.querySelector('.gfOptSearchNow').checked = !!svc.SearchNow;
                if (name === 'Radarr') {
                    panel.querySelector('.gfOptAvailability').value = svc.MinimumAvailability || 'released';
                    fillRadarrSelects();
                }
                updateApiVisibility(name);
            });
        }

        function updateApiVisibility(name) {
            var panel = arrPanel(name);
            var mode = panel.querySelector('input[type="radio"]:checked');
            var isApi = !!mode && mode.value === 'Api';
            // Only the hint of the connection type: link text for "Link", API fields and API hint for "API".
            panel.querySelector('.gfOptApi').style.display = isApi ? '' : 'none';
            panel.querySelector('.gfOptLinkHint').style.display = isApi ? 'none' : '';
        }

        /// Profile/root folder: list from the last "Test connection"; otherwise only the saved value.
        function fillRadarrSelects() {
            var panel = arrPanel('Radarr');
            var saved = (state.options && state.options.Radarr) || {};
            var meta = state.arrMeta.Radarr;
            var profile = panel.querySelector('.gfOptProfile');
            var root = panel.querySelector('.gfOptRoot');
            var currentProfile = String(profile.value && profile.value !== '0' ? profile.value : (saved.QualityProfileId || 0));
            var currentRoot = root.value || saved.RootFolder || '';
            var profiles = meta ? meta.QualityProfiles : (saved.QualityProfileId ? [{ id: saved.QualityProfileId, name: saved.QualityProfileName || ('profile id ' + saved.QualityProfileId) }] : []);
            var roots = meta ? meta.RootFolders : (saved.RootFolder ? [{ path: saved.RootFolder }] : []);
            profile.innerHTML = '<option value="0">' + (profiles.length ? '(choose)' : '(test connection first)') + '</option>' +
                profiles.map(function (q) { return '<option value="' + q.id + '">' + esc(q.name) + '</option>'; }).join('');
            root.innerHTML = '<option value="">' + (roots.length ? '(choose)' : '(test connection first)') + '</option>' +
                roots.map(function (r) { return '<option value="' + esc(r.path) + '">' + esc(r.path) + '</option>'; }).join('');
            profile.value = profiles.some(function (q) { return String(q.id) === currentProfile; }) ? currentProfile : '0';
            root.value = roots.some(function (r) { return r.path === currentRoot; }) ? currentRoot : '';
        }

        function testArrConnection(name) {
            var panel = arrPanel(name);
            var out = panel.querySelector('.gfOptTestResult');
            out.textContent = 'Testing...';
            out.className = 'gf-status gfOptTestResult';
            apiPost('GapFinder/Arr/Test', {
                Service: name,
                Url: (panel.querySelector('.gfOptUrl').value || '').trim(),
                ApiKey: (panel.querySelector('.gfOptKey').value || '').trim()
            }).then(function (r) {
                out.textContent = r.Message || (r.Ok ? 'Connected.' : 'Connection failed.');
                out.classList.add(r.Ok ? 'gf-ok-text' : 'gf-error');
                if (r.Ok && name === 'Radarr') {
                    state.arrMeta.Radarr = { QualityProfiles: r.QualityProfiles || [], RootFolders: r.RootFolders || [] };
                    fillRadarrSelects();
                }
            }).catch(function (err) {
                errorText(err).then(function (t) { out.textContent = t; out.classList.add('gf-error'); });
            });
        }

        function collectArr(name) {
            var panel = arrPanel(name);
            var mode = panel.querySelector('input[type="radio"]:checked');
            var result = {
                Enabled: panel.querySelector('.gfOptEnabled').checked,
                Mode: mode ? mode.value : 'Link',
                Url: (panel.querySelector('.gfOptUrl').value || '').trim(),
                ApiKey: (panel.querySelector('.gfOptKey').value || '').trim(),
                ClearApiKey: panel.querySelector('.gfOptKeyClear').checked,
                SearchNow: panel.querySelector('.gfOptSearchNow').checked
            };
            if (name === 'Radarr') {
                var profileSelect = panel.querySelector('.gfOptProfile');
                result.QualityProfileId = parseInt(profileSelect.value, 10) || 0;
                // Send the name too - the page shows it next time without a "Test connection".
                result.QualityProfileName = result.QualityProfileId && profileSelect.selectedIndex >= 0 ? profileSelect.options[profileSelect.selectedIndex].text : '';
                result.RootFolder = panel.querySelector('.gfOptRoot').value || '';
                result.MinimumAvailability = panel.querySelector('.gfOptAvailability').value;
            }
            return result;
        }

        function setOptionsMessage(text, isError) {
            var el = $('#gfOptStatus');
            el.textContent = text;
            el.classList.toggle('gf-error', !!isError);
            el.classList.toggle('gf-ok-text', !isError && !!text);
        }

        function saveOptions() {
            var body = { LogLevel: $('#gfOptLogLevel').value, Sonarr: collectArr('Sonarr'), Radarr: collectArr('Radarr') };
            $('#gfOptSave').disabled = true;
            setOptionsMessage('', false);
            apiPost('GapFinder/Options', body).then(function (o) {
                state.options = o;
                renderOptionsForm();
                renderSeries();
                renderMovies();
                setOptionsMessage('Saved.', false);
            }).catch(function (err) {
                errorText(err).then(function (t) { setOptionsMessage(t, true); });
            }).then(function () { $('#gfOptSave').disabled = false; });
        }

        // "Open": open the address from the input field (possibly unsaved) in a new tab - for checking.
        function openArrUrl(name) {
            var url = (arrPanel(name).querySelector('.gfOptUrl').value || '').trim().replace(/\/+$/, '');
            if (!/^https?:\/\/[^\s]+$/i.test(url)) {
                setOptionsMessage(name + ': please enter a full address starting with http:// or https://', true);
                return;
            }
            window.open(url, '_blank', 'noopener');
        }

        /// "Sonarr"/"Radarr" button ("Link" connection): opens the add search of the service, e.g.
        /// <url>/add/new?term=tmdb:603. Empty when the service is not enabled.
        // Only http(s) links - the server validates the address on save; this catches a hand-edited
        // GapFinder.xml (otherwise e.g. "javascript:" would be clickable).
        function isHttpUrl(url) { return /^https?:\/\//i.test(url || ''); }

        function arrLinkHtml(name, term) {
            var svc = state.options && state.options[name];
            if (!svc || !svc.Enabled || !isHttpUrl(svc.Url) || !term) { return ''; }
            var href = svc.Url + '/add/new?term=' + encodeURIComponent(term);
            return '<a class="gf-btn small link" style="text-decoration:none;" target="_blank" rel="noopener" href="' + esc(href) +
                '" title="Open ' + name + ' &quot;Add new&quot; search for ' + esc(term) + '">Find in ' + name + '</a>';
        }

        /// "Link", "Api" or null (service off/incomplete).
        function arrMode(name) {
            var svc = state.options && state.options[name];
            if (!svc || !svc.Enabled || !svc.Url) { return null; }
            return svc.Mode === 'Api' && svc.HasApiKey ? 'Api' : 'Link';
        }

        // Link to the series at the provider that supplied the episode list (first enabled series downloader of the
        // library). Results without EpisodeProvider: TVDB, otherwise TMDB.
        function seriesProviderLinkHtml(s) {
            var ids = s.ProviderIds || {};
            var key = s.EpisodeProvider || (ids.Tvdb ? 'Tvdb' : 'Tmdb');
            var id = ids[key];
            if (!id) { return ''; }
            var href = key === 'Tvdb' ? 'https://thetvdb.com/?tab=series&id=' + encodeURIComponent(id) : 'https://www.themoviedb.org/tv/' + encodeURIComponent(id);
            return '<a class="gf-btn small link" style="text-decoration:none;" target="_blank" rel="noopener" href="' + esc(href) + '" data-stop="1">' +
                (key === 'Tvdb' ? 'TVDB' : 'TMDB') + '</a>';
        }

        function sonarrButtonHtml(s) {
            if (arrMode('Sonarr') === 'Api') {
                return '<button type="button" class="gf-btn small link" data-sonarr-search="' + esc(s.Id) + '" title="Send the shown missing episodes to Sonarr">Send to Sonarr</button>';
            }
            return arrLinkHtml('Sonarr', s.ProviderIds && s.ProviderIds.Tvdb ? 'tvdb:' + s.ProviderIds.Tvdb : s.Name);
        }

        function radarrButtonHtml(m) {
            if (arrMode('Radarr') !== 'Api') { return arrLinkHtml('Radarr', 'tmdb:' + m.TmdbId); }
            var st = state.radarrStatus[m.TmdbId];
            if (st) {
                var cls = st.Status === 'error' ? 'gf-error' : 'gf-ok-text';
                return '<span class="gf-arr-status ' + cls + '" title="' + esc(st.Message || '') + '">Radarr: ' + esc(st.Message || st.Status) + '</span>';
            }
            return '<button type="button" class="gf-btn small link" data-radarr-add="' + esc(m.TmdbId) + '" title="Add this movie to Radarr">Send to Radarr</button>';
        }

        function addToRadarr(tmdbIds, label, confirmed) {
            if (!tmdbIds.length) { return; }
            if (tmdbIds.length > 1 && confirmed !== true) {
                embyConfirm('Add ' + tmdbIds.length + ' movies of "' + label + '" to Radarr?', 'Radarr', 'Add')
                    .then(function (ok) { if (ok) { addToRadarr(tmdbIds, label, true); } });
                return;
            }
            apiPost('GapFinder/Radarr/Add', { TmdbIds: tmdbIds }).then(function (results) {
                (results || []).forEach(function (r) { state.radarrStatus[r.TmdbId] = r; });
                renderMovies();
                if (tmdbIds.length > 1) {
                    var added = (results || []).filter(function (r) { return r.Status === 'added'; }).length;
                    var exists = (results || []).filter(function (r) { return r.Status === 'exists'; }).length;
                    var failed = (results || []).filter(function (r) { return r.Status === 'error'; });
                    embyAlert('Radarr: ' + added + ' added, ' + exists + ' already in Radarr' +
                        (failed.length ? ', ' + failed.length + ' failed:\n' + failed.map(function (r) { return '- ' + (r.Title || r.TmdbId) + ': ' + r.Message; }).join('\n') : '.'));
                }
            }).catch(function (err) {
                errorText(err).then(function (t) { embyAlert('Radarr: ' + t); });
            });
        }

        /// Small notice dialog (inside .gfRoot so theme/accent color apply). link = { href, label } or null.
        function showNotice(title, html, link) {
            var root = page.querySelector('.gfRoot');
            var old = root.querySelector('.gf-notice-overlay');
            if (old) { old.parentNode.removeChild(old); }
            var overlay = document.createElement('div');
            overlay.className = 'gf-notice-overlay';
            overlay.innerHTML = '<div class="gf-notice" role="dialog" aria-modal="true">' +
                '<h2>' + esc(title) + '</h2><div class="gf-notice-body">' + html + '</div>' +
                '<div class="gf-row" style="justify-content:flex-end;">' +
                (link && isHttpUrl(link.href) ? '<a class="gf-btn" target="_blank" rel="noopener" href="' + esc(link.href) + '">' + esc(link.label) + '</a>' : '') +
                '<button type="button" class="gf-btn" data-notice-close="1">Close</button></div></div>';
            function close() { if (overlay.parentNode) { overlay.parentNode.removeChild(overlay); } }
            overlay.addEventListener('click', function (ev) {
                // The link opens in a new tab and closes the dialog; a click next to the dialog closes it too.
                if (ev.target === overlay || ev.target.closest('[data-notice-close]') || ev.target.closest('a')) { setTimeout(close, 0); }
            });
            root.appendChild(overlay);
            var focusTarget = overlay.querySelector('a') || overlay.querySelector('button');
            if (focusTarget) { focusTarget.focus(); }
        }

        /// Sends the currently visible (filters!) and not ignored missing episodes of a series to Sonarr.
        function searchInSonarr(seriesId, confirmed) {
            var entry = buildSeriesView(seriesFilters()).list.find(function (x) { return x.s.Id === seriesId; });
            if (!entry) { return; }
            var s = entry.s;
            var episodes = [];
            entry.seasons.forEach(function (season) {
                season.episodes.forEach(function (e) {
                    if (isEpisodeIgnored(s, e, state.series.ignored)) { return; }
                    episodes.push({ SeasonNumber: e.SeasonNumber, EpisodeNumber: e.EpisodeNumber, EpisodeNumberEnd: e.EpisodeNumberEnd, Name: e.Name, PremiereDate: e.PremiereDate });
                });
            });
            if (!episodes.length) { embyAlert('No missing episodes shown for "' + s.Name + '".'); return; }
            var search = state.options.Sonarr.SearchNow ? ' and search for them' : '';
            if (confirmed !== true) {
                embyConfirm('Send ' + episodes.length + ' missing episode(s) of "' + s.Name + '" to Sonarr (monitor' + search + ')?\n\nOnly the episodes currently shown with your filters are sent. If the series is not monitored in Sonarr, it is set to monitored.', 'Sonarr', 'Send')
                    .then(function (ok) { if (ok) { searchInSonarr(seriesId, true); } });
                return;
            }
            apiPost('GapFinder/Sonarr/Search', { TvdbId: s.ProviderIds && s.ProviderIds.Tvdb, SeriesName: s.Name, Episodes: episodes }).then(function (r) {
                if (r.Status === 'notInSonarr') {
                    // No window.open: after the asynchronous answer it no longer counts as a user click and the popup
                    // blocker silently drops it. Instead a link the user clicks.
                    showNotice('Sonarr - "' + s.Name + '"', esc(r.Message),
                        r.AddUrl ? { href: r.AddUrl, label: 'Open "Add new series" in Sonarr' } : null);
                    return;
                }
                if (r.Status === 'error') { embyAlert('Sonarr: ' + r.Message); return; }
                var lines = ['Sonarr - "' + s.Name + '":', ''];
                if (r.SeriesMonitored) { lines.push('Series set to monitored in Sonarr'); }
                lines = lines.concat([
                    r.Monitored + ' episode(s) monitored' + (r.SearchStarted ? ' and searched' : ''),
                    r.AlreadyDownloaded + ' already downloaded by Sonarr (not yet in Emby?)']);
                if (r.MatchedByDateOrTitle) { lines.push(r.MatchedByDateOrTitle + ' matched by air date/title (numbering differs)'); }
                if (r.Unmatched && r.Unmatched.length) {
                    lines.push(r.Unmatched.length + ' not sent (no clear match in Sonarr):');
                    r.Unmatched.slice(0, 15).forEach(function (u) { lines.push('- ' + u); });
                    if (r.Unmatched.length > 15) { lines.push('...'); }
                }
                embyAlert(lines.join('\n'));
            }).catch(function (err) {
                errorText(err).then(function (t) { embyAlert('Sonarr: ' + t); });
            });
        }

        function switchTab(tab) {
            state.tab = tab;
            page.querySelectorAll('.gf-tab').forEach(function (t) {
                t.classList.toggle('active', t.getAttribute('data-tab') === tab);
            });
            $('#gfSeriesTab').style.display = tab === 'series' ? '' : 'none';
            $('#gfMoviesTab').style.display = tab === 'movies' ? '' : 'none';
            $('#gfOptionsTab').style.display = tab === 'options' ? '' : 'none';
            if (tab === 'options') { autoLoadRadarrLists(); }
        }

        /// When the Options tab opens, load profiles/root folders once per page view if Radarr is set up via API with
        /// a saved key - otherwise the selection only shows the saved value.
        function autoLoadRadarrLists() {
            var svc = state.options && state.options.Radarr;
            if (state.radarrAutoLoaded || state.arrMeta.Radarr || !svc || svc.Mode !== 'Api' || !svc.HasApiKey || !svc.Url) { return; }
            state.radarrAutoLoaded = true;
            testArrConnection('Radarr');
        }

        // =================================================================
        // Series tab
        // =================================================================
        function seriesFilters() {
            return {
                query: ($('#gfSeriesSearch').value || '').trim().toLowerCase(),
                sort: $('#gfSeriesSort').value,
                status: $('#gfSeriesStatus').value,
                specials: $('#gfSpecials').checked,
                unaired: $('#gfUnaired').checked,
                onlyFullSeasons: $('#gfOnlyFullSeasons').checked,
                showIgnored: $('#gfSeriesIgnored').checked
            };
        }

        // Must match ResultFilter.IsEpisodeVisible (C#) - the export uses the same rules.
        function isEpisodeIgnored(s, e, ignored) {
            return ignored.has(keys.series(s.Id)) || ignored.has(keys.season(s.Id, e.SeasonNumber)) ||
                ignored.has(keys.episode(s.Id, e.SeasonNumber, e.EpisodeNumber));
        }

        // Date column: aired/released = grey date; a future date ("upcoming"/"unreleased") or no date in orange.
        function dateCellHtml(date, futureLabel) {
            if (!date) { return '<span class="gf-ep-date gf-date-flag"><i>no date</i></span>'; }
            if (!isReleased(date)) { return '<span class="gf-ep-date gf-date-flag">' + futureLabel + ' ' + esc(formatDate(date)) + '</span>'; }
            return '<span class="gf-ep-date">' + esc(formatDate(date)) + '</span>';
        }

        // Same rule as ResultFilter.IsAired: aired by date, or no date but assumed aired by the server.
        function isEpisodeAired(e) {
            return isReleased(e.PremiereDate) || !!e.AssumedAired;
        }

        function isEpisodeVisible(s, e, f, ignored) {
            if (!f.specials && e.SeasonNumber === 0) { return false; }
            if (!f.unaired && !isEpisodeAired(e)) { return false; }
            return f.showIgnored || !isEpisodeIgnored(s, e, ignored);
        }

        /// Computes the visible series including seasons, counters and completeness.
        function buildSeriesView(f) {
            var res = state.series.results;
            var ignored = state.series.ignored;
            var list = [];
            var noData = [];
            if (!res || !res.Series) { return { list: list, noData: noData }; }

            res.Series.forEach(function (s) {
                if (s.NoProviderData || s.Error) { noData.push(s); return; }
                if (f.status && s.Status !== f.status) { return; }
                var seriesIgnored = ignored.has(keys.series(s.Id));
                if (seriesIgnored && !f.showIgnored) { return; }

                var seriesMatches = !f.query || matches(s.Name, f.query);
                var queryMatch = function (e) { return seriesMatches || matches(e.Name, f.query) || matches(episodeCode(e), f.query); };
                // Upcoming/undated episodes hidden by the filter: counted for "+N upcoming", shown only on request.
                var withUnaired = f.unaired ? null : Object.assign({}, f, { unaired: true });
                var showUpcoming = !!withUnaired && state.series.showUpcoming.has(s.Id);
                var present = 0;
                var missing = 0;
                var upcoming = 0;
                var fullSeasons = 0;
                var seasons = [];

                (s.Seasons || []).forEach(function (season) {
                    if (season.SeasonNumber === 0 && !f.specials) { return; }
                    var eps = (season.Missing || []).filter(function (e) { return isEpisodeVisible(s, e, f, ignored) && queryMatch(e); });
                    var upEps = !withUnaired ? [] : (season.Missing || []).filter(function (e) {
                        return !isEpisodeAired(e) && isEpisodeVisible(s, e, withUnaired, ignored) && queryMatch(e);
                    });
                    var isFull = season.PresentCount === 0 && eps.length > 0;
                    if (f.onlyFullSeasons && !isFull) { return; }
                    present += season.PresentCount;
                    upcoming += upEps.length;
                    if (!eps.length && !(showUpcoming && upEps.length)) { return; }
                    missing += eps.length;
                    if (isFull) { fullSeasons++; }
                    var shown = showUpcoming ? eps.concat(upEps).sort(function (a, b) { return a.EpisodeNumber - b.EpisodeNumber; }) : eps;
                    seasons.push({ season: season, episodes: shown, missingCount: eps.length, upcomingCount: showUpcoming ? upEps.length : 0, isFull: isFull });
                });

                if (!missing) { return; }
                list.push({ s: s, seasons: seasons, present: present, missing: missing, upcoming: upcoming, showUpcoming: showUpcoming,
                    fullSeasons: fullSeasons, pct: pct(present, missing), ignored: seriesIgnored });
            });

            list.sort(function (a, b) {
                if (f.sort === 'missing') { return (b.missing - a.missing) || compareNames(a.s.Name, b.s.Name); }
                if (f.sort === 'completeness') { return (a.pct - b.pct) || (b.missing - a.missing) || compareNames(a.s.Name, b.s.Name); }
                if (f.sort === 'year') { return ((b.s.Year || 0) - (a.s.Year || 0)) || compareNames(a.s.Name, b.s.Name); }
                return compareNames(a.s.Name, b.s.Name);
            });
            return { list: list, noData: noData };
        }

        function renderSeries() {
            var container = $('#gfSeriesList');
            var res = state.series.results;
            if (!res || !res.ScanDateUtc) {
                $('#gfSeriesBadge').textContent = '0';
                $('#gfSeriesSummary').textContent = '';
                container.innerHTML = '<div class="gf-empty">No series search yet. Choose "Series" or "Both" above and start a search.</div>';
                state.visibleSeriesIds = [];
                return;
            }

            var f = seriesFilters();
            var view = buildSeriesView(f);
            var totalMissing = view.list.reduce(function (n, x) { return n + x.missing; }, 0);
            var totalFull = view.list.reduce(function (n, x) { return n + x.fullSeasons; }, 0);

            $('#gfSeriesBadge').textContent = String(totalMissing);
            $('#gfSeriesBadge').className = 'gf-badge' + (totalMissing ? ' warn' : ' ok');
            $('#gfSeriesSummary').innerHTML =
                '<b>' + view.list.length + '</b> series with <b>' + totalMissing + '</b> missing episodes' +
                (totalFull ? ' (<b>' + totalFull + '</b> completely missing seasons)' : '') +
                ' &middot; searched ' + esc(formatDateTime(res.ScanDateUtc)) +
                (res.LibraryNames && res.LibraryNames.length ? ' in ' + esc(res.LibraryNames.join(', ')) : '');

            var shown = view.list.slice(0, state.series.limit);
            state.visibleSeriesIds = shown.map(function (x) { return x.s.Id; });

            var html = providerWarningsHtml(res.Warnings);
            if (!view.list.length) {
                html = '<div class="gf-empty">Nothing missing with the current filters.</div>';
            }
            // The library only matters when the cards come from more than one.
            var showLibrary = new Set(view.list.map(function (x) { return x.s.LibraryName || ''; })).size > 1;
            shown.forEach(function (x) { html += seriesCardHtml(x, showLibrary); });
            if (view.list.length > shown.length) {
                html += '<div class="gf-more"><button type="button" class="gf-btn" data-more="series">Show more (' +
                    (view.list.length - shown.length) + ' remaining)</button></div>';
            }
            if (view.noData.length) {
                html += '<details class="gf-nodata"><summary>' + view.noData.length +
                    ' series without provider data (no TheTVDB/TMDB id or provider error)</summary><ul>' +
                    view.noData.map(function (s) {
                        return '<li><button type="button" class="gf-btn link" data-open-item="' + esc(s.Id) + '">' + esc(s.Name) + '</button>' +
                            (s.Error ? ' <span class="gf-error">' + esc(s.Error) + '</span>' : '') + '</li>';
                    }).join('') + '</ul></details>';
            }
            container.innerHTML = html;
        }

        function seriesCardHtml(x, showLibrary) {
            var s = x.s;
            var open = state.series.open.has(s.Id);
            var meta = [];
            if (s.Year) { meta.push(s.Year); }
            if (s.Status) { meta.push(s.Status === 'Ended' ? 'Ended' : (s.Status === 'Continuing' ? 'Continuing' : esc(s.Status))); }
            var sub = [];
            if (showLibrary && s.LibraryName) { sub.push('<div class="gf-sub">Library: ' + esc(s.LibraryName) + '</div>'); }
            sub.push('<div class="gf-sub">' + x.present + ' of ' + (x.present + x.missing) + ' episodes' +
                (x.upcoming ? ' <span class="gf-upcoming-toggle" role="button" tabindex="0" data-toggle-upcoming="' + esc(s.Id) + '" title="' +
                    (x.showUpcoming ? 'Hide the upcoming episodes and episodes without air date' : 'Show the upcoming episodes and episodes without air date of this series') + '">' +
                    (x.showUpcoming ? 'hide upcoming' : '+' + x.upcoming + ' upcoming') + '</span>' : '') + '</div>');

            var html = '<div class="gf-card' + (open ? ' open' : '') + (x.ignored ? ' ignored' : '') + '" data-series="' + esc(s.Id) + '">' +
                '<div class="gf-card-head" data-toggle-series="' + esc(s.Id) + '">' +
                '<i class="md-icon gf-chevron" aria-hidden="true">chevron_right</i>' +
                '<img class="gf-poster" loading="lazy" src="' + esc(imageUrl(s.Id, 120)) + '" alt="" onerror="this.style.visibility=\'hidden\'" />' +
                '<div class="gf-card-title"><div class="gf-name gf-title-row"><span class="gf-col-link" role="link" tabindex="0" data-open-item="' + esc(s.Id) + '" title="Open series in Emby">' +
                esc(s.Name) + '</span>' + (meta.length ? '<span class="gf-title-meta">' + meta.join(' &middot; ') + '</span>' : '') + '</div>' +
                sub.join('') + '</div>' +
                '<div class="gf-card-stats"><div class="gf-card-nums">' +
                (x.fullSeasons ? '<span class="gf-badge info" title="Completely missing seasons">' + x.fullSeasons + ' full season' + (x.fullSeasons > 1 ? 's' : '') + '</span>' : '') +
                '<span class="gf-badge warn" title="Missing episodes">' + x.missing + '</span>' +
                '<div class="gf-progress ' + progressClass(x.pct) + '"><div style="width:' + x.pct + '%"></div></div>' +
                '<span class="gf-pct">' + x.pct + '%</span>' +
                '</div><div class="gf-card-actions">' +
                seriesProviderLinkHtml(s) +
                sonarrButtonHtml(s) +
                '<button type="button" class="gf-btn small" data-ignore="' + esc(keys.series(s.Id)) + '" data-ignore-state="' + (x.ignored ? '0' : '1') + '">' +
                (x.ignored ? 'Unignore' : 'Ignore') + '</button>' +
                '</div></div></div>';

            if (open) {
                html += '<div class="gf-card-body">' + x.seasons.map(function (entry) { return seasonHtml(s, entry); }).join('') + '</div>';
            }
            return html + '</div>';
        }

        function seasonHtml(s, entry) {
            var season = entry.season;
            var ignoredSet = state.series.ignored;
            var seasonKey = keys.season(s.Id, season.SeasonNumber);
            var seasonIgnored = ignoredSet.has(seasonKey);
            var title = season.SeasonNumber === 0 ? 'Specials' : 'Season ' + season.SeasonNumber;
            var upcomingInfo = entry.upcomingCount ? ', ' + entry.upcomingCount + ' upcoming' : '';
            var info = entry.isFull
                ? '<span class="gf-badge info">completely missing &middot; ' + entry.missingCount + ' episodes</span>' +
                    (entry.upcomingCount ? ' <span class="gf-muted" style="font-weight:normal;">' + entry.upcomingCount + ' upcoming</span>' : '')
                : entry.missingCount
                    ? '<span class="gf-muted" style="font-weight:normal;">' + entry.missingCount + ' missing, ' + season.PresentCount + ' in library' + upcomingInfo + '</span>'
                    : '<span class="gf-muted" style="font-weight:normal;">' + entry.upcomingCount + ' upcoming</span>';

            var html = '<div class="gf-season"><div class="gf-season-head">' + esc(title) + ' ' + info +
                '<span style="flex:1 1 auto;"></span>' +
                '<button type="button" class="gf-btn small" data-ignore="' + esc(seasonKey) + '" data-ignore-state="' + (seasonIgnored ? '0' : '1') + '">' +
                (seasonIgnored ? 'Unignore season' : 'Ignore season') + '</button></div>';

            entry.episodes.forEach(function (e) {
                var epKey = keys.episode(s.Id, e.SeasonNumber, e.EpisodeNumber);
                var epIgnored = isEpisodeIgnored(s, e, ignoredSet);
                var ownIgnored = ignoredSet.has(epKey);
                html += '<div class="gf-ep' + (epIgnored ? ' ignored' : '') + '"' + (e.Overview ? ' title="' + esc(e.Overview) + '"' : '') + '>' +
                    '<span class="gf-ep-code">' + esc(episodeCode(e)) + '</span>' +
                    '<span class="gf-ep-name">' + esc(e.Name || '(no title)') + '</span>' +
                    dateCellHtml(e.PremiereDate, 'upcoming') +
                    '<button type="button" class="gf-btn small link" data-ignore="' + esc(epKey) + '" data-ignore-state="' + (ownIgnored ? '0' : '1') + '">' +
                    (ownIgnored ? 'Unignore' : 'Ignore') + '</button>' +
                    '</div>';
            });
            return html + '</div>';
        }

        // =================================================================
        // Movie collections tab
        // =================================================================
        function movieFilters() {
            return {
                query: ($('#gfMoviesSearch').value || '').trim().toLowerCase(),
                sort: $('#gfMoviesSort').value,
                unreleased: $('#gfUnreleased').checked,
                showIgnored: $('#gfMoviesIgnored').checked
            };
        }

        // Must match ResultFilter.IsMovieVisible (C#).
        function isMovieIgnored(c, m, ignored) {
            return ignored.has(keys.collection(c.TmdbId)) || (!!m.TmdbId && ignored.has(keys.movie(m.TmdbId)));
        }

        function buildMoviesView(f) {
            var res = state.movies.results;
            var ignored = state.movies.ignored;
            var list = [];
            var noData = [];
            // Outdated metadata of local movies: collection deleted on TMDB, or movie does not belong to it on TMDB.
            var removed = [];
            var wrong = [];
            var validCount = 0;
            if (!res || !res.Collections) { return { list: list, noData: noData, removed: removed, wrong: wrong, validCount: 0 }; }

            res.Collections.forEach(function (c) {
                (c.WronglyAssigned || []).forEach(function (m) {
                    var accepted = ignored.has(keys.assignment(m.Id, c.TmdbId));
                    if (!accepted || f.showIgnored) { wrong.push({ movie: m, collection: c, ignored: accepted }); }
                });
                if (c.RemovedFromTmdb) {
                    var removedIgnored = ignored.has(keys.collection(c.TmdbId));
                    if (!removedIgnored || f.showIgnored) { removed.push({ c: c, ignored: removedIgnored }); }
                    return;
                }
                if (c.Error) { noData.push(c); return; }
                // No movie really belongs to the collection on TMDB and no assignment was accepted - do not show it.
                var presentList = presentWithAccepted(c, ignored);
                if (!presentList.length) { return; }
                validCount++;
                var collectionIgnored = ignored.has(keys.collection(c.TmdbId));
                if (collectionIgnored && !f.showIgnored) { return; }

                var collectionMatches = !f.query || matches(c.Name, f.query) ||
                    presentList.some(function (p) { return matches(p.Name, f.query); });
                var missing = (c.Missing || []).filter(function (m) {
                    if (!f.unreleased && !isReleased(m.PremiereDate)) { return false; }
                    if (!f.showIgnored && isMovieIgnored(c, m, ignored)) { return false; }
                    return collectionMatches || matches(m.Name, f.query);
                });
                if (!missing.length) { return; }
                var present = presentList.length;
                list.push({ c: c, missing: missing, present: present, presentList: presentList, pct: pct(present, missing.length), ignored: collectionIgnored });
            });

            list.sort(function (a, b) {
                if (f.sort === 'missing') { return (b.missing.length - a.missing.length) || compareNames(a.c.Name, b.c.Name); }
                if (f.sort === 'completeness') { return (a.pct - b.pct) || compareNames(a.c.Name, b.c.Name); }
                return compareNames(a.c.Name, b.c.Name);
            });
            removed.sort(function (a, b) { return compareNames(a.c.Name, b.c.Name); });
            wrong.sort(function (a, b) { return compareNames(a.movie.Name, b.movie.Name); });
            return { list: list, noData: noData, removed: removed, wrong: wrong, validCount: validCount };
        }

        // Present movies including accepted assignments. Must match ResultFilter.PresentWithAccepted (C#).
        function presentWithAccepted(c, ignored) {
            return (c.Present || []).concat((c.WronglyAssigned || []).filter(function (m) {
                return ignored.has(keys.assignment(m.Id, c.TmdbId));
            }));
        }

        // Movie as text link (opens the movie in Emby, also with Enter); several are separated by " | " (titles
        // often contain commas). Non-breaking space before the bar: wrap only after it, never a "|" at a line start.
        function movieLink(m) {
            return '<span class="gf-col-link" role="link" tabindex="0" data-open-item="' + esc(m.Id) + '">' + esc(m.Name) + (m.Year ? ' (' + m.Year + ')' : '') + '</span>';
        }

        function movieLinks(list) {
            return (list || []).map(movieLink).join('<span class="gf-sep">&nbsp;| </span>');
        }

        // Hints about outdated collection entries (Emby database/NFO) - for cleanup, not search errors.
        function outdatedCollectionsHtml(view) {
            var html = '';
            if (view.removed.length) {
                html += '<details class="gf-nodata" data-details="removed"' + (state.movies.openDetails.has('removed') ? ' open' : '') + '><summary>' + view.removed.length +
                    ' collection(s) removed from TMDB &ndash; these movies still name them in their metadata/NFO</summary>' +
                    '<div class="gf-outdated">' + view.removed.map(function (r) {
                        var c = r.c;
                        // Ignore = hide the hint; Remove = remove the collection from all movies naming it, delete the empty Emby collection.
                        return '<div class="gf-outdated-row' + (r.ignored ? ' gf-ignored-row' : '') + '"><span class="gf-outdated-name">' + collectionNameHtml(c.TmdbId, c.Name) +
                            ' <span class="gf-muted">(TMDB ' + esc(c.TmdbId) + ')</span></span>' +
                            '<span class="gf-outdated-movies">' + movieLinks(c.Present) + '</span>' +
                            '<span class="gf-outdated-actions">' +
                            '<button type="button" class="gf-btn small link" data-ignore="' + esc(keys.collection(c.TmdbId)) + '" data-ignore-state="' + (r.ignored ? '0' : '1') + '"' +
                            ' title="' + (r.ignored ? 'Show this collection again' : 'Hide this collection from the list') + '">' +
                            (r.ignored ? 'Unignore' : 'Ignore') + '</button>' +
                            (r.ignored ? '' : '<button type="button" class="gf-btn small danger" data-remove-removed="' + esc(c.TmdbId) + '">Remove collection</button>') +
                            '</span></div>';
                    }).join('') + '</div></details>';
            }
            if (view.wrong.length) {
                // Ignore = accept the assignment (the movie counts as present); Remove = remove it from the Emby collection, database and NFO.
                html += '<details class="gf-nodata" data-details="wrong"' + (state.movies.openDetails.has('wrong') ? ' open' : '') + '><summary>' + view.wrong.length +
                    ' movie(s) assigned to a collection they do not belong to on TMDB</summary>' +
                    '<div class="gf-outdated">' + view.wrong.map(function (w) {
                        var key = keys.assignment(w.movie.Id, w.collection.TmdbId);
                        return '<div class="gf-outdated-row' + (w.ignored ? ' gf-ignored-row' : '') + '"><span class="gf-outdated-name">' + movieLink(w.movie) + '</span>' +
                            '<span class="gf-outdated-movies">&rarr; ' + collectionNameHtml(w.collection.TmdbId, w.collection.Name) +
                            ' <a class="gf-btn small link" style="text-decoration:none;" target="_blank" rel="noopener" href="https://www.themoviedb.org/collection/' +
                            encodeURIComponent(w.collection.TmdbId) + '">TMDB ' + esc(w.collection.TmdbId) + '</a></span>' +
                            '<span class="gf-outdated-actions">' +
                            '<button type="button" class="gf-btn small link" data-ignore="' + esc(key) + '" data-ignore-state="' + (w.ignored ? '0' : '1') + '"' +
                            ' title="' + (w.ignored ? 'Show this assignment as wrong again' : 'Keep this assignment: the movie counts as part of the collection') + '">' +
                            (w.ignored ? 'Unignore' : 'Ignore') + '</button>' +
                            (w.ignored ? '' : '<button type="button" class="gf-btn small danger" data-remove-movie="' + esc(w.movie.Id) + '" data-remove-collection="' +
                                esc(w.collection.TmdbId) + '">Remove from collection</button>') +
                            '</span></div>';
                    }).join('') + '</div></details>';
            }
            return html;
        }

        // Libraries without a suitable metadata downloader (determined by the server during the search).
        function providerWarningsHtml(warnings) {
            if (!warnings || !warnings.length) { return ''; }
            return '<div class="gf-warnbox"><b>Check your library settings</b><ul>' +
                warnings.map(function (w) { return '<li>' + esc(w) + '</li>'; }).join('') +
                '</ul><span class="gf-muted">Enable the metadata downloader in the library settings, then start a new search.</span></div>';
        }

        function renderMovies() {
            var container = $('#gfMoviesList');
            var res = state.movies.results;
            if (!res || !res.ScanDateUtc) {
                $('#gfMoviesBadge').textContent = '0';
                $('#gfMoviesSummary').textContent = '';
                container.innerHTML = '<div class="gf-empty">No movie collection search yet. Choose "Movie collections" or "Both" above and start a search.</div>';
                state.visibleCollectionIds = [];
                return;
            }

            var f = movieFilters();
            var view = buildMoviesView(f);
            var totalMissing = view.list.reduce(function (n, x) { return n + x.missing.length; }, 0);

            $('#gfMoviesBadge').textContent = String(totalMissing);
            $('#gfMoviesBadge').className = 'gf-badge' + (totalMissing ? ' warn' : ' ok');
            $('#gfMoviesSummary').innerHTML =
                '<b>' + view.list.length + '</b> of ' + view.validCount + ' collections incomplete, <b>' + totalMissing + '</b> missing movies' +
                ' &middot; searched ' + esc(formatDateTime(res.ScanDateUtc)) +
                (res.LibraryNames && res.LibraryNames.length ? ' in ' + esc(res.LibraryNames.join(', ')) : '');

            var shown = view.list.slice(0, state.movies.limit);
            state.visibleCollectionIds = shown.map(function (x) { return x.c.TmdbId; });

            var html = providerWarningsHtml(res.Warnings);
            if (!view.list.length) {
                html = '<div class="gf-empty">Nothing missing with the current filters.</div>';
            }
            shown.forEach(function (x) { html += collectionCardHtml(x); });
            if (view.list.length > shown.length) {
                html += '<div class="gf-more"><button type="button" class="gf-btn" data-more="movies">Show more (' +
                    (view.list.length - shown.length) + ' remaining)</button></div>';
            }
            if (view.noData.length) {
                html += '<details class="gf-nodata"><summary>' + view.noData.length + ' collections could not be loaded from TMDB</summary><ul>' +
                    view.noData.map(function (c) {
                        return '<li>' + esc(c.Name) + ' <span class="gf-error">' + esc(c.Error) + '</span></li>';
                    }).join('') + '</ul></details>';
            }
            html += outdatedCollectionsHtml(view);
            container.innerHTML = html;
        }

        function collectionCardHtml(x) {
            var c = x.c;
            var open = state.movies.open.has(c.TmdbId);
            var poster = x.presentList.length ? imageUrl(x.presentList[0].Id, 120) : (x.missing[0] && x.missing[0].ThumbnailUrl) || '';

            var html = '<div class="gf-card' + (open ? ' open' : '') + (x.ignored ? ' ignored' : '') + '">' +
                '<div class="gf-card-head" data-toggle-collection="' + esc(c.TmdbId) + '">' +
                '<i class="md-icon gf-chevron" aria-hidden="true">chevron_right</i>' +
                '<img class="gf-poster" loading="lazy" src="' + esc(poster) + '" alt="" onerror="this.style.visibility=\'hidden\'" />' +
                '<div class="gf-card-title"><div class="gf-name">' + collectionNameHtml(c.TmdbId, c.Name) + '</div><div class="gf-sub">' +
                x.present + ' of ' + (x.present + x.missing.length) + ' movies in library</div></div>' +
                '<div class="gf-card-stats"><div class="gf-card-nums">' +
                '<span class="gf-badge warn" title="Missing movies">' + x.missing.length + '</span>' +
                '<div class="gf-progress ' + progressClass(x.pct) + '"><div style="width:' + x.pct + '%"></div></div>' +
                '<span class="gf-pct">' + x.pct + '%</span>' +
                '</div><div class="gf-card-actions">' +
                '<a class="gf-btn small link" style="text-decoration:none;" target="_blank" rel="noopener" href="https://www.themoviedb.org/collection/' +
                encodeURIComponent(c.TmdbId) + '" data-stop="1">TMDB</a>' +
                (arrMode('Radarr') === 'Api' ? '<button type="button" class="gf-btn small link" data-radarr-add-all="' + esc(c.TmdbId) + '" title="Add all shown missing movies of this collection to Radarr">Send all to Radarr</button>' : '') +
                '<button type="button" class="gf-btn small" data-ignore="' + esc(keys.collection(c.TmdbId)) + '" data-ignore-state="' + (x.ignored ? '0' : '1') + '">' +
                (x.ignored ? 'Unignore' : 'Ignore') + '</button>' +
                '</div></div></div>';

            if (open) {
                html += '<div class="gf-card-body">';
                x.missing.forEach(function (m) {
                    var ownIgnored = !!m.TmdbId && state.movies.ignored.has(keys.movie(m.TmdbId));
                    var ignored = isMovieIgnored(c, m, state.movies.ignored);
                    html += '<div class="gf-ep' + (ignored ? ' ignored' : '') + '"' + (m.Overview ? ' title="' + esc(m.Overview) + '"' : '') + '>' +
                        '<img class="gf-movie-thumb" loading="lazy" src="' + esc(m.ThumbnailUrl || '') + '" alt="" onerror="this.style.visibility=\'hidden\'" />' +
                        '<span class="gf-ep-name">' + esc(m.Name) + (m.Year ? ' <span class="gf-muted">(' + m.Year + ')</span>' : '') + '</span>' +
                        dateCellHtml(m.PremiereDate, 'unreleased') +
                        (m.TmdbId ? '<a class="gf-btn small link" style="text-decoration:none;" target="_blank" rel="noopener" href="https://www.themoviedb.org/movie/' +
                            encodeURIComponent(m.TmdbId) + '">TMDB</a>' : '') +
                        (m.TmdbId ? radarrButtonHtml(m) : '') +
                        (m.TmdbId ? '<button type="button" class="gf-btn small link" data-ignore="' + esc(keys.movie(m.TmdbId)) + '" data-ignore-state="' + (ownIgnored ? '0' : '1') + '">' +
                            (ownIgnored ? 'Unignore' : 'Ignore') + '</button>' : '') +
                        '</div>';
                });
                if (x.presentList.length) {
                    html += '<div class="gf-present"><span class="gf-muted">In library:</span> ' + movieLinks(x.presentList) + '</div>';
                }
                html += '</div>';
            }
            return html + '</div>';
        }

        // =================================================================
        // Export
        // =================================================================
        function exportResults(type, format) {
            var params = {
                Type: type,
                Format: format,
                IncludeIgnored: type === 'series' ? $('#gfSeriesIgnored').checked : $('#gfMoviesIgnored').checked,
                IncludeSpecials: $('#gfSpecials').checked,
                IncludeUnaired: type === 'series' ? $('#gfUnaired').checked : $('#gfUnreleased').checked
            };
            apiGet('GapFinder/Export', params).then(function (res) {
                downloadBase64(res.ContentBase64, res.FileName, res.ContentType);
            }).catch(function (err) {
                errorText(err).then(function (t) { embyAlert('Export failed: ' + t); });
            });
        }

        function downloadBase64(base64, fileName, mime) {
            var byteChars = atob(base64);
            var bytes = new Uint8Array(byteChars.length);
            for (var i = 0; i < byteChars.length; i++) { bytes[i] = byteChars.charCodeAt(i); }
            var url = URL.createObjectURL(new Blob([bytes], { type: mime }));
            var a = document.createElement('a');
            a.href = url;
            a.download = fileName;
            document.body.appendChild(a);
            a.click();
            setTimeout(function () { document.body.removeChild(a); URL.revokeObjectURL(url); }, 250);
        }

        // =================================================================
        // Theme (detect a light/dark dashboard from the background)
        // =================================================================
        function applyBackgroundAwareTheme() {
            try {
                var probe = page;
                var bg = null;
                while (probe) {
                    var color = window.getComputedStyle(probe).backgroundColor;
                    if (color && color !== 'transparent' && color !== 'rgba(0, 0, 0, 0)') { bg = color; break; }
                    probe = probe.parentElement;
                }
                var match = /rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)/.exec(bg || '');
                if (!match) { return; }
                var luminance = 0.299 * parseInt(match[1], 10) + 0.587 * parseInt(match[2], 10) + 0.114 * parseInt(match[3], 10);
                var root = page.querySelector('.gfRoot');
                if (root) { root.classList.toggle('gf-light-bg', luminance > 150); }
            } catch (e) { /* the dark default color scheme stays active */ }
        }

        // Emby's /configurationpage route (unlike /dashboard) does not call clearBackdrop - coming back from a
        // detail page (e.g. via "In library") would leave its backdrop image behind.
        function clearBackdrop() {
            try {
                Emby.importModule('./modules/backdrop/backdrop.js').then(function (m) {
                    (m.default || m).clear();
                });
            } catch (e) { /* backdrop module not available - nothing to do */ }
        }

        function applyBackgroundAwareThemeWithRetries() {
            applyBackgroundAwareTheme();
            setTimeout(applyBackgroundAwareTheme, 150);
            setTimeout(applyBackgroundAwareTheme, 600);
        }

        // =================================================================
        // Events
        // =================================================================
        function wireStaticControls() {
            page.querySelectorAll('#gfModeSeg button').forEach(function (b) {
                b.addEventListener('click', function () { state.mode = b.getAttribute('data-mode'); renderSearchOptions(); });
            });

            $('#gfLibraries').addEventListener('click', function (ev) {
                var chip = ev.target.closest('[data-lib]');
                if (!chip) { return; }
                var id = chip.getAttribute('data-lib');
                if (!id) {
                    state.selectedLibraryIds.clear();
                } else if (state.selectedLibraryIds.has(id)) {
                    state.selectedLibraryIds.delete(id);
                } else {
                    state.selectedLibraryIds.add(id);
                }
                renderSearchOptions();
            });

            $('#gfStartBtn').addEventListener('click', startScan);
            $('#gfClearBtn').addEventListener('click', clearResults);
            $('#gfCancelBtn').addEventListener('click', cancelScan);

            page.querySelectorAll('.gf-tab').forEach(function (t) {
                t.addEventListener('click', function () { switchTab(t.getAttribute('data-tab')); });
            });

            $('#gfOptSave').addEventListener('click', saveOptions);
            ['Sonarr', 'Radarr'].forEach(function (name) {
                arrPanel(name).querySelector('.gfOptOpen').addEventListener('click', function () { openArrUrl(name); });
                arrPanel(name).querySelector('.gfOptTest').addEventListener('click', function () { testArrConnection(name); });
                arrPanel(name).querySelectorAll('input[type="radio"]').forEach(function (r) {
                    r.addEventListener('change', function () { updateApiVisibility(name); });
                });
            });

            var rerenderSeries = function () { state.series.limit = PAGE_SIZE; renderSeries(); };
            var rerenderMovies = function () { state.movies.limit = PAGE_SIZE; renderMovies(); };
            ['#gfSeriesSearch'].forEach(function (s) { $(s).addEventListener('input', rerenderSeries); });
            ['#gfSeriesSort', '#gfSeriesStatus', '#gfSpecials', '#gfUnaired', '#gfOnlyFullSeasons', '#gfSeriesIgnored'].forEach(function (s) {
                $(s).addEventListener('change', rerenderSeries);
            });
            $('#gfMoviesSearch').addEventListener('input', rerenderMovies);
            ['#gfMoviesSort', '#gfUnreleased', '#gfMoviesIgnored'].forEach(function (s) {
                $(s).addEventListener('change', rerenderMovies);
            });

            page.querySelectorAll('[data-export]').forEach(function (b) {
                b.addEventListener('click', function () { exportResults(b.getAttribute('data-export'), b.getAttribute('data-format')); });
            });
            page.querySelectorAll('[data-expand]').forEach(function (b) {
                b.addEventListener('click', function () {
                    if (b.getAttribute('data-expand') === 'series') {
                        state.visibleSeriesIds.forEach(function (id) { state.series.open.add(id); });
                        renderSeries();
                    } else {
                        state.visibleCollectionIds.forEach(function (id) { state.movies.open.add(id); });
                        renderMovies();
                    }
                });
            });
            page.querySelectorAll('[data-collapse]').forEach(function (b) {
                b.addEventListener('click', function () {
                    if (b.getAttribute('data-collapse') === 'series') { state.series.open.clear(); renderSeries(); } else { state.movies.open.clear(); renderMovies(); }
                });
            });

            // Delegated clicks in the result lists (the cards are recreated on every render).
            // Series and collection links also open with the keyboard.
            [$('#gfSeriesList'), $('#gfMoviesList')].forEach(function (list) {
                list.addEventListener('keydown', function (ev) {
                    var upcomingToggle = ev.key === 'Enter' && ev.target.closest('.gf-upcoming-toggle');
                    if (upcomingToggle) { ev.preventDefault(); upcomingToggle.click(); return; }
                    var link = ev.key === 'Enter' && ev.target.closest('.gf-col-link[data-open-item]');
                    if (link) { ev.preventDefault(); ev.stopPropagation(); showItem(link.getAttribute('data-open-item')); }
                });
            });
            [$('#gfSeriesList'), $('#gfMoviesList')].forEach(function (list) {
                list.addEventListener('click', function (ev) {
                    var target = ev.target;

                    var summary = target.closest('details[data-details] > summary');
                    if (summary) {
                        // The click toggles only afterwards - so remember the upcoming state.
                        var details = summary.parentElement;
                        var name = details.getAttribute('data-details');
                        if (details.open) { state.movies.openDetails.delete(name); } else { state.movies.openDetails.add(name); }
                        return;
                    }
                    var sonarrBtn = target.closest('[data-sonarr-search]');
                    if (sonarrBtn) {
                        ev.stopPropagation();
                        searchInSonarr(sonarrBtn.getAttribute('data-sonarr-search'));
                        return;
                    }
                    var radarrAll = target.closest('[data-radarr-add-all]');
                    if (radarrAll) {
                        ev.stopPropagation();
                        var col = findCollection(radarrAll.getAttribute('data-radarr-add-all'));
                        var view = buildMoviesView(movieFilters()).list.find(function (x) { return col && x.c.TmdbId === col.TmdbId; });
                        if (view) {
                            addToRadarr(view.missing.filter(function (m) { return m.TmdbId && !isMovieIgnored(col, m, state.movies.ignored); })
                                .map(function (m) { return m.TmdbId; }), col.Name);
                        }
                        return;
                    }
                    var radarrBtn = target.closest('[data-radarr-add]');
                    if (radarrBtn) {
                        ev.stopPropagation();
                        addToRadarr([radarrBtn.getAttribute('data-radarr-add')], '');
                        return;
                    }
                    var removeRemovedBtn = target.closest('[data-remove-removed]');
                    if (removeRemovedBtn) {
                        ev.stopPropagation();
                        removeRemovedCollection(removeRemovedBtn.getAttribute('data-remove-removed'));
                        return;
                    }
                    var removeBtn = target.closest('[data-remove-movie]');
                    if (removeBtn) {
                        ev.stopPropagation();
                        removeFromCollection(removeBtn.getAttribute('data-remove-movie'), removeBtn.getAttribute('data-remove-collection'));
                        return;
                    }
                    var ignoreBtn = target.closest('[data-ignore]');
                    if (ignoreBtn) {
                        ev.stopPropagation();
                        setIgnored(ignoreBtn.getAttribute('data-ignore'), ignoreBtn.getAttribute('data-ignore-state') === '1');
                        return;
                    }
                    var openBtn = target.closest('[data-open-item]');
                    if (openBtn) {
                        ev.stopPropagation();
                        showItem(openBtn.getAttribute('data-open-item'));
                        return;
                    }
                    if (target.closest('a')) { return; }
                    var more = target.closest('[data-more]');
                    if (more) {
                        if (more.getAttribute('data-more') === 'series') { state.series.limit += PAGE_SIZE; renderSeries(); } else { state.movies.limit += PAGE_SIZE; renderMovies(); }
                        return;
                    }
                    var toggleUpcoming = target.closest('[data-toggle-upcoming]');
                    if (toggleUpcoming) {
                        var uid = toggleUpcoming.getAttribute('data-toggle-upcoming');
                        if (state.series.showUpcoming.has(uid)) { state.series.showUpcoming.delete(uid); } else { state.series.showUpcoming.add(uid); }
                        renderSeries();
                        return;
                    }
                    var toggleSeries = target.closest('[data-toggle-series]');
                    if (toggleSeries) {
                        var sid = toggleSeries.getAttribute('data-toggle-series');
                        if (state.series.open.has(sid)) { state.series.open.delete(sid); } else { state.series.open.add(sid); }
                        renderSeries();
                        return;
                    }
                    var toggleCollection = target.closest('[data-toggle-collection]');
                    if (toggleCollection) {
                        var cid = toggleCollection.getAttribute('data-toggle-collection');
                        if (state.movies.open.has(cid)) { state.movies.open.delete(cid); } else { state.movies.open.add(cid); }
                        renderMovies();
                    }
                });
            });
        }

        function loadAll() {
            loadOptions();
            Promise.all([apiGet('GapFinder/Libraries'), apiGet('GapFinder/Settings')]).then(function (r) {
                state.libraries = r[0] || [];
                var settings = r[1] || {};
                state.mode = settings.ScanSeries && settings.ScanMovies ? 'both' : (settings.ScanMovies ? 'movies' : 'series');
                state.selectedLibraryIds = new Set(settings.LibraryIds || []);
                $('#gfParallel').value = String(settings.MaxParallelRequests || 3);
                $('#gfVersion').textContent = settings.Version || '';
                renderSearchOptions();
            }).catch(function (err) {
                errorText(err).then(function (t) { setScanMessage('Could not load settings: ' + esc(t), true); });
            });

            apiGet('GapFinder/Status').then(function (status) {
                applyStatus(status);
                if (status.IsRunning) { startPolling(); }
            });

            loadSeries();
            loadMovies();
        }

        function checkAdminAndLoad() {
            ApiClient.getCurrentUser().then(function (user) {
                if (!user || !user.Policy || !user.Policy.IsAdministrator) {
                    var root = page.querySelector('.gfRoot');
                    if (root) { root.innerHTML = '<h1>Gap Finder</h1><p>This area is accessible to administrators only.</p>'; }
                    return;
                }
                loadAll();
            }).catch(loadAll);
        }

        wireStaticControls();
        clearBackdrop();
        applyBackgroundAwareThemeWithRetries();
        loadDateTimeLocale();
        checkAdminAndLoad();

        return {
            onResume: function () {
                clearBackdrop();
                applyBackgroundAwareThemeWithRetries();
                // The date setting may have changed in Emby while the page was in the background.
                loadDateTimeLocale();
                // Query the status at once: if a search was running when the page was left, pollStatus reloads the
                // results when it ends.
                if (state.status && state.status.IsRunning) {
                    pollStatus();
                    startPolling();
                }
            },
            onPause: function () {
                stopPolling();
            }
        };
    };
});
