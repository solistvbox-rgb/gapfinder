using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using GapFinder.Api;
using GapFinder.Data;
using NSubstitute;
using Xunit;

namespace GapFinder.Tests
{
    public class SecretProtectorTests : IDisposable
    {
        private readonly TestEnv _env = new TestEnv();
        private string KeyFile => Path.Combine(_env.Plugin.DataFolder, "secret.key");

        public void Dispose() => _env.Dispose();

        [Fact]
        public void Roundtrip_with_random_iv()
        {
            var a = SecretProtector.Protect("abc123");
            var b = SecretProtector.Protect("abc123");
            Assert.StartsWith(SecretProtector.Prefix, a);
            Assert.NotEqual(a, b);
            Assert.DoesNotContain("abc123", a);
            Assert.Equal("abc123", SecretProtector.Unprotect(a));
            Assert.Equal(64, File.ReadAllBytes(KeyFile).Length);
        }

        [Fact]
        public void Every_tampered_byte_is_detected()
        {
            var value = SecretProtector.Protect("abc123");
            var raw = Convert.FromBase64String(value.Substring(SecretProtector.Prefix.Length));
            for (var i = 0; i < raw.Length; i++)
            {
                var copy = (byte[])raw.Clone();
                copy[i] ^= 0x01;
                Assert.Null(SecretProtector.Unprotect(SecretProtector.Prefix + Convert.ToBase64String(copy)));
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("plaintext-key")]
        [InlineData("emby-encrypted:v1:")]
        [InlineData("emby-encrypted:v1:!!!notbase64")]
        [InlineData("emby-encrypted:v1:AAAA")]
        public void Garbage_is_rejected_without_exception(string value)
        {
            SecretProtector.Protect("init");
            Assert.Null(SecretProtector.Unprotect(value));
        }

        [Fact]
        public void Missing_or_damaged_key_file_makes_value_unreadable()
        {
            var value = SecretProtector.Protect("abc123");
            File.WriteAllBytes(KeyFile, new byte[10]);
            Assert.Null(SecretProtector.Unprotect(value));
            File.Delete(KeyFile);
            Assert.Null(SecretProtector.Unprotect(value));
        }

        [Fact]
        public void Concurrent_first_use_creates_a_single_key()
        {
            var values = new string[32];
            Parallel.For(0, values.Length, i => values[i] = SecretProtector.Protect("k" + i));
            for (var i = 0; i < values.Length; i++)
            {
                Assert.Equal("k" + i, SecretProtector.Unprotect(values[i]));
            }
        }

        [Fact]
        public void No_key_is_written_while_uninstalling()
        {
            TestEnv.SetUninstalling(true);
            Assert.Throws<InvalidOperationException>(() => SecretProtector.Protect("abc"));
            Assert.False(Directory.Exists(_env.Plugin.DataFolder));
        }
    }

    public class NfoCleanerTests : IDisposable
    {
        private readonly TestEnv _env = new TestEnv();
        private readonly ILibraryMonitor _monitor = Substitute.For<ILibraryMonitor>();

        public void Dispose() => _env.Dispose();

        private NfoCleaner Cleaner() => new NfoCleaner(_monitor, new GapFinderLogger(TestEnv.Logs()));

        private Movie MovieIn(string folderName, out string nfo, byte[] content)
        {
            var folder = Path.Combine(_env.Root, "media", folderName);
            Directory.CreateDirectory(folder);
            var video = Path.Combine(folder, folderName + ".mkv");
            File.WriteAllBytes(video, new byte[0]);
            nfo = Path.Combine(folder, folderName + ".nfo");
            File.WriteAllBytes(nfo, content);
            return new Movie { Path = video, IsInMixedFolder = true };
        }

        private static byte[] Utf8(string s, bool bom) =>
            (bom ? new byte[] { 0xEF, 0xBB, 0xBF } : new byte[0]).Concat(new UTF8Encoding(false).GetBytes(s)).ToArray();

