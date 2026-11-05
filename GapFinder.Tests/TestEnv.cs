using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using NSubstitute;

namespace GapFinder.Tests
{
    /// <summary>
    /// Plugin instance with its own temporary data folder. Plugin.Instance is static, so all tests run one after
    /// another (CollectionBehavior in ArrSecurityTests.cs).
    /// </summary>
    public sealed class TestEnv : IDisposable
    {
        public string Root { get; }
        public string DataPath => Path.Combine(Root, "data");
        public Plugin Plugin { get; }

        public TestEnv()
        {
            Root = Path.Combine(Path.GetTempPath(), "gf-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "data"));
            Directory.CreateDirectory(Path.Combine(Root, "plugins", "configurations"));

            var paths = Substitute.For<IApplicationPaths>();
            paths.DataPath.Returns(Path.Combine(Root, "data"));
            paths.PluginsPath.Returns(Path.Combine(Root, "plugins"));
            paths.PluginConfigurationsPath.Returns(Path.Combine(Root, "plugins", "configurations"));
            paths.ProgramDataPath.Returns(Root);
            paths.CachePath.Returns(Path.Combine(Root, "cache"));
            paths.TempDirectory.Returns(Path.Combine(Root, "temp"));

            var xml = Substitute.For<IXmlSerializer>();
            xml.DeserializeFromFile(Arg.Any<Type>(), Arg.Any<string>()).Returns(_ => new PluginConfiguration());

            Plugin = new Plugin(paths, xml, Logs());
            // Emby sets this when loading the DLL; it determines ConfigurationFilePath ("GapFinder.xml") among others.
            Plugin.SetAttributes(Path.Combine(Root, "plugins", "GapFinder.dll"), Path.Combine(Root, "plugins", "GapFinder"), new Version(1, 0, 0, 0));
            SetUninstalling(false);

            // BaseItem.ContainingFolderPath uses Emby's static IFileSystem.
            var fs = Substitute.For<MediaBrowser.Model.IO.IFileSystem>();
            fs.GetDirectoryName(Arg.Any<string>()).Returns(ci => Path.GetDirectoryName(ci.Arg<string>()));
            foreach (var p in typeof(MediaBrowser.Controller.Entities.BaseItem).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                if (p.PropertyType == typeof(MediaBrowser.Model.IO.IFileSystem) && p.CanWrite)
                {
                    p.SetValue(null, fs);
                }
            }
        }

        public static ILogManager Logs()
        {
            var logManager = Substitute.For<ILogManager>();
            logManager.GetLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
            return logManager;
        }

        /// <summary>Plugin.IsUninstalling has a private setter - reset it via reflection for tests.</summary>
        public static void SetUninstalling(bool value) =>
            typeof(Plugin).GetProperty(nameof(Plugin.IsUninstalling)).SetValue(null, value);

        public void Dispose()
        {
            SetUninstalling(false);
            try
            {
                foreach (var f in Directory.GetFiles(Root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                }
                Directory.Delete(Root, true);
            }
            catch
            {
                // Temp folder - cleanup errors do not matter for the test result.
            }
        }
    }

    /// <summary>Minimal HTTP server on 127.0.0.1 (free port) for Sonarr/Radarr mocks.</summary>
    public sealed class MockHttp : IDisposable
    {
        private readonly HttpListener _listener = new HttpListener();
        private readonly Func<HttpListenerContext, Task> _handler;
        public List<(string Path, string Host, string ApiKey)> Requests { get; } = new List<(string, string, string)>();
        public int Port { get; }
        public string Url => "http://127.0.0.1:" + Port;

        public MockHttp(Func<HttpListenerContext, Task> handler)
        {
            _handler = handler;
            Port = FreePort();
            _listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
            _listener.Prefixes.Add("http://localhost:" + Port + "/");
            _listener.Start();
            Task.Run(Loop);
        }

        private async Task Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }
                lock (Requests)
                {
                    Requests.Add((ctx.Request.Url.PathAndQuery, ctx.Request.Headers["Host"], ctx.Request.Headers["X-Api-Key"]));
                }
                try { await _handler(ctx); }
                catch { /* client aborted */ }
                finally { try { ctx.Response.Close(); } catch { } }
            }
        }

        public static async Task Json(HttpListenerContext ctx, string json, int status = 200)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public void Dispose()
        {
            try { _listener.Stop(); _listener.Close(); } catch { }
        }
    }
}
