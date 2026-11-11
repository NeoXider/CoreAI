using System;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Aim checks for the camera tests. <see cref="Vector3.Angle"/> takes a single-precision acos of the normalized
    /// dot product, so it cannot report anything between 0 and 0.0198 degrees (the acos of the float just below 1):
    /// a bound tighter than that only tests which way the dot rounded, and float noise of about 1e-7 rounds it either
    /// way. <see cref="AngleDegrees"/> is atan2(|a x b|, a . b) in double, which stays accurate near zero.
    /// </summary>
    internal static class CameraAimAssert
    {
        /// <summary>
        /// The angle in degrees between two directions, accurate down to the float rounding of the inputs; NaN when
        /// either direction has no length, so a bound on it fails instead of passing.
        /// </summary>
        public static double AngleDegrees(Vector3 a, Vector3 b)
        {
            double ax = a.x, ay = a.y, az = a.z;
            double bx = b.x, by = b.y, bz = b.z;
            double lengths = Math.Sqrt((ax * ax + ay * ay + az * az) * (bx * bx + by * by + bz * bz));
            if (lengths < 1e-12)
            {
                return double.NaN;
            }

            double cx = ay * bz - az * by;
            double cy = az * bx - ax * bz;
            double cz = ax * by - ay * bx;
            double cross = Math.Sqrt(cx * cx + cy * cy + cz * cz);
            double dot = ax * bx + ay * by + az * bz;
            return Math.Atan2(cross, dot) * (180.0 / Math.PI);
        }

        /// <summary>
        /// Asserts that <paramref name="camera"/> looks at <paramref name="point"/> within
        /// <paramref name="toleranceDegrees"/>.
        /// </summary>
        public static void LooksAt(Transform camera, Vector3 point, double toleranceDegrees,
            string message = "the camera looks at the point")
        {
            AssertWithin(AngleDegrees(camera.forward, point - camera.position), toleranceDegrees, message);
        }

        /// <summary>Asserts that two directions are within <paramref name="toleranceDegrees"/> of each other.</summary>
        public static void SameDirection(Vector3 actual, Vector3 expected, double toleranceDegrees,
            string message = "the directions match")
        {
            AssertWithin(AngleDegrees(actual, expected), toleranceDegrees, message);
        }

        private static void AssertWithin(double angle, double toleranceDegrees, string message)
        {
            Assert.IsFalse(double.IsNaN(angle), message + ": a direction has no length");
            Assert.Less(angle, toleranceDegrees, message);
        }
    }
}