        [Fact]
        public void Removes_only_matching_set_and_keeps_bom_crlf_and_rest()
        {
            var xml = "<?xml version=\"1.0\"?>\r\n<movie>\r\n  <title>Ä &amp; Ö</title>\r\n" +
                      "  <set tmdbcolid=\"10\">\r\n    <name>Wrong</name>\r\n  </set>\r\n" +
                      "  <set tmdbcolid=\"20\"><name>Right</name></set>\r\n" +
                      "  <settings>keep</settings>\r\n</movie>\r\n";
            var movie = MovieIn("m1", out var nfo, Utf8(xml, true));

            var result = Cleaner().RemoveCollection(movie, "10", "Wrong").Single();

            Assert.Equal("removed", result.Status);
            var expected = "<?xml version=\"1.0\"?>\r\n<movie>\r\n  <title>Ä &amp; Ö</title>\r\n" +
                           "  <set tmdbcolid=\"20\"><name>Right</name></set>\r\n" +
                           "  <settings>keep</settings>\r\n</movie>\r\n";
            Assert.Equal(Utf8(expected, true), File.ReadAllBytes(nfo));
            Assert.Equal(Utf8(xml, true), File.ReadAllBytes(result.BackupPath));
            Assert.Contains(nfo, File.ReadAllText(Path.Combine(Path.GetDirectoryName(result.BackupPath), "backup-index.txt")));
            Assert.False(File.Exists(nfo + ".gapfinder.tmp"));
        }

        [Fact]
        public void Old_entries_without_id_match_by_name_including_cdata_and_entities()
        {
            var xml = "<movie><set><name><![CDATA[Tom & Jerry]]></name></set><set>Tom &amp; Jerry</set><set>Other</set></movie>";
            var movie = MovieIn("m2", out var nfo, Utf8(xml, false));
            Cleaner().RemoveCollection(movie, "77", "Tom & Jerry");
            Assert.Equal("<movie><set>Other</set></movie>", File.ReadAllText(nfo));
        }

        [Fact]
        public void No_match_leaves_file_untouched_and_creates_no_backup()
        {
            var xml = "<movie><set tmdbcolid=\"5\"><name>A</name></set></movie>";
            var movie = MovieIn("m3", out var nfo, Utf8(xml, false));
            var before = File.GetLastWriteTimeUtc(nfo);
            var result = Cleaner().RemoveCollection(movie, "6", "B").Single();
            Assert.Equal("nomatch", result.Status);
            Assert.Equal(xml, File.ReadAllText(nfo));
            Assert.Equal(before, File.GetLastWriteTimeUtc(nfo));
            Assert.False(Directory.Exists(Path.Combine(_env.Plugin.DataFolder, "nfo-backups")));
        }

        [Theory]
        [InlineData("")]
        [InlineData("<movie><set tmdbcolid=\"5\"><name>A</name>")]           // truncated
        [InlineData("not xml at all <set")]
        [InlineData("<movie><set tmdbcolid='5'/><title>x</title></movie>")]   // self-closing, single quotes
        public void Invalid_or_unusual_xml_does_not_throw(string xml)
        {
            var movie = MovieIn("m4", out var nfo, Utf8(xml, false));
            var result = Cleaner().RemoveCollection(movie, "5", "A").Single();
            Assert.NotEqual("error", result.Status);
            if (result.Status == "removed")
            {
                Assert.Equal(xml, File.ReadAllText(result.BackupPath));
            }
        }

        [Fact]
        public void Read_only_nfo_is_reported_and_unchanged()
        {
            var xml = "<movie><set tmdbcolid=\"5\"><name>A</name></set></movie>";
            var movie = MovieIn("m5", out var nfo, Utf8(xml, false));
            File.SetAttributes(nfo, FileAttributes.ReadOnly);
            var result = Cleaner().RemoveCollection(movie, "5", "A").Single();
            Assert.Equal("error", result.Status);
            Assert.Equal(xml, File.ReadAllText(nfo));
            Assert.False(File.Exists(nfo + ".gapfinder.tmp"));
            _monitor.Received().ReportFileSystemChangeComplete(nfo, false);
        }

