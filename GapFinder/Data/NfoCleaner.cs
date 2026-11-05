using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;

namespace GapFinder.Data
{
    /// <summary>
    /// Removes the &lt;set&gt; entry of a collection from a movie's NFO. The NFO paths match Emby's NFO plugin
    /// (MovieNfoSaver.GetMovieSavePaths). Only the matching &lt;set&gt; block is cut out - the rest of the file
    /// stays identical character by character (no reformatting) and the encoding (UTF-8 with/without BOM) is
    /// kept. A backup is written to the plugin data folder first.
    /// </summary>
    public class NfoCleaner
    {
        // <set ...>...</set> or <set .../>, including the indentation before and the line break after it.
        private static readonly Regex SetBlock = new Regex(
            @"[ \t]*<set\b(?<attrs>[^>]*?)(?:/>|>(?<inner>.*?)</set\s*>)[ \t]*(?:\r?\n)?",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TmdbAttr = new Regex(@"\btmdbcolid\s*=\s*[""'](?<id>[^""']*)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex NameElement = new Regex(@"<name\b[^>]*>(?<name>.*?)</name\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly ILibraryMonitor _libraryMonitor;
        private readonly GapFinderLogger _logger;

        public NfoCleaner(ILibraryMonitor libraryMonitor, GapFinderLogger logger)
        {
            _libraryMonitor = libraryMonitor;
            _logger = logger;
        }

        public class FileResult
        {
            public string Path { get; set; }
            /// <summary>"removed", "nomatch" or "error".</summary>
            public string Status { get; set; }
            public string Message { get; set; }
            public string BackupPath { get; set; }
        }

        /// <summary>Existing NFO files of the movie (same locations as Emby's NFO plugin).</summary>
        public static List<string> GetExistingNfoPaths(Movie movie)
        {
            var candidates = new List<string>();
            var folder = movie.ContainingFolderPath;
            var isDvd = string.Equals(movie.Container, "dvd", StringComparison.OrdinalIgnoreCase);
            var isBluray = string.Equals(movie.Container, "bluray", StringComparison.OrdinalIgnoreCase);

            if ((isDvd || isBluray) && !string.IsNullOrEmpty(folder))
            {
                candidates.Add(isDvd ? Path.Combine(folder, "VIDEO_TS", "VIDEO_TS.nfo") : Path.Combine(folder, "BDMV", "index.nfo"));
                candidates.Add(Path.Combine(folder, Path.GetFileName(folder.TrimEnd('\\', '/')) + ".nfo"));
            }
            else if (!string.IsNullOrEmpty(movie.Path) && movie.Path.IndexOf("://", StringComparison.Ordinal) < 0)
            {
                candidates.Add(Path.ChangeExtension(movie.Path, ".nfo"));
                if (!movie.IsInMixedFolder && !string.IsNullOrEmpty(folder))
                {
                    candidates.Add(Path.Combine(folder, "movie.nfo"));
                }
            }

            return candidates
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(p =>
                {
                    try { return File.Exists(p); }
                    catch { return false; }
                })
                .ToList();
        }

        /// <summary>Removes the &lt;set&gt; entries of this collection from all NFOs of the movie.</summary>
        public List<FileResult> RemoveCollection(Movie movie, string collectionTmdbId, string collectionName)
        {
            return GetExistingNfoPaths(movie).Select(path => RemoveCollection(path, collectionTmdbId, collectionName)).ToList();
        }

        private FileResult RemoveCollection(string path, string collectionTmdbId, string collectionName)
        {
            var result = new FileResult { Path = path };
            try
            {
                var bytes = File.ReadAllBytes(path);
                var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                var text = new UTF8Encoding(false).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));

                var removedCount = 0;
                var cleaned = SetBlock.Replace(text, match =>
                {
                    if (!IsMatch(match, collectionTmdbId, collectionName))
                    {
                        return match.Value;
                    }
                    removedCount++;
                    return string.Empty;
                });

                if (removedCount == 0)
                {
                    result.Status = "nomatch";
                    return result;
                }

                result.BackupPath = Backup(path, bytes);

                var body = new UTF8Encoding(false).GetBytes(cleaned);
                var output = hasBom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray() : body;

                // Write a temp file next to the NFO first, then replace the NFO. Tell Emby beforehand so the file change
                // does not trigger a refresh of the movie.
                var tmp = path + ".gapfinder.tmp";
                _libraryMonitor.ReportFileSystemChangeBeginning(path);
                try
                {
                    File.WriteAllBytes(tmp, output);
                    File.Copy(tmp, path, true);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tmp))
                        {
                            File.Delete(tmp);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn("[GapFinder] Temp file {0} could not be deleted: {1}", tmp, ex.Message);
                    }
                    _libraryMonitor.ReportFileSystemChangeComplete(path, false);
                }

