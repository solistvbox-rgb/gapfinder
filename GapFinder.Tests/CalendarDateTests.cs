using System;
using GapFinder.Api;
using GapFinder.Data;
using Xunit;

namespace GapFinder.Tests
{
    /// <summary>Emby stores air dates as server-local midnight in UTC; the calendar day must not shift.</summary>
    public class CalendarDateTests
    {
        private static TimeZoneInfo Zone(int hours) =>
            TimeZoneInfo.CreateCustomTimeZone("test" + hours, TimeSpan.FromHours(hours), "test", "test");

        private static DateTimeOffset Utc(int y, int m, int d, int h) => new DateTimeOffset(y, m, d, h, 0, 0, TimeSpan.Zero);

        [Fact]
        public void CentralEurope_WinterMidnight_IsNextUtcDay()
        {
            // "11.11.2026" on a server at UTC+1 is stored as 10.11. 23:00 UTC.
            Assert.Equal(new DateTime(2026, 11, 11), CalendarDate.Of(Utc(2026, 11, 10, 23), Zone(1)));
        }

        [Fact]
        public void CentralEurope_SummerMidnight_IsNextUtcDay()
        {
            Assert.Equal(new DateTime(2026, 7, 1), CalendarDate.Of(Utc(2026, 6, 30, 22), Zone(2)));
        }

        [Fact]
        public void UsEast_Midnight_IsSameUtcDay()
        {
            Assert.Equal(new DateTime(2026, 11, 11), CalendarDate.Of(Utc(2026, 11, 11, 5), Zone(-5)));
        }

        [Fact]
        public void NewZealandSummer_Midnight_IsNextUtcDay()
        {
            // UTC+13: 11:00 UTC - nearest-midnight rounding alone would get this wrong.
            Assert.Equal(new DateTime(2026, 11, 11), CalendarDate.Of(Utc(2026, 11, 10, 11), Zone(13)));
        }

        [Fact]
        public void UtcMidnight_StaysTheSameDay()
        {
            Assert.Equal(new DateTime(2026, 11, 11), CalendarDate.Of(Utc(2026, 11, 11, 0), Zone(1)));
            Assert.Equal(new DateTime(2026, 11, 11), CalendarDate.Of(Utc(2026, 11, 11, 0), Zone(-5)));
        }

        [Fact]
        public void OtherZone_FallsBackToNearestMidnight()
        {
            // Stored by a UTC+1 server, read on a UTC-5 server (moved database).
            Assert.Equal(new DateTime(2026, 11, 11), CalendarDate.Of(Utc(2026, 11, 10, 23), Zone(-5)));
            Assert.Equal(new DateTime(2026, 11, 11), CalendarDate.Of(Utc(2026, 11, 11, 3), Zone(1)));
        }

        [Fact]
        public void Normalize_GivesUtcMidnight_AndIsIdempotent()
        {
            // Server-local midnight of tomorrow, as Emby stores it.
            var once = CalendarDate.Normalize(new DateTimeOffset(DateTime.Now.Date.AddDays(1)));
            Assert.Equal(TimeSpan.Zero, once.Value.Offset);
            Assert.Equal(TimeSpan.Zero, once.Value.TimeOfDay);
            Assert.Equal(DateTime.Now.Date.AddDays(1), once.Value.UtcDateTime.Date);
            Assert.Equal(once, CalendarDate.Normalize(once));
            Assert.Null(CalendarDate.Normalize(null));
        }

        [Fact]
        public void IsReleased_UsesTheCalendarDay()
        {
            var tomorrowLocalMidnight = new DateTimeOffset(DateTime.Now.Date.AddDays(1));
            var todayLocalMidnight = new DateTimeOffset(DateTime.Now.Date);
            Assert.False(ResultFilter.IsReleased(tomorrowLocalMidnight, DateTime.Now.Date));
            Assert.True(ResultFilter.IsReleased(todayLocalMidnight, DateTime.Now.Date));
            Assert.False(ResultFilter.IsReleased(null, DateTime.Now.Date));
        }
    }
}
