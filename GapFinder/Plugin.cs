using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using GapFinder.Data;

namespace GapFinder
{
    /// <summary>
    /// Plugin entry point: registers the dashboard page and provides the data paths.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        public static readonly Guid PluginGuid = new Guid("5f3c9b7e-2a41-4d8e-9c6b-1e7a0d4f8b23");

        private readonly GapFinderLogger _logger;

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogManager logManager)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            _logger = new GapFinderLogger(logManager);
        }

        public static Plugin Instance { get; private set; }

        public override Guid Id => PluginGuid;

        public override string Name => "Gap Finder";

        public override string Description =>
            "Finds missing episodes and seasons of series and missing movies of movie collections - with filters, ignore list and CSV/JSON export (administrators only).";

        /// <summary>
        /// Folder for search results, the ignore list and NFO backups (".../data/GapFinder"). Not
        /// BasePlugin.DataFolderPath, which points to the plugins folder in this SDK version.
        /// Created only when something is written (ResultStore/NfoCleaner); otherwise merely opening the page
        /// after an uninstall would recreate it, since the plugin stays loaded until the next restart.
        /// </summary>
        public string DataFolder => Path.Combine(ApplicationPaths.DataPath, "GapFinder");

        /// <summary>
        /// Set as soon as Emby uninstalls the plugin. From then on nothing is written (results, ignore list,
        /// configuration or NFO backups), so no leftovers are created.
        /// </summary>
        public static bool IsUninstalling { get; private set; }

        public string SeriesResultsFilePath => Path.Combine(DataFolder, "series-results.json");

        public string MovieResultsFilePath => Path.Combine(DataFolder, "movie-results.json");

        public string IgnoreListFilePath => Path.Combine(DataFolder, "ignored.json");

        /// <summary>Provider answers reused between searches (see Data.ProviderCache).</summary>
        public string ProviderCacheFilePath => Path.Combine(DataFolder, "provider-cache.json");

        public string DisplayVersion
        {
            get
            {
                var v = GetType().Assembly.GetName().Version;
                return string.Format("v{0}.{1}.{2}.{3}", v.Major, v.Minor, v.Build, v.Revision);
            }
        }

        /// <summary>
        /// Emby only removes the DLL on uninstall; results, ignore list, NFO backups and the configuration XML
        /// are removed here. Errors never block the uninstall.
        /// </summary>
        public override void OnUninstalling()
        {
            base.OnUninstalling();
            IsUninstalling = true;

            // A running search would otherwise save its results after the deletion and recreate the folder.
            try
            {
                Scanning.ScanManager.Cancel();
                for (var i = 0; i < 100 && Scanning.ScanManager.GetStatus().IsRunning; i++)
                {
                    System.Threading.Thread.Sleep(100);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("[GapFinder] Could not cancel the running search on uninstall: {0}", ex.Message);
            }

            try
            {
                var folder = DataFolder;
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("[GapFinder] Could not delete the plugin data on uninstall: {0}", ex.Message);
            }

            try
            {
                // Emby names the XML after the DLL (e.g. "GapFinder (1).xml"), so the name is not fixed.
                var configXmlPath = ConfigurationFilePath;
                if (File.Exists(configXmlPath))
                {
                    File.Delete(configXmlPath);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("[GapFinder] Could not delete the configuration XML on uninstall: {0}", ex.Message);
            }
        }

        public Stream GetThumbImage() =>
            GetType().Assembly.GetManifestResourceStream(GetType().Namespace + ".thumb.webp");

        public ImageFormat ThumbImageFormat => ImageFormat.Webp;

        /// <summary>
        /// Changes with every build (assembly MVID). The page loader appends it to the URLs of the UI and the
        /// script, so browsers never serve an old version from their cache after an update.
        /// </summary>
        public string BuildId => GetType().Assembly.ManifestModule.ModuleVersionId.ToString("N");

        public IEnumerable<PluginPageInfo> GetPages()
        {
            // "gapfinderjs" must match data-controller="__plugin/gapfinderjs" in gapfinder.html.
            // gapfinder.html and gapfinder.js are a fixed loader; the actual page is "gapfinderui" and
            // "gapfinderapp" (see gapfinder.js). MenuSection stays empty so the entry appears under
            // "Advanced" instead of "Emby Server".
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "gapfinder",
                    EmbeddedResourcePath = GetType().Namespace + ".Web.gapfinder.html",
                    EnableInMainMenu = true,
                    MenuIcon = "playlist_remove",
                    DisplayName = "Gap Finder"
                },
                new PluginPageInfo
                {
                    Name = "gapfinderjs",
                    EmbeddedResourcePath = GetType().Namespace + ".Web.gapfinder.js"
                },
                new PluginPageInfo
                {
                    Name = "gapfinderui",
                    EmbeddedResourcePath = GetType().Namespace + ".Web.gapfinder-ui.html"
                },
                new PluginPageInfo
                {
                    Name = "gapfinderapp",
                    EmbeddedResourcePath = GetType().Namespace + ".Web.gapfinder-app.js"
                }
            };
        }
    }
}