        [Fact]
        public void Backups_of_files_with_the_same_name_never_overwrite_each_other()
        {
            // Typical case: "Remove collection" cleans up many movies with "movie.nfo" within the same millisecond.
            var backup = typeof(NfoCleaner).GetMethod("Backup", BindingFlags.NonPublic | BindingFlags.Static);
            var paths = new List<string>();
            for (var i = 0; i < 50; i++)
            {
                paths.Add((string)backup.Invoke(null, new object[] { Path.Combine(_env.Root, "media", "f" + i, "movie.nfo"), Encoding.ASCII.GetBytes("content-" + i) }));
            }
            Assert.Equal(paths.Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            for (var i = 0; i < 50; i++)
            {
                Assert.Equal("content-" + i, File.ReadAllText(paths[i]));
            }
        }
    }

    public class UninstallTests : IDisposable
    {
        private readonly TestEnv _env = new TestEnv();

        public void Dispose() => _env.Dispose();

        [Fact]
        public void Uninstall_deletes_data_folder_and_config_but_nothing_else()
        {
            var data = _env.Plugin.DataFolder;
            Directory.CreateDirectory(Path.Combine(data, "nfo-backups", "20260101"));
            File.WriteAllText(Path.Combine(data, "nfo-backups", "20260101", "backup-index.txt"), "x");
            File.WriteAllText(Path.Combine(data, "secret.key"), "x");
            File.WriteAllText(_env.Plugin.ConfigurationFilePath, "<x/>");
            var sibling = Path.Combine(_env.DataPath, "GapFinderOther");
            Directory.CreateDirectory(sibling);
            File.WriteAllText(Path.Combine(sibling, "keep.txt"), "keep");

            _env.Plugin.OnUninstalling();

            Assert.False(Directory.Exists(data));
            Assert.False(File.Exists(_env.Plugin.ConfigurationFilePath));
            Assert.True(File.Exists(Path.Combine(sibling, "keep.txt")));
            Assert.True(Plugin.IsUninstalling);
        }

        [Fact]
        public void Uninstall_does_not_follow_a_junction_out_of_the_data_folder()
        {
            var data = _env.Plugin.DataFolder;
            Directory.CreateDirectory(data);
            File.WriteAllText(_env.Plugin.ConfigurationFilePath, "<x/>");
            var sibling = Path.Combine(_env.DataPath, "GapFinderOther");
            Directory.CreateDirectory(sibling);
            File.WriteAllText(Path.Combine(sibling, "keep.txt"), "keep");
            var outside = Path.Combine(_env.Root, "outside");
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
            var junction = Path.Combine(data, "link");
            var mk = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + junction + "\" \"" + outside + "\"") { CreateNoWindow = true, UseShellExecute = false });
            mk.WaitForExit();
            Assert.True(Directory.Exists(junction), "junction could not be created");

            _env.Plugin.OnUninstalling();

            // The junction target stays untouched; the delete error does not block the uninstall.
            Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
            Assert.True(File.Exists(Path.Combine(sibling, "keep.txt")));
            Assert.False(File.Exists(_env.Plugin.ConfigurationFilePath));
        }
    }

    public class CsvTests
    {
        [Theory]
        [InlineData("=HYPERLINK(\"http://x\")", "\"'=HYPERLINK(\"\"http://x\"\")\"")]
        [InlineData("+1", "'+1")]
        [InlineData("-1", "'-1")]
        [InlineData("@SUM(A1)", "'@SUM(A1)")]
        [InlineData("\tx", "'\tx")]
        [InlineData("a;b", "\"a;b\"")]
        [InlineData("line\nbreak", "\"line\nbreak\"")]
        [InlineData("normal", "normal")]
        public void Formulas_are_neutralised_and_values_quoted(string value, string expected)
        {
            Assert.Equal(expected, CsvUtil.Escape(value, ';'));
        }
    }
}
