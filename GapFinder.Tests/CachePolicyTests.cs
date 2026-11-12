using System;
using System.Collections.Generic;
using GapFinder.Data;
using Xunit;

namespace GapFinder.Tests
{
    /// <summary>When saved provider answers are reused instead of asking TheTVDB/TMDB again.</summary>
    public class CachePolicyTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime Today = new DateTime(2026, 11, 11);

        private static CachedEpisodeList Episodes(int ageDays, string status = "Ended") => new CachedEpisodeList
        {
            FetchedUtc = Now.AddDays(-ageDays),
            Provider = "Tvdb",
            ProviderSeriesId = "123",
            Language = "de-CH",
            Status = status
        };

        [Fact]
        public void Ended_series_is_reused_while_fresh()
        {
            Assert.True(CachePolicy.CanReuseEpisodes(Episodes(10), "Ended", "Tvdb", "123", "de-CH", "s1", Now));
        }

        [Fact]
        public void Continuing_series_is_always_fetched()
        {
            Assert.False(CachePolicy.CanReuseEpisodes(Episodes(1), "Continuing", "Tvdb", "123", "de-CH", "s1", Now));
            // Ended when fetched, continuing again now (revived series).
            Assert.False(CachePolicy.CanReuseEpisodes(Episodes(1, "Ended"), "Continuing", "Tvdb", "123", "de-CH", "s1", Now));
            Assert.False(CachePolicy.CanReuseEpisodes(Episodes(1, "Continuing"), "Ended", "Tvdb", "123", "de-CH", "s1", Now));
        }

        [Fact]
        public void Changed_provider_id_language_or_provider_fetches_again()
        {
            Assert.False(CachePolicy.CanReuseEpisodes(Episodes(1), "Ended", "Tvdb", "999", "de-CH", "s1", Now));
            Assert.False(CachePolicy.CanReuseEpisodes(Episodes(1), "Ended", "Tvdb", "123", "en-US", "s1", Now));
            Assert.False(CachePolicy.CanReuseEpisodes(Episodes(1), "Ended", "Tmdb", "123", "de-CH", "s1", Now));
            Assert.False(CachePolicy.CanReuseEpisodes(Episodes(1), "Ended", "Tvdb", null, "de-CH", "s1", Now));
        }

        [Fact]
        public void Ended_series_expires_after_60_to_74_days()
        {
            Assert.True(CachePolicy.CanReuseEpisodes(Episodes(59), "Ended", "Tvdb", "123", "de-CH", "s1", Now));
            Assert.False(CachePolicy.CanReuseEpisodes(Episodes(75), "Ended", "Tvdb", "123", "de-CH", "s1", Now));
        }

        [Fact]
        public void Spread_is_stable_and_differs_between_keys()
        {
            var expiries = new HashSet<int>();
            for (var i = 0; i < 50; i++)
            {
                var key = "series" + i;
                var day = 0;
                while (CachePolicy.IsFresh(Now.AddDays(-day), TimeSpan.FromDays(60), key, Now))
                {
                    day++;
                }
                Assert.InRange(day, 60, 74);
                Assert.Equal(day, Expiry(key));
                expiries.Add(day);
            }
            Assert.True(expiries.Count > 5);
        }

        private static int Expiry(string key)
        {
            var day = 0;
            while (CachePolicy.IsFresh(Now.AddDays(-day), TimeSpan.FromDays(60), key, Now))
            {
                day++;
            }
            return day;
        }

        [Fact]
        public void Movie_collection_is_reused_for_30_days_and_not_with_other_language()
        {
            var entry = new CachedMovieCollections { FetchedUtc = Now.AddDays(-29), Language = "de-CH" };
            Assert.True(CachePolicy.CanReuseMovieCollections(entry, "de-CH", "tmdb:1", Now));
            Assert.False(CachePolicy.CanReuseMovieCollections(entry, "en-US", "tmdb:1", Now));
            entry.FetchedUtc = Now.AddDays(-45);
            Assert.False(CachePolicy.CanReuseMovieCollections(entry, "de-CH", "tmdb:1", Now));
        }

