using System.Collections.Generic;
using System.Linq;
using GapFinder.Api;
using Xunit;

namespace GapFinder.Tests
{
    /// <summary>Mapping TMDB collection -> Emby collection (links in the UI, same rule as removing).</summary>
    public class CollectionLinkerTests
    {
        private static CollectionLinker.EmbyCollection Box(string id, string name, string tmdb = null) =>
            new CollectionLinker.EmbyCollection { Id = id, Name = name, TmdbId = tmdb };

        private static CollectionRefDto Want(string tmdb, string name) => new CollectionRefDto { TmdbId = tmdb, Name = name };

        [Fact]
        public void Matches_by_tmdb_id_even_with_other_name()
        {
            var r = CollectionLinker.Match(new[] { Want("688042", "Hobbs & Shaw Collection") }, new[] { Box("1", "Hobbs and Shaw Collection", "688042") });
            Assert.Single(r);
            Assert.Equal("1", r[0].Id);
        }

        [Fact]
        public void Collection_with_other_tmdb_id_never_matches_by_name()
        {
            var r = CollectionLinker.Match(new[] { Want("688042", "Hobbs & Shaw Collection") }, new[] { Box("1", "Hobbs & Shaw Collection", "999") });
            Assert.Empty(r);
        }

        [Fact]
        public void Own_collection_without_tmdb_id_matches_only_by_exact_name()
        {
            var boxes = new[] { Box("1", "Christmas Specials"), Box("2", "  unbreakable collection ") };
            var r = CollectionLinker.Match(new[] { Want("1769700", "Unbreakable Collection"), Want("1", "Christmas") }, boxes);
            Assert.Single(r);
            Assert.Equal("2", r[0].Id);
        }

        [Fact]
        public void Tmdb_match_wins_over_name_match_and_count_is_reported()
        {
            var boxes = new[] { Box("name", "Kong Collection"), Box("tmdb", "Monsterverse", "1280074") };
            var r = CollectionLinker.Match(new[] { Want("1280074", "Kong Collection") }, boxes);
            Assert.Equal("tmdb", r[0].Id);
            Assert.Equal(2, r[0].MatchCount);
        }

        [Fact]
        public void Empty_duplicate_and_null_requests_are_skipped()
        {
            var boxes = new[] { Box("1", "A", "10") };
            var r = CollectionLinker.Match(new[] { null, Want("", "A"), Want("10", "A"), Want("10", "A") }, boxes);
            Assert.Single(r);
            Assert.Empty(CollectionLinker.Match(null, boxes));
        }

        [Fact]
        public void Request_size_is_limited()
        {
            var wanted = Enumerable.Range(1, CollectionLinker.MaxRequests + 100).Select(i => Want(i.ToString(), "X"));
            var boxes = Enumerable.Range(1, CollectionLinker.MaxRequests + 100).Select(i => Box("b" + i, "Y", i.ToString())).ToList();
            Assert.Equal(CollectionLinker.MaxRequests, CollectionLinker.Match(wanted, boxes).Count);
        }
    }
}
