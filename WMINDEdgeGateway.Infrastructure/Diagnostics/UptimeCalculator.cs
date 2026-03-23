using System;
using System.Collections.Generic;
using System.Linq;

namespace WMINDEdgeGateway.Infrastructure.Diagnostics
{
    /// <summary>
    /// Computes uptime percentages and 24-hour bar data from the downtime history
    /// stored in <see cref="GatewayDiagnosticsState"/>.
    /// </summary>
    public static class UptimeCalculator
    {
        /// <summary>Returns uptime % for the past <paramref name="days"/> calendar days (0–100).</summary>
        public static double UptimePercent(IReadOnlyList<DowntimeRecord> history, int days)
        {
            var windowStart = DateTime.UtcNow.Date.AddDays(-days + 1);
            var windowEnd = DateTime.UtcNow;
            double windowSeconds = (windowEnd - windowStart).TotalSeconds;
            if (windowSeconds <= 0) return 100.0;

            double downtimeSeconds = history
                .Where(r => r.End >= windowStart && r.Start <= windowEnd)
                .Sum(r =>
                {
                    var clampStart = r.Start < windowStart ? windowStart : r.Start;
                    var clampEnd = r.End > windowEnd ? windowEnd : r.End;
                    return (clampEnd - clampStart).TotalSeconds;
                });

            return Math.Round(Math.Max(0, (1.0 - downtimeSeconds / windowSeconds) * 100.0), 1);
        }

        /// <summary>
        /// Returns a 24-element array (one slot per hour, hour 0 = midnight today UTC)
        /// where each element is "up" or "down".
        /// </summary>
        public static string[] HourlyBar(IReadOnlyList<DowntimeRecord> history)
        {
            var today = DateTime.Now.Date;   
            var bar = new string[24];
            var nowHour = DateTime.Now.Hour;

            for (int h = 0; h < 24; h++)
            {
                if (h > nowHour) { bar[h] = "future"; continue; }

                var slotStart = today.AddHours(h);
                var slotEnd = slotStart.AddHours(1);

                bool hasDowntime = history.Any(r =>
                r.Start.ToLocalTime() < slotEnd &&
                r.End.ToLocalTime() > slotStart);

                bar[h] = hasDowntime ? "down" : "up";
            }
            return bar;
        }

        public static TimeSpan LongestDowntime(IReadOnlyList<DowntimeRecord> history)
            => history.Any() ? history.Max(r => r.Duration) : TimeSpan.Zero;

        public static TimeSpan TotalDowntime(IReadOnlyList<DowntimeRecord> history)
            => history.Any()
                ? TimeSpan.FromSeconds(history.Sum(r => r.Duration.TotalSeconds))
                : TimeSpan.Zero;
    }
}