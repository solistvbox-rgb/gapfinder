using System;
using MediaBrowser.Model.Logging;

namespace GapFinder.Data
{
    /// <summary>Log levels of the "Logging" setting in the Options tab (PluginConfiguration.LogLevel).</summary>
    public static class LogLevels
    {
        /// <summary>Everything: info, warnings and errors.</summary>
        public const string All = "All";
        /// <summary>Errors and warnings (e.g. provider problems).</summary>
        public const string Errors = "Errors";
        public const string Nothing = "Nothing";

        public const string Default = All;

        /// <summary>Unknown or empty values become the default.</summary>
        public static string Normalize(string value)
        {
            foreach (var level in new[] { All, Errors, Nothing })
            {
                if (string.Equals(value, level, StringComparison.OrdinalIgnoreCase))
                {
                    return level;
                }
            }
            return Default;
        }
    }

    /// <summary>
    /// Wrapper around Emby's ILogger that checks every entry against the configured level (takes effect at
    /// once, without a restart). Not an ILogger implementation, because the interface differs between Emby versions.
    /// </summary>
    public class GapFinderLogger
    {
        private readonly ILogger _inner;

        public GapFinderLogger(ILogManager logManager)
        {
            _inner = logManager.GetLogger("GapFinder");
        }

        private static string CurrentLevel => LogLevels.Normalize(Plugin.Instance?.Configuration?.LogLevel);

        public void Info(string message, params object[] args)
        {
            if (CurrentLevel == LogLevels.All)
            {
                _inner.Info(message, args);
            }
        }

        public void Warn(string message, params object[] args)
        {
            if (CurrentLevel != LogLevels.Nothing)
            {
                _inner.Warn(message, args);
            }
        }

        public void Error(string message, params object[] args)
        {
            if (CurrentLevel != LogLevels.Nothing)
            {
                _inner.Error(message, args);
            }
        }

        public void ErrorException(string message, Exception exception, params object[] args)
        {
            if (CurrentLevel != LogLevels.Nothing)
            {
                _inner.ErrorException(message, exception, args);
            }
        }
    }
}
