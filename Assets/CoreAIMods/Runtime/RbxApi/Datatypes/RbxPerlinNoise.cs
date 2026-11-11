using System;

namespace CoreAI.Mods.Rbx.Datatypes
{
    /// <summary>
    /// Luau's <c>math.noise</c>: Ken Perlin's improved noise over his reference permutation, evaluated in
    /// single precision like the Luau runtime. The mirror's fixed points hold bit for bit
    /// (<c>math.noise(1.158, 5.723)</c> is 0.48397532105446, <c>math.noise(1.158, 6)</c> is
    /// 0.15315161645412), integer lattice points give 0, and the noise repeats every 256 units on each
    /// axis.
    /// </summary>
    public static class RbxPerlinNoise
    {
        /// <summary>The period of the noise on every axis.</summary>
        public const int Period = 256;

        private static readonly byte[] Permutation =
        {
            151, 160, 137, 91, 90, 15, 131, 13, 201, 95, 96, 53, 194, 233, 7, 225, 140, 36, 103, 30, 69,
            142, 8, 99, 37, 240, 21, 10, 23, 190, 6, 148, 247, 120, 234, 75, 0, 26, 197, 62, 94, 252, 219,
            203, 117, 35, 11, 32, 57, 177, 33, 88, 237, 149, 56, 87, 174, 20, 125, 136, 171, 168, 68, 175,
            74, 165, 71, 134, 139, 48, 27, 166, 77, 146, 158, 231, 83, 111, 229, 122, 60, 211, 133, 230,
            220, 105, 92, 41, 55, 46, 245, 40, 244, 102, 143, 54, 65, 25, 63, 161, 1, 216, 80, 73, 209, 76,
            132, 187, 208, 89, 18, 169, 200, 196, 135, 130, 116, 188, 159, 86, 164, 100, 109, 198, 173, 186,
            3, 64, 52, 217, 226, 250, 124, 123, 5, 202, 38, 147, 118, 126, 255, 82, 85, 212, 207, 206, 59,
            227, 47, 16, 58, 17, 182, 189, 28, 42, 223, 183, 170, 213, 119, 248, 152, 2, 44, 154, 163, 70,
            221, 153, 101, 155, 167, 43, 172, 9, 129, 22, 39, 253, 19, 98, 108, 110, 79, 113, 224, 232, 178,
            185, 112, 104, 218, 246, 97, 228, 251, 34, 242, 193, 238, 210, 144, 12, 191, 179, 162, 241, 81,
            51, 145, 235, 249, 14, 239, 107, 49, 192, 214, 31, 181, 199, 106, 157, 184, 84, 204, 176, 115,
            121, 50, 45, 127, 4, 150, 254, 138, 236, 205, 93, 222, 114, 67, 29, 24, 72, 243, 141, 128, 195,
            78, 66, 215, 61, 156, 180
        };

        /// <summary>
        /// <c>math.noise(x, y, z)</c>: the arguments are narrowed to single precision first, as Luau does,
        /// and the single-precision result is widened back. A non-finite argument gives NaN.
        /// </summary>
        public static double Noise(double x, double y = 0d, double z = 0d)
        {
            return Perlin((float)x, (float)y, (float)z);
        }

        /// <summary>The single-precision noise value at (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>).</summary>
        public static float Perlin(float x, float y, float z)
        {
            float xFloor = MathF.Floor(x);
            float yFloor = MathF.Floor(y);
            float zFloor = MathF.Floor(z);
            int xi = LatticeIndex(xFloor);
            int yi = LatticeIndex(yFloor);
            int zi = LatticeIndex(zFloor);
            float xf = (float)(x - xFloor);
            float yf = (float)(y - yFloor);
            float zf = (float)(z - zFloor);
            float u = Fade(xf);
            float v = Fade(yf);
            float w = Fade(zf);

            int a = P(xi) + yi;
            int aa = P(a) + zi;
            int ab = P(a + 1) + zi;
            int b = P(xi + 1) + yi;
            int ba = P(b) + zi;
            int bb = P(b + 1) + zi;

            float xm = (float)(xf - 1f);
            float ym = (float)(yf - 1f);
            float zm = (float)(zf - 1f);
            float la = Lerp(u, Grad(P(aa), xf, yf, zf), Grad(P(ba), xm, yf, zf));
            float lb = Lerp(u, Grad(P(ab), xf, ym, zf), Grad(P(bb), xm, ym, zf));
            float la1 = Lerp(u, Grad(P(aa + 1), xf, yf, zm), Grad(P(ba + 1), xm, yf, zm));
            float lb1 = Lerp(u, Grad(P(ab + 1), xf, ym, zm), Grad(P(bb + 1), xm, ym, zm));
            return Lerp(w, Lerp(v, la, lb), Lerp(v, la1, lb1));
        }

        /// <summary>The permutation entry for <paramref name="index"/>, wrapped like the doubled reference table.</summary>
        private static int P(int index)
        {
            return Permutation[index & 255];
        }

        /// <summary>
        /// The low byte of the floored coordinate as the native runtime computes it: a value outside the
        /// int range (or NaN) truncates to int.MinValue on x86 and x64, whose low byte is 0, so it is
        /// spelled out here instead of left to a runtime's float-to-int conversion.
        /// </summary>
        private static int LatticeIndex(float floor)
        {
            if (float.IsNaN(floor) || floor >= 2147483648f || floor < -2147483648f)
            {
                return 0;
            }

            return (int)floor & 255;
        }

        // WHY float casts on every intermediate: C# lets an implementation keep float expressions in
        // higher precision (the 32-bit Mono JIT does), and the same seed must give the same world on
        // every runtime; an explicit cast is the one place the language guarantees single rounding.
        private static float Fade(float t)
        {
            float inner = (float)((float)(t * 6f) - 15f);
            float poly = (float)((float)(t * inner) + 10f);
            return (float)((float)((float)(t * t) * t) * poly);
        }

        private static float Lerp(float t, float a, float b)
        {
            return (float)(a + (float)(t * (float)(b - a)));
        }

        private static float Grad(int hash, float x, float y, float z)
        {
            int h = hash & 15;
            float u = h < 8 ? x : y;
            float v = h < 4 ? y : (h == 12 || h == 14 ? x : z);
            return (float)(((h & 1) != 0 ? -u : u) + ((h & 2) != 0 ? -v : v));
        }
    }
}