        private static CachedCollectionParts Parts(int ageDays, params CachedPart[] parts) => new CachedCollectionParts
        {
            FetchedUtc = Now.AddDays(-ageDays),
            Language = "de-CH",
            Parts = new List<CachedPart>(parts),
            LocalMemberIds = new List<string> { "1" }
        };

        private static CachedPart Part(string tmdb, DateTime? date) => new CachedPart
        {
            TmdbId = tmdb,
            PremiereDate = date.HasValue ? new DateTimeOffset(date.Value, TimeSpan.Zero) : (DateTimeOffset?)null
        };

        [Fact]
        public void Released_collection_is_reused_for_14_days()
        {
            var entry = Parts(10, Part("1", new DateTime(2001, 1, 1)), Part("2", new DateTime(2003, 1, 1)));
            Assert.True(CachePolicy.CanReuseCollectionParts(entry, new[] { "1" }, "de-CH", "c1", Now, Today));
            entry.FetchedUtc = Now.AddDays(-30);
            Assert.False(CachePolicy.CanReuseCollectionParts(entry, new[] { "1" }, "de-CH", "c1", Now, Today));
        }

        [Fact]
        public void Collection_with_upcoming_or_undated_movie_is_fetched_after_3_days()
        {
            var upcoming = Parts(4, Part("1", new DateTime(2001, 1, 1)), Part("2", new DateTime(2027, 5, 1)));
            Assert.False(CachePolicy.CanReuseCollectionParts(upcoming, new[] { "1" }, "de-CH", "c1", Now, Today));
            var undated = Parts(4, Part("1", new DateTime(2001, 1, 1)), Part("2", null));
            Assert.False(CachePolicy.CanReuseCollectionParts(undated, new[] { "1" }, "de-CH", "c1", Now, Today));
            var recent = Parts(1, Part("1", new DateTime(2001, 1, 1)), Part("2", null));
            Assert.True(CachePolicy.CanReuseCollectionParts(recent, new[] { "1" }, "de-CH", "c1", Now, Today));
        }

        [Fact]
        public void New_local_member_fetches_the_collection_again()
        {
            var entry = Parts(1, Part("1", new DateTime(2001, 1, 1)));
            // "2" is part of the collection (known) - "3" is new and unknown.
            entry.Parts.Add(Part("2", new DateTime(2003, 1, 1)));
            Assert.True(CachePolicy.CanReuseCollectionParts(entry, new[] { "1", "2" }, "de-CH", "c1", Now, Today));
            Assert.False(CachePolicy.CanReuseCollectionParts(entry, new[] { "1", "3" }, "de-CH", "c1", Now, Today));
        }

        [Fact]
        public void Wrongly_assigned_member_known_at_fetch_time_does_not_fetch_again()
        {
            var entry = Parts(1, Part("1", new DateTime(2001, 1, 1)));
            entry.LocalMemberIds.Add("77");
            Assert.True(CachePolicy.CanReuseCollectionParts(entry, new[] { "1", "77" }, "de-CH", "c1", Now, Today));
        }

        [Fact]
        public void Deleted_collection_is_reused_like_a_released_one()
        {
            var entry = new CachedCollectionParts { FetchedUtc = Now.AddDays(-5), Language = "de-CH", NotFound = true, LocalMemberIds = new List<string> { "1" } };
            Assert.True(CachePolicy.CanReuseCollectionParts(entry, new[] { "1" }, "de-CH", "c1", Now, Today));
        }

        [Fact]
        public void Future_fetch_time_is_not_fresh()
        {
            Assert.False(CachePolicy.IsFresh(Now.AddDays(1), TimeSpan.FromDays(30), "k", Now));
        }
    }
}
