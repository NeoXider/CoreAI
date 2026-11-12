using System;
using Newtonsoft.Json.Linq;

namespace CoreAI.Diagnostics
{
    /// <summary>
    /// The compact JSON shapes the debug tools share for frame rate and engine counters, so the MCP tools and the
    /// in-game tool report the same field names. Numbers are rounded to what a reader can act on.
    /// </summary>
    public static class RuntimeDiagnosticsJson
    {
        /// <summary>
        /// <c>{"fps_avg","fps_min","frame_ms_avg","frame_ms_p95","frame_ms_max","slow_frames","frames","window_s"}</c>,
        /// or <c>{"frames":0,"note":...}</c> when nothing was recorded.
        /// </summary>
        public static JObject FrameRate(FrameRateSnapshot snapshot)
        {
            if (snapshot.Frames == 0)
            {
                return new JObject
                {
                    ["frames"] = 0,
                    ["note"] = "no frames recorded yet (not playing, or the monitor has just started)"
                };
            }

            return new JObject
            {
                ["fps_avg"] = Round(snapshot.AverageFps, 1),
                ["fps_min"] = Round(snapshot.MinimumFps, 1),
                ["frame_ms_avg"] = Round(snapshot.AverageFrameMilliseconds, 2),
                ["frame_ms_p95"] = Round(snapshot.P95FrameMilliseconds, 2),
                ["frame_ms_max"] = Round(snapshot.MaxFrameMilliseconds, 2),
                ["slow_frames"] = snapshot.SlowFrames,
                ["frames"] = snapshot.Frames,
                ["window_s"] = Round(snapshot.WindowSeconds, 2)
            };
        }

        /// <summary>
        /// One field per available counter (bytes as <c>&lt;name&gt;_mb</c>, times in ms) plus <c>unavailable</c>
        /// listing what the build does not provide, and <c>note</c> when the source gave one.
        /// </summary>
        public static JObject Counters(EngineCounterSnapshot snapshot)
        {
            JObject result = new();
            if (snapshot == null)
            {
                return result;
            }

            foreach (EngineCounterValue value in snapshot.Values)
            {
                if (string.Equals(value.Unit, "bytes", StringComparison.Ordinal))
                {
                    result[value.Name + "_mb"] = Round(value.Value / (1024d * 1024d), 1);
                }
                else if (string.Equals(value.Unit, "ms", StringComparison.Ordinal))
                {
                    result[value.Name] = Round(value.Value, 2);
                }
                else
                {
                    result[value.Name] = (long)Math.Round(value.Value);
                }
            }

            if (snapshot.Unavailable.Count > 0)
            {
                result["unavailable"] = new JArray(snapshot.Unavailable);
            }

            if (snapshot.Note.Length > 0)
            {
                result["note"] = snapshot.Note;
            }

            return result;
        }

        private static double Round(double value, int digits)
        {
            return double.IsNaN(value) || double.IsInfinity(value) ? 0d : Math.Round(value, digits);
        }
    }
}
