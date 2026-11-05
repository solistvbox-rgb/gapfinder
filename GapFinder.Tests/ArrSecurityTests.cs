using System;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using GapFinder.Api;
using GapFinder.Arr;
using GapFinder.Data;
using NSubstitute;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace GapFinder.Tests
{
    /// <summary>Outgoing connections to Sonarr/Radarr: redirects, key forwarding, sizes, cancellation, URL validation.</summary>
    public class ArrSecurityTests : IDisposable
    {
        private const string TestKey = "TESTKEY-not-a-real-secret-0001";
        private const string StatusJson = "{\"appName\":\"Sonarr\",\"version\":\"9.9.9-mock\"}";
        private readonly TestEnv _env = new TestEnv();

        public void Dispose() => _env.Dispose();

        [Fact]
        public async Task Redirect_to_other_host_does_not_forward_api_key()
        {
            using (var receiver = new MockHttp(ctx => MockHttp.Json(ctx, StatusJson)))
            using (var redirector = new MockHttp(ctx =>
            {
                ctx.Response.StatusCode = 302;
                ctx.Response.RedirectLocation = "http://localhost:" + receiver.Port + ctx.Request.Url.PathAndQuery;
                return Task.CompletedTask;
            }))
            {
                var client = new ArrClient("Sonarr", redirector.Url, TestKey, TestJson.Create());
                try
                {
                    await client.GetAsync<ArrSystemStatus>("system/status", CancellationToken.None);
                }
                catch (ArrException)
                {
                    // The redirect is reported instead of followed.
                }

                Assert.DoesNotContain(receiver.Requests, r => r.ApiKey == TestKey);
            }
        }

        [Fact]
        public async Task Redirect_is_reported_with_target_instead_of_followed()
        {
            using (var redirector = new MockHttp(ctx =>
            {
                ctx.Response.StatusCode = 301;
                ctx.Response.RedirectLocation = "https://sonarr.example.test/api/v3/system/status";
                return Task.CompletedTask;
            }))
            {
                var client = new ArrClient("Sonarr", redirector.Url, TestKey, TestJson.Create());
                var ex = await Assert.ThrowsAsync<ArrException>(() => client.GetAsync<ArrSystemStatus>("system/status", CancellationToken.None));
                Assert.Contains("redirect", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("https://sonarr.example.test", ex.Message);
            }
        }

        [Fact]
        public async Task Oversized_response_is_rejected()
        {
            using (var big = new MockHttp(async ctx =>
            {
                var chunk = Encoding.ASCII.GetBytes(new string(' ', 65536));
                ctx.Response.ContentType = "application/json";
                ctx.Response.SendChunked = true;
                for (var i = 0; i < 40 * 16; i++) // 40 MB
                {
                    await ctx.Response.OutputStream.WriteAsync(chunk, 0, chunk.Length);
                }
            }))
            {
                var client = new ArrClient("Sonarr", big.Url, TestKey, TestJson.Create());
                var ex = await Assert.ThrowsAsync<ArrException>(() => client.GetAsync<ArrSystemStatus>("system/status", CancellationToken.None));
                Assert.Contains("too large", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public async Task Error_body_is_shortened_in_message()
        {
            using (var err = new MockHttp(ctx => MockHttp.Json(ctx, new string('x', 5000), 500)))
            {
                var client = new ArrClient("Sonarr", err.Url, TestKey, TestJson.Create());
                var ex = await Assert.ThrowsAsync<ArrException>(() => client.GetAsync<ArrSystemStatus>("system/status", CancellationToken.None));
                Assert.True(ex.Message.Length < 300, ex.Message.Length.ToString());
                Assert.DoesNotContain(TestKey, ex.Message);
            }
        }

        [Fact]
        public async Task Cancellation_is_honored_while_waiting_for_slow_service()
        {
            using (var slow = new MockHttp(async ctx => await Task.Delay(20000)))
            using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
            {
                var client = new ArrClient("Sonarr", slow.Url, TestKey, TestJson.Create());
                var started = DateTime.UtcNow;
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync<ArrSystemStatus>("system/status", cts.Token));
                Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
            }
        }

        [Fact]
        public async Task Huge_episode_range_from_request_is_bounded()
        {
            using (var sonarr = new MockHttp(ctx =>
            {
                var path = ctx.Request.Url.AbsolutePath;
                if (path.EndsWith("/series")) { return MockHttp.Json(ctx, "[{\"id\":1,\"title\":\"S\",\"tvdbId\":42,\"monitored\":true}]"); }
                if (path.EndsWith("/episode")) { return MockHttp.Json(ctx, "[{\"id\":7,\"seasonNumber\":1,\"episodeNumber\":1,\"title\":\"Pilot\",\"airDate\":\"2001-01-01\",\"hasFile\":false}]"); }
                return MockHttp.Json(ctx, "{}");
            }))
            {
                var config = _env.Plugin.Configuration;
                config.SonarrUrl = sonarr.Url;
                config.SonarrSearchNow = false;
                var episodes = new System.Collections.Generic.List<EpisodeRef>
                {
                    // int.MaxValue: without the bound "n++" would overflow and the loop would never end.
                    new EpisodeRef { SeasonNumber = 1, EpisodeNumber = 1, EpisodeNumberEnd = int.MaxValue, Name = "Pilot" }
                };
                var task = new ArrActions(TestJson.Create(), new GapFinderLogger(TestEnv.Logs()))
                    .SearchEpisodesInSonarrAsync(config, TestKey, "42", "S", episodes, CancellationToken.None);
                var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))) == task;
                Assert.True(finished, "Sonarr search did not finish within 10 s (unbounded episode range)");
                var result = await task;
                Assert.True(result.Unmatched.Count <= 100, result.Unmatched.Count.ToString());
                Assert.Equal(1, result.Monitored);
            }
        }

        [Fact]
        public async Task Radarr_add_still_works_and_reports_existing_movies()
        {
            using (var radarr = new MockHttp(ctx =>
            {
                var path = ctx.Request.Url.AbsolutePath;
                var query = ctx.Request.Url.Query;
                if (path.EndsWith("/movie") && ctx.Request.HttpMethod == "GET")
                {
                    return MockHttp.Json(ctx, query.Contains("tmdbId=603") ? "[{\"id\":1,\"title\":\"The Matrix\",\"tmdbId\":603,\"hasFile\":true}]" : "[]");
                }
                if (path.EndsWith("/movie/lookup/tmdb")) { return MockHttp.Json(ctx, "{\"title\":\"The Matrix Reloaded\",\"year\":2003,\"tmdbId\":604}"); }
                if (path.EndsWith("/movie") && ctx.Request.HttpMethod == "POST") { return MockHttp.Json(ctx, "{\"id\":2,\"title\":\"The Matrix Reloaded\",\"tmdbId\":604}", 201); }
                return MockHttp.Json(ctx, "{}", 404);
            }))
            {
                var config = _env.Plugin.Configuration;
                config.RadarrUrl = radarr.Url;
                config.RadarrQualityProfileId = 1;
                config.RadarrRootFolder = "/movies";
                var results = await new ArrActions(TestJson.Create(), new GapFinderLogger(TestEnv.Logs()))
                    .AddMoviesToRadarrAsync(config, TestKey, new[] { "603", "604", "abc" }, CancellationToken.None);

                Assert.Equal(new[] { "exists", "added", "error" }, results.Select(r => r.Status).ToArray());
                Assert.All(radarr.Requests, r => Assert.Equal(TestKey, r.ApiKey));
            }
        }

        // ---------------------------------------------------------------
        // "Test connection" with a saved key
        // ---------------------------------------------------------------

        private GapFinderService NewService() => new GapFinderService(
            Substitute.For<ILibraryManager>(), Substitute.For<IProviderManager>(), Substitute.For<IFileSystem>(),
            Substitute.For<ICollectionManager>(), Substitute.For<ILibraryMonitor>(), TestJson.Create(), TestEnv.Logs());

        [Fact]
        public void Test_connection_does_not_send_saved_key_to_a_different_address()
        {
            using (var foreign = new MockHttp(ctx => MockHttp.Json(ctx, StatusJson)))
            {
                var config = _env.Plugin.Configuration;
                config.SonarrUrl = "http://127.0.0.1:1"; // saved, "real" address
                config.SonarrApiKey = SecretProtector.Protect(TestKey);

                var result = (ArrTestResult)NewService().Post(new TestArrConnection { Service = "Sonarr", Url = foreign.Url, ApiKey = "" });

                Assert.DoesNotContain(foreign.Requests, r => r.ApiKey == TestKey);
                Assert.False(result.Ok);
            }
        }

        [Fact]
        public void Test_connection_uses_saved_key_for_the_saved_address()
        {
            using (var sonarr = new MockHttp(ctx => MockHttp.Json(ctx, StatusJson)))
            {
                var config = _env.Plugin.Configuration;
                config.SonarrUrl = sonarr.Url;
                config.SonarrApiKey = SecretProtector.Protect(TestKey);

                // Same address, only spelled differently (trailing slash, upper case).
                var result = (ArrTestResult)NewService().Post(new TestArrConnection { Service = "Sonarr", Url = sonarr.Url.ToUpperInvariant() + "/", ApiKey = "" });

                Assert.True(result.Ok, result.Message);
                Assert.Contains(sonarr.Requests, r => r.ApiKey == TestKey);
            }
        }

        [Fact]
        public void Test_connection_with_newly_entered_key_may_use_any_address()
        {
            using (var sonarr = new MockHttp(ctx => MockHttp.Json(ctx, StatusJson)))
            {
                var result = (ArrTestResult)NewService().Post(new TestArrConnection { Service = "Sonarr", Url = sonarr.Url, ApiKey = "entered-key" });
                Assert.True(result.Ok, result.Message);
                Assert.Contains(sonarr.Requests, r => r.ApiKey == "entered-key");
            }
        }

        // ---------------------------------------------------------------
        // URL validation on save
        // ---------------------------------------------------------------

        private static string Normalize(string url)
        {
            var method = typeof(GapFinderService).GetMethod("NormalizeService", BindingFlags.NonPublic | BindingFlags.Static);
            try
            {
                return ((ArrServiceDto)method.Invoke(null, new object[] { new ArrServiceDto { Url = url, Enabled = true }, "Sonarr" })).Url;
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException;
            }
        }

        [Theory]
        [InlineData("http://nas:8989", "http://nas:8989")]
        [InlineData("https://nas.local/sonarr/", "https://nas.local/sonarr")]
        [InlineData("http://192.168.1.10:8989", "http://192.168.1.10:8989")]
        [InlineData("http://[fd00::10]:8989", "http://[fd00::10]:8989")]
        public void Valid_addresses_are_accepted(string url, string expected)
        {
            Assert.Equal(expected, Normalize(url));
        }

        [Theory]
        [InlineData("file:///C:/Windows/win.ini")]
        [InlineData("ftp://nas/")]
        [InlineData("javascript:alert(1)")]
        [InlineData("nas:8989")]
        [InlineData("http://user:secret@nas:8989")]
        [InlineData("http://nas:8989/?x=1")]
        [InlineData("http://nas:8989/#frag")]
        public void Invalid_addresses_are_rejected(string url)
        {
            Assert.Throws<ArgumentException>(() => Normalize(url));
        }

        // ---------------------------------------------------------------
        // Authorization
        // ---------------------------------------------------------------

        [Fact]
        public void Every_route_requires_the_admin_role()
        {
            var routes = typeof(Plugin).Assembly.GetTypes()
                .Where(t => t.GetCustomAttributes(true).Any(a => a.GetType().Name == "RouteAttribute"))
                .ToList();
            Assert.Equal(18, routes.Count);
            foreach (var route in routes)
            {
                var auth = route.GetCustomAttributes(true).FirstOrDefault(a => a.GetType().Name == "AuthenticatedAttribute");
                Assert.True(auth != null, route.Name + " has no [Authenticated]");
                var roles = (string)auth.GetType().GetProperty("Roles").GetValue(auth);
                Assert.True(string.Equals(roles, "Admin", StringComparison.OrdinalIgnoreCase), route.Name + " roles: " + roles);
            }
        }
    }
}
