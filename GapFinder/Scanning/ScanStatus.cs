using System;

namespace GapFinder.Scanning
{
    /// <summary>Snapshot of a running or finished search (for GET /GapFinder/Status).</summary>
    public class ScanStatus
    {
        public bool IsRunning { get; set; }
        /// <summary>"Series", "MovieInfo" (determining the collections of the movies), "Movies" or null.</summary>
        public string Phase { get; set; }
        public int Processed { get; set; }
        public int Total { get; set; }
        public string CurrentItem { get; set; }
        public DateTime? StartedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }
        public bool WasCancelled { get; set; }
        public string LastError { get; set; }
        /// <summary>Number of series/collections whose provider lookup failed.</summary>
        public int ItemErrors { get; set; }
        public bool ScanSeries { get; set; }
        public bool ScanMovies { get; set; }
    }
}
