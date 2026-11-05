using System;
using System.Collections.Generic;
using System.Linq;
using GapFinder.Api;
using GapFinder.Data;
using GapFinder.Scanning;
using Xunit;

namespace GapFinder.Tests
{
    /// <summary>Missing episodes without an air date (old series on TheTVDB) that count as aired.</summary>
    public class AssumedAiredTests
    {
        private static readonly DateTime Today = new DateTime(2026, 11, 5);

        private static DateTimeOffset? D(int y, int m, int d) => new DateTimeOffset(y, m, d, 0, 0, 0, TimeSpan.Zero);

        private static List<SeasonResult> Seasons(params (int Season, int Episode, DateTimeOffset? Date)[] missing) =>
            missing.GroupBy(m => m.Season).Select(g => new SeasonResult
            {
                SeasonNumber = g.Key,
                Missing = g.Select(m => new MissingEpisode { SeasonNumber = m.Season, EpisodeNumber = m.Episode, PremiereDate = m.Date }).ToList()
            }).ToList();

        private static MissingEpisode Find(List<SeasonResult> seasons, int s, int e) =>
            seasons.SelectMany(x => x.Missing).Single(m => m.SeasonNumber == s && m.EpisodeNumber == e);

        [Fact]
        public void Undated_episode_before_a_later_aired_episode_counts_as_aired()
        {
            var seasons = Seasons((3, 7, null), (6, 19, null));
            var provider = new[] { (3, 6, D(1959, 2, 13)), (3, 7, (DateTimeOffset?)null), (6, 18, D(1959, 5, 2)), (6, 19, (DateTimeOffset?)null) };

            SeriesScanner.MarkAssumedAired(seasons, false, provider, Today);

            Assert.True(Find(seasons, 3, 7).AssumedAired);
            Assert.False(Find(seasons, 6, 19).AssumedAired);
            Assert.True(ResultFilter.IsAired(Find(seasons, 3, 7)));
        }

        [Fact]
        public void Ended_series_counts_all_undated_episodes_as_aired()
        {
            var seasons = Seasons((7, 1, null), (0, 3, null));

            SeriesScanner.MarkAssumedAired(seasons, true, new[] { (1, 1, D(1954, 1, 1)) }, Today);

            Assert.True(Find(seasons, 7, 1).AssumedAired);
            Assert.True(Find(seasons, 0, 3).AssumedAired);
        }

        [Fact]
        public void Announced_episodes_of_a_running_series_stay_upcoming()
        {
            var seasons = Seasons((5, 1, null), (0, 2, null));
            var provider = new[] { (4, 10, D(2026, 6, 1)), (5, 2, D(2027, 3, 1)), (5, 1, (DateTimeOffset?)null) };

            SeriesScanner.MarkAssumedAired(seasons, false, provider, Today);

            Assert.False(Find(seasons, 5, 1).AssumedAired);
            Assert.False(Find(seasons, 0, 2).AssumedAired);
            Assert.False(ResultFilter.IsAired(Find(seasons, 5, 1)));
        }
    }
}
