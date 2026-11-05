using GapFinder.Scanning;
using Xunit;

namespace GapFinder.Tests
{
    /// <summary>Provider of the episode list -> link target (TVDB/TMDB) of the series.</summary>
    public class EpisodeProviderTests
    {
        [Theory]
        [InlineData("TheTVDB", "Tvdb")]
        [InlineData("TheMovieDb", "Tmdb")]
        [InlineData("themoviedb", "Tmdb")]
        [InlineData("AniList", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void Maps_provider_name_to_provider_id_key(string name, string expected)
        {
            Assert.Equal(expected, SeriesScanner.EpisodeProviderKey(name));
        }
    }
}
