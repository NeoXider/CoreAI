using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CoreAI.Mods.WorldPackages
{
    /// <summary>
    /// Where the world's save work waits now. The mutation gate, the autosave written behind a change, the kept
    /// world's record and the package store's transaction each name the step they are in while they await it; the
    /// session controller's frame pump asks <see cref="DescribeStuck"/> and logs a step that has waited far longer
    /// than any save takes, once per step. A save that hangs in a player (WebGL, no debugger) is then read from the
    /// player's log instead of guessed.
    /// </summary>
    internal static class WorldSaveStages
    {
        private static readonly object Gate = new();
        private static readonly List<Step> Active = new();

        /// <summary>
        /// Marks the step <paramref name="name"/> of <paramref name="lane"/> as running until the answer is disposed.
        /// Never throws.
        /// </summary>
        internal static IDisposable Enter(string lane, string name)
        {
            Step step = new(lane ?? "", name ?? "", Stopwatch.GetTimestamp());
            lock (Gate)
            {
                Active.Add(step);
            }

            return step;
        }

        /// <summary>
        /// The steps running for at least <paramref name="olderThan"/>, oldest first, as one line
        /// (<c>lane: step for 42 s; ...</c>); empty when none is.
        /// </summary>
        internal static string DescribeStuck(TimeSpan olderThan)
        {
            long now = Stopwatch.GetTimestamp();
            StringBuilder text = null;
            lock (Gate)
            {
                foreach (Step step in Active)
                {
                    double seconds = (now - step.Since) / (double)Stopwatch.Frequency;
                    if (seconds < olderThan.TotalSeconds)
                    {
                        continue;
                    }

                    text ??= new StringBuilder();
                    if (text.Length > 0)
                    {
                        text.Append("; ");
                    }

                    text.Append(step.Lane).Append(": ").Append(step.Name).Append(" for ")
                        .Append(seconds.ToString("0", CultureInfo.InvariantCulture)).Append(" s");
                }
            }

            return text?.ToString() ?? "";
        }

        /// <summary>How many steps are running now (tests).</summary>
        internal static int RunningCount
        {
            get
            {
                lock (Gate)
                {
                    return Active.Count;
                }
            }
        }

        private static void Leave(Step step)
        {
            lock (Gate)
            {
                Active.Remove(step);
            }
        }

        private sealed class Step : IDisposable
        {
            internal readonly string Lane;
            internal readonly string Name;
            internal readonly long Since;

            internal Step(string lane, string name, long since)
            {
                Lane = lane;
                Name = name;
                Since = since;
            }

            public void Dispose()
            {
                Leave(this);
            }
        }
    }
}
