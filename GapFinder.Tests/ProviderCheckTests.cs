using GapFinder.Scanning;
using Xunit;

namespace GapFinder.Tests
{
    /// <summary>Hints for missing metadata downloaders (series, movies, movies of collections).</summary>
    public class ProviderCheckTests
    {
        [Fact]
        public void Series_library_without_episode_provider_gets_a_warning()
        {
            Assert.Null(ProviderCheck.SeriesWarning("TV Shows", true));
            Assert.Contains("'TV Shows'", ProviderCheck.SeriesWarning("TV Shows", false));
        }

        [Theory]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, true)]
        public void Movie_library_needs_collection_import_or_moviedb(bool importCollections, bool movieDb, bool warns)
        {
            var warning = ProviderCheck.MovieWarning("Movies", importCollections, movieDb);
            Assert.Equal(warns, warning != null);
        }

        [Fact]
        public void Collection_parts_warning_names_the_collections_library()
        {
            Assert.Null(ProviderCheck.CollectionPartsWarning(true, "Collections", true));
            Assert.Contains("'Collections'", ProviderCheck.CollectionPartsWarning(true, "Collections", false));
            Assert.Contains("not available", ProviderCheck.CollectionPartsWarning(false, null, false));
        }
    }
}
