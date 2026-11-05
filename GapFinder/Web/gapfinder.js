define(['baseView'], function (BaseView) {
    'use strict';

    // Loader of the Gap Finder page (data-controller="__plugin/gapfinderjs" in gapfinder.html).
    // This file and gapfinder.html must NOT change any more: Emby versions their URL only with the server
    // version, so browsers keep them cached after plugin updates. The loader therefore gets the build id from
    // the (never cached) API and loads the UI and the script with that id in the URL - every new plugin
    // version automatically gets fresh files.
    function View(view, params) {
        BaseView.apply(this, arguments);
        var instance = this;
        var mount = view.querySelector('.gfMount');

        function resourceUrl(name, buildId) {
            return ApiClient.getUrl('web/ConfigurationPage', { name: name, b: buildId });
        }

        function showError(err) {
            if (mount) {
                mount.innerHTML = '<h1>Gap Finder</h1><p>The page could not be loaded: ' +
                    String((err && err.message) || err || 'unknown error').replace(/</g, '&lt;') + '</p>';
            }
        }

        this.appReady = ApiClient.getJSON(ApiClient.getUrl('GapFinder/Settings')).then(function (settings) {
            var buildId = settings.BuildId || settings.Version || String(Date.now());
            return fetch(resourceUrl('gapfinderui', buildId)).then(function (response) {
                if (!response.ok) { throw new Error('HTTP ' + response.status); }
                return response.text();
            }).then(function (html) {
                mount.innerHTML = html;
                return new Promise(function (resolve, reject) {
                    require([resourceUrl('gapfinderapp', buildId)], resolve, reject);
                });
            });
        }).then(function (App) {
            instance.app = App(view);
            return instance.app;
        }).catch(function (err) {
            showError(err);
        });
    }

    View.prototype = Object.create(BaseView.prototype);
    View.prototype.constructor = View;

    View.prototype.onResume = function () {
        BaseView.prototype.onResume.apply(this, arguments);
        // On first display App(view) runs everything itself; afterwards on every re-display.
        if (this.app) { this.app.onResume(); }
    };

    View.prototype.onPause = function () {
        BaseView.prototype.onPause.apply(this, arguments);
        if (this.app) { this.app.onPause(); }
    };

    return View;
});
