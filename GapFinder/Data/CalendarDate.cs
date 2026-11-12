using System;
using System.Collections.Generic;

namespace GapFinder.Data
{
    /// <summary>
    /// Air and release dates are plain calendar dates, but Emby stores them as midnight in the server's time zone,
    /// converted to UTC: "11.11.2026" becomes 2026-11-10T23:00Z on a server in Central Europe. Taking the UTC day
    /// would then show (and count as aired) one day too early. This class turns such a value back into its calendar
    /// day; results keep it as UTC midnight, so the UTC day (server and browser) is the right one again.
    /// </summary>
    public static class CalendarDate
    {
        /// <summary>The calendar day of a date stored by Emby on this server.</summary>
        public static DateTime Of(DateTimeOffset date) => Of(date, TimeZoneInfo.Local);

        public static DateTime Of(DateTimeOffset date, TimeZoneInfo serverZone)
        {
            var utc = date.UtcDateTime;
            // Already a plain date (UTC midnight), e.g. after Normalize or from a provider that stores UTC.
            if (utc.TimeOfDay == TimeSpan.Zero)
            {
                return utc.Date;
            }
            // Midnight in the server's time zone - the usual case.
            var local = TimeZoneInfo.ConvertTimeFromUtc(utc, serverZone ?? TimeZoneInfo.Utc);
            if (local.TimeOfDay == TimeSpan.Zero)
            {
                return local.Date;
            }
            // Stored by a server in another time zone (moved database): the nearest midnight.
            return utc.TimeOfDay.TotalHours >= 12 ? utc.Date.AddDays(1) : utc.Date;
        }

        /// <summary>The calendar day as UTC midnight - the form kept in the results.</summary>
        public static DateTimeOffset? Normalize(DateTimeOffset? date) =>
            date.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(Of(date.Value), DateTimeKind.Unspecified), TimeSpan.Zero) : (DateTimeOffset?)null;

        /// <summary>Today in the server's time zone - an episode of 11.11. counts as aired from 11.11. 00:00 local time.</summary>
        public static DateTime Today => DateTime.Now.Date;

        /// <summary>Normalizes the dates of results saved by older versions (stored as server-local midnight in UTC).</summary>
        public static void NormalizeDates(SeriesResults results)
        {
            foreach (var series in results?.Series ?? new List<SeriesResult>())
            {
                foreach (var season in series.Seasons ?? new List<SeasonResult>())
                {
                    foreach (var episode in season.Missing ?? new List<MissingEpisode>())
                    {
                        episode.PremiereDate = Normalize(episode.PremiereDate);
                    }
                }
            }
        }

        public static void NormalizeDates(MovieResults results)
        {
            foreach (var collection in results?.Collections ?? new List<CollectionResult>())
            {
                foreach (var movie in collection.Missing ?? new List<MissingMovie>())
                {
                    movie.PremiereDate = Normalize(movie.PremiereDate);
                }
            }
        }
    }
}
