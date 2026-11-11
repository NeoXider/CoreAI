using System;
using System.Collections.Generic;

namespace CoreAI.Mods.Rbx.Instances.Terrain
{
    /// <summary>
    /// How much time a frame may spend building Terrain chunks: the full budget while the last frames were
    /// smooth, less as they get slower. Engine-free; the presenter feeds it each frame's length.
    /// </summary>
    /// <remarks>
    /// WHY adaptive: a fixed six-millisecond budget either wastes smooth frames while a burst of new columns
    /// waits, or adds its whole cost to frames that are already late; following the last frames spends the
    /// time a frame has and gives it back when it has none.
    /// </remarks>
    public sealed class TerrainFrameBudget
    {
        /// <summary>The budget while the last frames were smooth, in milliseconds.</summary>
        public const double FullMilliseconds = 4d;

        /// <summary>The budget while the last frames were late but under a 60 Hz frame, in milliseconds.</summary>
        public const double ReducedMilliseconds = 1.5d;

        /// <summary>The budget while a frame was over a 60 Hz frame; one chunk is always built anyway.</summary>
        public const double MinimumMilliseconds = 0.5d;

        /// <summary>The longest frame, in milliseconds, still counted as smooth.</summary>
        public const double SmoothFrameMilliseconds = 12d;

        /// <summary>The longest frame, in milliseconds, under a 60 Hz frame.</summary>
        public const double LateFrameMilliseconds = 1000d / 60d;

        private readonly double[] _recent = new double[4];
        private int _count;
        private int _next;

        /// <summary>The milliseconds the next frame may spend building chunks.</summary>
        public double Milliseconds { get; private set; } = FullMilliseconds;

        /// <summary>Records a frame's length and updates <see cref="Milliseconds"/> from the last four frames.</summary>
        public void RecordFrame(double frameMilliseconds)
        {
            if (double.IsNaN(frameMilliseconds) || frameMilliseconds < 0d)
            {
                return;
            }

            _recent[_next] = frameMilliseconds;
            _next = (_next + 1) % _recent.Length;
            _count = Math.Min(_count + 1, _recent.Length);
            double worst = 0d;
            for (int index = 0; index < _count; index++)
            {
                worst = Math.Max(worst, _recent[index]);
            }

            Milliseconds = worst < SmoothFrameMilliseconds ? FullMilliseconds
                : worst < LateFrameMilliseconds ? ReducedMilliseconds
                : MinimumMilliseconds;
        }
    }

    /// <summary>
    /// The chunk-grid geometry the Terrain's streaming needs: how far a chunk is from a point, the order to build
    /// queued chunks in, and the chunks a ray crosses. Engine-free; positions are in cells.
    /// </summary>
    public static class TerrainChunkReach
    {
        /// <summary>The squared distance, in cells, from (x, y, z) to the nearest point of chunk <paramref name="coord"/>.</summary>
        public static double DistanceSquared(TerrainChunkCoord coord, double x, double y, double z)
        {
            double dx = Gap(x, coord.MinCellX);
            double dy = Gap(y, coord.MinCellY);
            double dz = Gap(z, coord.MinCellZ);
            return dx * dx + dy * dy + dz * dz;
        }

        /// <summary>Sorts <paramref name="coords"/> nearest first to (x, y, z), ties kept in their order.</summary>
        public static void SortNearestFirst(List<TerrainChunkCoord> coords, double x, double y, double z,
            List<double> distanceScratch)
        {
            if (coords == null)
            {
                throw new ArgumentNullException(nameof(coords));
            }

            if (coords.Count < 2)
            {
                return;
            }

            // WHY an insertion sort over keyed copies: the pending list is short and mostly in order from the
            // last frame, which insertion sort finishes in a pass, and it keeps ties in their order with no
            // allocation.
            distanceScratch.Clear();
            for (int index = 0; index < coords.Count; index++)
            {
                distanceScratch.Add(DistanceSquared(coords[index], x, y, z));
            }

            for (int index = 1; index < coords.Count; index++)
            {
                TerrainChunkCoord coord = coords[index];
                double distance = distanceScratch[index];
                int at = index - 1;
                while (at >= 0 && distanceScratch[at] > distance)
                {
                    coords[at + 1] = coords[at];
                    distanceScratch[at + 1] = distanceScratch[at];
                    at--;
                }

                coords[at + 1] = coord;
                distanceScratch[at + 1] = distance;
            }
        }

        /// <summary>
        /// Adds to <paramref name="into"/>, in the order a ray from (x, y, z) along (dirX, dirY, dirZ) enters them,
        /// the chunks it crosses within <paramref name="lengthCells"/> cells, at most <paramref name="maxChunks"/>
        /// of them; returns how many it added. A grid walk (Amanatides and Woo) over 16-cell chunks.
        /// </summary>
        public static int ChunksAlong(double x, double y, double z, double dirX, double dirY, double dirZ,
            double lengthCells, int maxChunks, List<TerrainChunkCoord> into)
        {
            if (into == null)
            {
                throw new ArgumentNullException(nameof(into));
            }

            double magnitude = Math.Sqrt(dirX * dirX + dirY * dirY + dirZ * dirZ);
            if (maxChunks <= 0 || magnitude <= 0d || double.IsNaN(magnitude) || lengthCells < 0d)
            {
                return 0;
            }

            dirX /= magnitude;
            dirY /= magnitude;
            dirZ /= magnitude;
            const double edge = TerrainChunkCoord.Size;
            int cx = (int)Math.Floor(x / edge);
            int cy = (int)Math.Floor(y / edge);
            int cz = (int)Math.Floor(z / edge);
            int stepX = Math.Sign(dirX);
            int stepY = Math.Sign(dirY);
            int stepZ = Math.Sign(dirZ);
            double nextX = Boundary(x, cx, stepX, dirX, edge);
            double nextY = Boundary(y, cy, stepY, dirY, edge);
            double nextZ = Boundary(z, cz, stepZ, dirZ, edge);
            double deltaX = stepX == 0 ? double.PositiveInfinity : edge / Math.Abs(dirX);
            double deltaY = stepY == 0 ? double.PositiveInfinity : edge / Math.Abs(dirY);
            double deltaZ = stepZ == 0 ? double.PositiveInfinity : edge / Math.Abs(dirZ);
            int added = 0;
            double travelled = 0d;
            while (added < maxChunks)
            {
                into.Add(new TerrainChunkCoord(cx, cy, cz));
                added++;
                travelled = Math.Min(nextX, Math.Min(nextY, nextZ));
                if (travelled > lengthCells)
                {
                    break;
                }

                if (nextX <= nextY && nextX <= nextZ)
                {
                    cx += stepX;
                    nextX += deltaX;
                }
                else if (nextY <= nextZ)
                {
                    cy += stepY;
                    nextY += deltaY;
                }
                else
                {
                    cz += stepZ;
                    nextZ += deltaZ;
                }
            }

            return added;
        }

        private static double Gap(double value, int min)
        {
            if (value < min)
            {
                return min - value;
            }

            double max = min + TerrainChunkCoord.Size;
            return value > max ? value - max : 0d;
        }

        private static double Boundary(double position, int chunk, int step, double direction, double edge)
        {
            if (step == 0)
            {
                return double.PositiveInfinity;
            }

            double boundary = step > 0 ? (chunk + 1) * edge : chunk * edge;
            return (boundary - position) / direction;
        }
    }
}
