using System;
using System.Globalization;

namespace CoreAI.Mods.WorldPackages
{
    /// <summary>
    /// The byte and entry limits one world-package encode or decode is held to. <see cref="Format"/> is what
    /// the format allows; a reader or writer may be held to lower limits (a platform budget), never to
    /// higher ones, so every limit stays a decompression-bomb bound.
    /// </summary>
    /// <remarks>
    /// The format's entry limit was raised from 16 MiB to 64 MiB after 7.56.0 so that a world at the desktop
    /// instance ceiling (16,384 charged instances) can be backed up: a realistic heavy world there (every
    /// instance a rotated, coloured Part with eight attributes and four tags) writes a 47.8 MB
    /// <c>world.json</c>, the reference grid world 19.6 MB. Nothing else in the format changed, so every
    /// package that fits <see cref="Version1Baseline"/> is still read by 7.56.0 and earlier, and those
    /// readers refuse a larger <c>world.json</c> explicitly by its declared size before inflating it.
    /// </remarks>
    public sealed class RbxWorldPackageLimits
    {
        /// <summary>One mebibyte.</summary>
        public const int Mebibyte = 1024 * 1024;

        public RbxWorldPackageLimits(
            int maximumPackageBytes,
            int maximumEntryBytes,
            int maximumExpandedPackageBytes,
            int maximumEntries)
        {
            RequireWithin(maximumPackageBytes, RbxWorldPackageSerializer.MaximumPackageBytes, nameof(maximumPackageBytes));
            RequireWithin(maximumEntryBytes, RbxWorldPackageSerializer.MaximumEntryBytes, nameof(maximumEntryBytes));
            RequireWithin(
                maximumExpandedPackageBytes,
                RbxWorldPackageSerializer.MaximumExpandedPackageBytes,
                nameof(maximumExpandedPackageBytes));
            RequireWithin(maximumEntries, RbxWorldPackageSerializer.MaximumEntries, nameof(maximumEntries));
            if (maximumEntryBytes > maximumExpandedPackageBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumEntryBytes),
                    maximumEntryBytes,
                    "One entry cannot be allowed more bytes than the whole expanded package.");
            }

            MaximumPackageBytes = maximumPackageBytes;
            MaximumEntryBytes = maximumEntryBytes;
            MaximumExpandedPackageBytes = maximumExpandedPackageBytes;
            MaximumEntries = maximumEntries;
        }

        /// <summary>The limits of the format itself: what this build writes and reads on desktop.</summary>
        public static RbxWorldPackageLimits Format { get; } = new(
            RbxWorldPackageSerializer.MaximumPackageBytes,
            RbxWorldPackageSerializer.MaximumEntryBytes,
            RbxWorldPackageSerializer.MaximumExpandedPackageBytes,
            RbxWorldPackageSerializer.MaximumEntries);

        /// <summary>
        /// The limits every reader up to 7.56.0 enforces (16 MiB per entry). A package within them is read
        /// by every release; one above them is refused by those releases with a size error.
        /// </summary>
        public static RbxWorldPackageLimits Version1Baseline { get; } = new(
            RbxWorldPackageSerializer.MaximumPackageBytes,
            RbxWorldPackageSerializer.Version1BaselineMaximumEntryBytes,
            RbxWorldPackageSerializer.MaximumExpandedPackageBytes,
            RbxWorldPackageSerializer.MaximumEntries);

        /// <summary>
        /// What a WebGL player writes and reads: 4 MiB compressed and the baseline 16 MiB per entry, so the
        /// raised desktop entry limit adds no decode memory in a browser, and every package a WebGL player
        /// writes is read by every release.
        /// </summary>
        public static RbxWorldPackageLimits WebGl { get; } = new(
            4 * Mebibyte,
            RbxWorldPackageSerializer.Version1BaselineMaximumEntryBytes,
            RbxWorldPackageSerializer.MaximumExpandedPackageBytes,
            RbxWorldPackageSerializer.MaximumEntries);

        /// <summary>Largest compressed package, which a reader loads into memory before anything else.</summary>
        public int MaximumPackageBytes { get; }

        /// <summary>Largest expanded size of one entry, checked from the ZIP header and again while inflating.</summary>
        public int MaximumEntryBytes { get; }

        /// <summary>Largest expanded size of all entries together.</summary>
        public int MaximumExpandedPackageBytes { get; }

        /// <summary>Largest number of ZIP entries.</summary>
        public int MaximumEntries { get; }

        public override string ToString()
        {
            return "package " + MaximumPackageBytes.ToString(CultureInfo.InvariantCulture)
                   + " B, entry " + MaximumEntryBytes.ToString(CultureInfo.InvariantCulture)
                   + " B, expanded " + MaximumExpandedPackageBytes.ToString(CultureInfo.InvariantCulture)
                   + " B, entries " + MaximumEntries.ToString(CultureInfo.InvariantCulture);
        }

        private static void RequireWithin(int value, int formatMaximum, string name)
        {
            if (value <= 0 || value > formatMaximum)
            {
                throw new ArgumentOutOfRangeException(
                    name,
                    value,
                    "A world-package limit must be positive and at most the format's "
                    + formatMaximum.ToString(CultureInfo.InvariantCulture) + ".");
            }
        }
    }
}
