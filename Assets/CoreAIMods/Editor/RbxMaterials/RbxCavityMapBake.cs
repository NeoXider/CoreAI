using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace CoreAI.Editor.RbxMaterials
{
    /// <summary>
    /// Bakes an occlusion (cavity) map from a tangent-space normal map: the height field is
    /// integrated from the normals, and every texel is darkened by how far it sits below the average
    /// height of its surroundings at three scales.
    /// <para>
    /// WHY: the packaged ambientCG sets carry their relief in one- or two-texel bevels of the normal
    /// map (a Bricks104 mortar joint is 13 texels wide with a two-texel edge). The mip chain averages
    /// those bevels away, so beyond a few metres a brick wall rendered flat whatever the normal
    /// strength. Occlusion is a scalar that averages correctly under mipmapping, so a joint that is
    /// darker at 1K is still darker at 16 pixels. The shader reads the map through the existing
    /// ambient-occlusion slot, and the catalog's cavity strength lets it darken the albedo too.
    /// </para>
    /// </summary>
    internal static class RbxCavityMapBake
    {
        /// <summary>Map name inside the packaged folder: <c>stem_1K-JPG_Cavity.jpg</c>.</summary>
        internal const string MapSuffix = "Cavity";

        private const string PackagedRoot =
            "Assets/CoreAIMods/Runtime/RbxApi/Unity/Resources/CoreAIRbxTextures";

        private const string Resolution = "_1K-JPG_";

        // WHY: blur radii in texels of the half-resolution height field. 1 catches bevels and
        // pores, 3 a mortar joint or cobble gap, 8 the dip between large stones.
        private static readonly double[] ConcavityScales = { 1.0, 3.0, 8.0 };

        // WHY: normals flatter than this are treated as this steep, so a near-vertical bevel in a
        // JPEG-damaged map cannot integrate into a cliff.
        private const double MinimumNormalZ = 0.25;

        // WHY: each map is normalised by its own 90th-percentile concavity so the catalog strength
        // means the same on every set, but never by less than this, so a nearly flat set (Marble,
        // Granite, polished Metal) stays nearly white instead of amplifying JPEG noise into dirt.
        private const double MinimumReference = 0.35;

        // WHY: the 90th-percentile concavity maps to exp(-0.7) = 0.5, so a typical groove reaches
        // half occlusion and only the deepest few percent approach black.
        private const double Falloff = 0.7;

        /// <summary>Bakes every packaged set whose relief profile asks for a cavity map.</summary>
        [MenuItem("CoreAI/Materials/Bake cavity maps for packaged textures")]
        public static void BakePackaged()
        {
            AssetDatabase.Refresh();
            List<string> baked = new();
            List<string> skipped = new();
            foreach (RbxCc0TextureSet set in RbxCc0TextureSets.Sets)
            {
                if (RbxMaterialSurfaceProfiles.ReliefFor(set.MaterialName).CavityStrength <= 0f)
                {
                    continue;
                }

                string stem = set.Folder.Substring(set.Folder.IndexOf('/') + 1);
                string normalPath = PackagedRoot + "/" + stem + Resolution + "NormalGL.jpg";
                if (!File.Exists(normalPath))
                {
                    skipped.Add(set.MaterialName + ": no " + normalPath);
                    continue;
                }

                string cavityPath = PackagedRoot + "/" + stem + Resolution + MapSuffix + ".jpg";
                BakeFile(normalPath, true, cavityPath);
                baked.Add(cavityPath);
            }

            AssetDatabase.Refresh();
            foreach (string path in baked)
            {
                RbxMaterialCatalogEditorUtility.ApplyTextureImportSettings(path, false, false);
            }

            AssetDatabase.SaveAssets();
            Debug.Log("[CoreAI] Cavity maps baked: " + baked.Count +
                      (skipped.Count == 0 ? "." : ", skipped: " + string.Join("; ", skipped)));
        }

        /// <summary>Headless entry point: bakes the cavity maps, then rebuilds the packaged catalog.</summary>
        public static void BakePackagedAndRebuildCatalog()
        {
            BakePackaged();
            RbxPackagedTextureCatalogBuild.Rebuild();
        }

        /// <summary>Reads a normal map from disk and writes its half-resolution cavity map as JPG.</summary>
        internal static void BakeFile(string normalPath, bool isOpenGlNormal, string cavityPath)
        {
            Texture2D source = new(2, 2, TextureFormat.RGBA32, false, true);
            Texture2D output = null;
            try
            {
                if (!source.LoadImage(File.ReadAllBytes(normalPath)))
                {
                    throw new InvalidDataException("Could not decode " + normalPath);
                }

                byte[] occlusion = Bake(source.GetPixels32(), source.width, source.height,
                    isOpenGlNormal, out int width, out int height);
                output = new Texture2D(width, height, TextureFormat.RGB24, false, true);
                Color32[] pixels = new Color32[occlusion.Length];
                for (int i = 0; i < occlusion.Length; i++)
                {
                    byte value = occlusion[i];
                    pixels[i] = new Color32(value, value, value, 255);
                }

                output.SetPixels32(pixels);
                output.Apply(false);
                File.WriteAllBytes(cavityPath, output.EncodeToJPG(95));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(source);
                if (output != null)
                {
                    UnityEngine.Object.DestroyImmediate(output);
                }
            }
        }

        /// <summary>
        /// Bakes an 8-bit occlusion map at half the normal map's resolution (255 = open surface).
        /// Rows run bottom-up as in <see cref="Texture2D.GetPixels32()"/>; both sides must be powers
        /// of two because the height field is integrated with a periodic FFT, which also keeps the
        /// result tileable.
        /// </summary>
        internal static byte[] Bake(Color32[] normalPixels, int width, int height, bool isOpenGlNormal,
            out int outputWidth, out int outputHeight)
        {
            if (normalPixels == null || normalPixels.Length != width * height)
            {
                throw new ArgumentException("pixel count does not match the size", nameof(normalPixels));
            }

            if (width < 4 || height < 4 || !Mathf.IsPowerOfTwo(width) || !Mathf.IsPowerOfTwo(height))
            {
                throw new ArgumentException("normal maps must be power-of-two sized, at least 4x4");
            }

            outputWidth = width / 2;
            outputHeight = height / 2;
            int count = outputWidth * outputHeight;
            double[] slopeX = new double[count];
            double[] slopeY = new double[count];
            double greenSign = isOpenGlNormal ? 1.0 : -1.0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Color32 pixel = normalPixels[y * width + x];
                    double nx = pixel.r / 255.0 * 2.0 - 1.0;
                    double ny = (pixel.g / 255.0 * 2.0 - 1.0) * greenSign;
                    double nz = Math.Max(pixel.b / 255.0 * 2.0 - 1.0, MinimumNormalZ);
                    // WHY: averaging slopes over 2x2 texels is exact for the box-filtered height,
                    // and the factor 2 converts them to half-resolution texel units.
                    int index = (y / 2) * outputWidth + x / 2;
                    slopeX[index] += -nx / nz * 0.5;
                    slopeY[index] += -ny / nz * 0.5;
                }
            }

            double[] realX = slopeX;
            double[] imaginaryX = new double[count];
            double[] realY = slopeY;
            double[] imaginaryY = new double[count];
            Fft2D(realX, imaginaryX, outputWidth, outputHeight, false);
            Fft2D(realY, imaginaryY, outputWidth, outputHeight, false);

            double[] realC = new double[count];
            double[] imaginaryC = new double[count];
            for (int y = 0; y < outputHeight; y++)
            {
                double ky = Frequency(y, outputHeight);
                for (int x = 0; x < outputWidth; x++)
                {
                    int index = y * outputWidth + x;
                    if (index == 0)
                    {
                        continue;
                    }

                    double kx = Frequency(x, outputWidth);
                    // WHY: forward differences (e^ik - 1) have no null space at the Nyquist
                    // frequency, unlike central differences, which leave a checkerboard in the height.
                    double dxRe = Math.Cos(kx) - 1.0;
                    double dxIm = Math.Sin(kx);
                    double dyRe = Math.Cos(ky) - 1.0;
                    double dyIm = Math.Sin(ky);
                    double denominator = dxRe * dxRe + dxIm * dxIm + dyRe * dyRe + dyIm * dyIm;
                    // Least-squares height: conj(Dx) * Gx + conj(Dy) * Gy over |Dx|^2 + |Dy|^2.
                    double heightRe = (dxRe * realX[index] + dxIm * imaginaryX[index] +
                                       dyRe * realY[index] + dyIm * imaginaryY[index]) / denominator;
                    double heightIm = (dxRe * imaginaryX[index] - dxIm * realX[index] +
                                       dyRe * imaginaryY[index] - dyIm * realY[index]) / denominator;
                    double squaredFrequency = kx * kx + ky * ky;
                    double filter = 0.0;
                    foreach (double scale in ConcavityScales)
                    {
                        filter += Math.Exp(-0.5 * scale * scale * squaredFrequency) - 1.0;
                    }

                    realC[index] = heightRe * filter;
                    imaginaryC[index] = heightIm * filter;
                }
            }

            Fft2D(realC, imaginaryC, outputWidth, outputHeight, true);

            double[] concavity = new double[count];
            for (int i = 0; i < count; i++)
            {
                concavity[i] = Math.Max(realC[i], 0.0);
            }

            double[] sorted = (double[])concavity.Clone();
            Array.Sort(sorted);
            double reference = Math.Max(sorted[(int)(0.9 * (count - 1))], MinimumReference);
            byte[] occlusion = new byte[count];
            for (int i = 0; i < count; i++)
            {
                double value = Math.Exp(-Falloff * concavity[i] / reference);
                occlusion[i] = (byte)Math.Round(Math.Min(Math.Max(value, 0.0), 1.0) * 255.0);
            }

            return occlusion;
        }

        private static double Frequency(int index, int size)
        {
            int signedIndex = index < size / 2 ? index : index - size;
            return 2.0 * Math.PI * signedIndex / size;
        }

        private static void Fft2D(double[] real, double[] imaginary, int width, int height,
            bool inverse)
        {
            double[] rowReal = new double[width];
            double[] rowImaginary = new double[width];
            for (int y = 0; y < height; y++)
            {
                Array.Copy(real, y * width, rowReal, 0, width);
                Array.Copy(imaginary, y * width, rowImaginary, 0, width);
                Fft(rowReal, rowImaginary, inverse);
                Array.Copy(rowReal, 0, real, y * width, width);
                Array.Copy(rowImaginary, 0, imaginary, y * width, width);
            }

            double[] columnReal = new double[height];
            double[] columnImaginary = new double[height];
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    columnReal[y] = real[y * width + x];
                    columnImaginary[y] = imaginary[y * width + x];
                }

                Fft(columnReal, columnImaginary, inverse);
                for (int y = 0; y < height; y++)
                {
                    real[y * width + x] = columnReal[y];
                    imaginary[y * width + x] = columnImaginary[y];
                }
            }
        }

        /// <summary>In-place iterative radix-2 FFT; the inverse is scaled by 1/n.</summary>
        private static void Fft(double[] real, double[] imaginary, bool inverse)
        {
            int n = real.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1)
                {
                    j ^= bit;
                }

                j ^= bit;
                if (i < j)
                {
                    (real[i], real[j]) = (real[j], real[i]);
                    (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
                }
            }

            for (int length = 2; length <= n; length <<= 1)
            {
                double angle = 2.0 * Math.PI / length * (inverse ? 1.0 : -1.0);
                double stepRe = Math.Cos(angle);
                double stepIm = Math.Sin(angle);
                for (int start = 0; start < n; start += length)
                {
                    double twiddleRe = 1.0;
                    double twiddleIm = 0.0;
                    int half = length >> 1;
                    for (int k = 0; k < half; k++)
                    {
                        int a = start + k;
                        int b = a + half;
                        double productRe = real[b] * twiddleRe - imaginary[b] * twiddleIm;
                        double productIm = real[b] * twiddleIm + imaginary[b] * twiddleRe;
                        real[b] = real[a] - productRe;
                        imaginary[b] = imaginary[a] - productIm;
                        real[a] += productRe;
                        imaginary[a] += productIm;
                        double nextRe = twiddleRe * stepRe - twiddleIm * stepIm;
                        twiddleIm = twiddleRe * stepIm + twiddleIm * stepRe;
                        twiddleRe = nextRe;
                    }
                }
            }

            if (!inverse)
            {
                return;
            }

            for (int i = 0; i < n; i++)
            {
                real[i] /= n;
                imaginary[i] /= n;
            }
        }
    }
}