                _logger.Info("[GapFinder] NFO cleaned: {0} ({1} set entr(y/ies) of collection {2} removed, backup: {3})",
                    path, removedCount, collectionTmdbId, result.BackupPath);
                result.Status = "removed";
            }
            catch (Exception ex)
            {
                _logger.Error("[GapFinder] NFO {0} could not be cleaned: {1}", path, ex.Message);
                result.Status = "error";
                result.Message = ex.Message;
            }
            return result;
        }

        /// <summary>Matches by tmdbcolid; old entries without an id are matched by name.</summary>
        private static bool IsMatch(Match match, string collectionTmdbId, string collectionName)
        {
            var idMatch = TmdbAttr.Match(match.Groups["attrs"].Value);
            if (idMatch.Success && !string.IsNullOrWhiteSpace(idMatch.Groups["id"].Value))
            {
                return string.Equals(idMatch.Groups["id"].Value.Trim(), collectionTmdbId, StringComparison.OrdinalIgnoreCase);
            }

            var inner = match.Groups["inner"].Value;
            var nameMatch = NameElement.Match(inner);
            var name = WebUtility.HtmlDecode((nameMatch.Success ? nameMatch.Groups["name"].Value : inner).Trim());
            if (name.StartsWith("<![CDATA[", StringComparison.Ordinal) && name.EndsWith("]]>", StringComparison.Ordinal))
            {
                name = name.Substring(9, name.Length - 12).Trim();
            }
            return !string.IsNullOrEmpty(collectionName) && string.Equals(name, collectionName.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Backup in data/GapFinder/nfo-backups/yyyyMMdd/HHmmss-fff_&lt;file name&gt;; the original path is recorded
        /// in backup-index.txt in the same folder.
        /// </summary>
        private static string Backup(string path, byte[] original)
        {
            var now = DateTime.Now;
            var folder = System.IO.Path.Combine(Plugin.Instance.DataFolder, "nfo-backups", now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            // Unique name: "Remove collection" often backs up several "movie.nfo" in the same millisecond. CreateNew
            // never overwrites an existing backup; the next counter is used instead.
            var stamp = now.ToString("HHmmss-fff", CultureInfo.InvariantCulture);
            var fileName = System.IO.Path.GetFileName(path);
            string backupPath;
            for (var n = 0; ; n++)
            {
                backupPath = System.IO.Path.Combine(folder, stamp + (n == 0 ? string.Empty : "-" + n.ToString(CultureInfo.InvariantCulture)) + "_" + fileName);
                try
                {
                    using (var stream = new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write))
                    {
                        stream.Write(original, 0, original.Length);
                    }
                    break;
                }
                catch (IOException) when (File.Exists(backupPath) && n < 10000)
                {
                    // Name taken - next counter.
                }
            }
            File.AppendAllText(System.IO.Path.Combine(folder, "backup-index.txt"),
                System.IO.Path.GetFileName(backupPath) + "\t" + path + Environment.NewLine, new UTF8Encoding(false));
            return backupPath;
        }
    }
}
