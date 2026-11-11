using System;

namespace CoreAI.Look
{
    /// <summary>
    /// The colours of Unity's procedural sky (<c>Skybox/Procedural</c>) near the horizon, computed on the CPU from the
    /// same single-scattering model the sky shader evaluates per vertex, so the look can set the sky's ground colour
    /// that makes the view below the horizon continue the haze above it, at every sun position. Engine-free; all
    /// colours are linear RGB.
    /// </summary>
    /// <remarks>
    /// WHY: the procedural sky draws the hemisphere below the horizon as a fixed ground colour attenuated by a long,
    /// reddening light path, so at 10:00 the unbuilt distance read as a brown floor (about sRGB 77, 72, 70) right
    /// under a haze of about 196, 214, 214. Below the horizon the sky computes
    /// <c>exposure * (inScatter + groundColor * attenuation)</c>; solving that for the ground colour at a reference
    /// angle just below the horizon, with the haze just above it as the target, gives a ground colour that follows
    /// the day: pale haze at noon, warm at dusk, dark at night. The model, its constants and its two-sample
    /// integration follow O'Neil's "Accurate Atmospheric Scattering" (GPU Gems 2, chapter 16) as Unity's
    /// procedural sky uses them; a capture test compares the result with the rendered sky.
    /// </remarks>
    public static class ProceduralSkyHaze
    {
        /// <summary>Elevation (sine of the angle) of the haze sample just above the horizon.</summary>
        public const float HazeElevation = 0.02f;

        /// <summary>Elevation (sine of the angle, negative) where the ground colour is matched to the haze.</summary>
        public const float GroundElevation = -0.05f;

        /// <summary>Largest ground colour channel returned; the sky keeps the colour finite.</summary>
        public const float MaxGroundChannel = 6f;

        private const float OuterRadius = 1.025f;
        private const float InnerRadius = 1f;
        private const float CameraHeight = 0.0001f;
        private const float Mie = 0.001f;
        private const float SunBrightness = 20f;
        private const float MaxScatter = 50f;
        private const float ScaleFactor = 1f / (OuterRadius - InnerRadius);
        private const float ScaleDepth = 0.25f;
        private const float ScaleOverScaleDepth = ScaleFactor / ScaleDepth;
        private const int Azimuths = 8;

        /// <summary>
        /// The sky's ground colour (linear) that makes the sky at <see cref="GroundElevation"/> equal the haze at
        /// <see cref="HazeElevation"/>, averaged round the horizon. <paramref name="towardSun"/> is the unit direction
        /// to the sun (y up); <paramref name="skyTint"/> is the sky material's tint as authored (sRGB, 0..1).
        /// </summary>
        public static void GroundColorMatchingHaze(float sunX, float sunY, float sunZ, float tintR, float tintG,
            float tintB, float atmosphereThickness, out float red, out float green, out float blue)
        {
            Constants constants = new(tintR, tintG, tintB, atmosphereThickness);
            Normalize(ref sunX, ref sunY, ref sunZ);
            float hazeR = 0f;
            float hazeG = 0f;
            float hazeB = 0f;
            float inR = 0f;
            float inG = 0f;
            float inB = 0f;
            float outR = 0f;
            float outG = 0f;
            float outB = 0f;
            for (int index = 0; index < Azimuths; index++)
            {
                double angle = index * 2.0 * Math.PI / Azimuths;
                float dx = (float)Math.Cos(angle);
                float dz = (float)Math.Sin(angle);
                Sky(in constants, Ray(dx, HazeElevation, dz), sunX, sunY, sunZ, out float r, out float g,
                    out float b);
                hazeR += r;
                hazeG += g;
                hazeB += b;
                Ground(in constants, Ray(dx, GroundElevation, dz), sunX, sunY, sunZ, out float scatterR, out float scatterG,
                    out float scatterB, out float throughR, out float throughG, out float throughB);
                inR += scatterR;
                inG += scatterG;
                inB += scatterB;
                outR += throughR;
                outG += throughG;
                outB += throughB;
            }

            red = Solve(hazeR, inR, outR);
            green = Solve(hazeG, inG, outG);
            blue = Solve(hazeB, inB, outB);
        }

        /// <summary>
        /// The sky's own colour (linear, before the exposure) just above the horizon, averaged round it: the haze a
        /// fog colour should match.
        /// </summary>
        public static void HorizonHaze(float sunX, float sunY, float sunZ, float tintR, float tintG, float tintB,
            float atmosphereThickness, out float red, out float green, out float blue)
        {
            Constants constants = new(tintR, tintG, tintB, atmosphereThickness);
            Normalize(ref sunX, ref sunY, ref sunZ);
            red = 0f;
            green = 0f;
            blue = 0f;
            for (int index = 0; index < Azimuths; index++)
            {
                double angle = index * 2.0 * Math.PI / Azimuths;
                Sky(in constants, Ray((float)Math.Cos(angle), HazeElevation, (float)Math.Sin(angle)), sunX, sunY,
                    sunZ, out float r, out float g, out float b);
                red += r / Azimuths;
                green += g / Azimuths;
                blue += b / Azimuths;
            }
        }

        /// <summary>
        /// The sky colour (linear, before the exposure) the procedural sky draws towards <paramref name="ray"/>,
        /// with its ground colour <paramref name="groundR"/>.. below the horizon; no sun disc.
        /// </summary>
        public static void SkyColor(float rayX, float rayY, float rayZ, float sunX, float sunY, float sunZ,
            float tintR, float tintG, float tintB, float atmosphereThickness, float groundR, float groundG,
            float groundB, out float red, out float green, out float blue)
        {
            Constants constants = new(tintR, tintG, tintB, atmosphereThickness);
            Normalize(ref sunX, ref sunY, ref sunZ);
            Normalize(ref rayX, ref rayY, ref rayZ);
            Vector ray = new(rayX, rayY, rayZ);
            if (rayY >= 0f)
            {
                Sky(in constants, ray, sunX, sunY, sunZ, out red, out green, out blue);
                return;
            }

            Ground(in constants, ray, sunX, sunY, sunZ, out float inR, out float inG, out float inB, out float outR,
                out float outG, out float outB);
            red = inR + groundR * outR;
            green = inG + groundG * outG;
            blue = inB + groundB * outB;
        }

        private static float Solve(float haze, float inScatter, float attenuation)
        {
            if (attenuation <= 1e-6f)
            {
                return 0f;
            }

            float ground = (haze - inScatter) / attenuation;
            return ground < 0f ? 0f : ground > MaxGroundChannel ? MaxGroundChannel : ground;
        }

        private static Vector Ray(float x, float y, float z)
        {
            float horizontal = (float)Math.Sqrt(Math.Max(0f, 1f - y * y));
            return new Vector(x * horizontal, y, z * horizontal);
        }

        private static void Sky(in Constants c, Vector ray, float sunX, float sunY, float sunZ, out float red,
            out float green, out float blue)
        {
            Vector camera = new(0f, InnerRadius + CameraHeight, 0f);
            float far = (float)Math.Sqrt(OuterRadius * OuterRadius + InnerRadius * InnerRadius * ray.Y * ray.Y
                                         - InnerRadius * InnerRadius) - InnerRadius * ray.Y;
            float height = InnerRadius + CameraHeight;
            float depth = (float)Math.Exp(ScaleOverScaleDepth * -CameraHeight);
            float startAngle = Dot(ray, camera) / height;
            float startOffset = depth * Scale(startAngle);
            float sampleLength = far / 2f;
            float scaledLength = sampleLength * ScaleFactor;
            Vector sampleRay = ray * sampleLength;
            Vector sample = camera + sampleRay * 0.5f;
            float frontR = 0f;
            float frontG = 0f;
            float frontB = 0f;
            for (int step = 0; step < 2; step++)
            {
                float sampleHeight = sample.Length;
                float sampleDepth = (float)Math.Exp(ScaleOverScaleDepth * (InnerRadius - sampleHeight));
                float lightAngle = (sunX * sample.X + sunY * sample.Y + sunZ * sample.Z) / sampleHeight;
                float cameraAngle = Dot(ray, sample) / sampleHeight;
                float scatter = startOffset + sampleDepth * (Scale(lightAngle) - Scale(cameraAngle));
                float clamped = Clamp(scatter, 0f, MaxScatter);
                float weight = sampleDepth * scaledLength;
                frontR += (float)Math.Exp(-clamped * c.ExtinctionR) * weight;
                frontG += (float)Math.Exp(-clamped * c.ExtinctionG) * weight;
                frontB += (float)Math.Exp(-clamped * c.ExtinctionB) * weight;
                sample = sample + sampleRay;
            }

            float cosine = -(sunX * ray.X + sunY * ray.Y + sunZ * ray.Z);
            float phase = 0.75f + 0.75f * cosine * cosine;
            red = frontR * c.InvWavelengthR * c.RayleighSun * phase;
            green = frontG * c.InvWavelengthG * c.RayleighSun * phase;
            blue = frontB * c.InvWavelengthB * c.RayleighSun * phase;
        }

        private static void Ground(in Constants c, Vector ray, float sunX, float sunY, float sunZ, out float inR,
            out float inG, out float inB, out float outR, out float outG, out float outB)
        {
            Vector camera = new(0f, InnerRadius + CameraHeight, 0f);
            float far = -CameraHeight / Math.Min(-0.001f, ray.Y);
            Vector position = camera + ray * far;
            float depth = (float)Math.Exp(-CameraHeight * (1f / ScaleDepth));
            float cameraAngle = -Dot(ray, position);
            float lightAngle = sunX * position.X + sunY * position.Y + sunZ * position.Z;
            float cameraScale = Scale(cameraAngle);
            float lightScale = Scale(lightAngle);
            float cameraOffset = depth * cameraScale;
            float total = lightScale + cameraScale;
            float scaledLength = far * ScaleFactor;
            Vector sample = camera + ray * (far * 0.5f);
            float sampleDepth = (float)Math.Exp(ScaleOverScaleDepth * (InnerRadius - sample.Length));
            float scatter = Clamp(sampleDepth * total - cameraOffset, 0f, MaxScatter);
            float attenuationR = (float)Math.Exp(-scatter * c.ExtinctionR);
            float attenuationG = (float)Math.Exp(-scatter * c.ExtinctionG);
            float attenuationB = (float)Math.Exp(-scatter * c.ExtinctionB);
            float weight = sampleDepth * scaledLength;
            inR = attenuationR * weight * (c.InvWavelengthR * c.RayleighSun + c.MieSun);
            inG = attenuationG * weight * (c.InvWavelengthG * c.RayleighSun + c.MieSun);
            inB = attenuationB * weight * (c.InvWavelengthB * c.RayleighSun + c.MieSun);
            outR = Clamp(attenuationR, 0f, 1f);
            outG = Clamp(attenuationG, 0f, 1f);
            outB = Clamp(attenuationB, 0f, 1f);
        }

        private static float Scale(float cosine)
        {
            float x = 1f - cosine;
            return 0.25f * (float)Math.Exp(-0.00287f + x * (0.459f + x * (3.83f + x * (-6.80f + x * 5.25f))));
        }

        private static float Dot(Vector a, Vector b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        }

        private static float Clamp(float value, float low, float high)
        {
            return value < low ? low : value > high ? high : value;
        }

        private static void Normalize(ref float x, ref float y, ref float z)
        {
            float length = (float)Math.Sqrt(x * x + y * y + z * z);
            if (length < 1e-6f)
            {
                x = 0f;
                y = 1f;
                z = 0f;
                return;
            }

            x /= length;
            y /= length;
            z /= length;
        }

        private readonly struct Vector
        {
            public Vector(float x, float y, float z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            public float X { get; }

            public float Y { get; }

            public float Z { get; }

            public float Length => (float)Math.Sqrt(X * X + Y * Y + Z * Z);

            public static Vector operator +(Vector a, Vector b)
            {
                return new Vector(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
            }

            public static Vector operator *(Vector a, float k)
            {
                return new Vector(a.X * k, a.Y * k, a.Z * k);
            }
        }

        private readonly struct Constants
        {
            public Constants(float tintR, float tintG, float tintB, float thickness)
            {
                float rayleigh = 0.0025f * (float)Math.Pow(Math.Max(0f, thickness), 2.5);
                InvWavelengthR = InverseWavelength(0.65f, tintR);
                InvWavelengthG = InverseWavelength(0.57f, tintG);
                InvWavelengthB = InverseWavelength(0.475f, tintB);
                RayleighSun = rayleigh * SunBrightness;
                MieSun = Mie * SunBrightness;
                float rayleigh4Pi = rayleigh * 4f * (float)Math.PI;
                float mie4Pi = Mie * 4f * (float)Math.PI;
                ExtinctionR = InvWavelengthR * rayleigh4Pi + mie4Pi;
                ExtinctionG = InvWavelengthG * rayleigh4Pi + mie4Pi;
                ExtinctionB = InvWavelengthB * rayleigh4Pi + mie4Pi;
            }

            public float InvWavelengthR { get; }

            public float InvWavelengthG { get; }

            public float InvWavelengthB { get; }

            public float RayleighSun { get; }

            public float MieSun { get; }

            public float ExtinctionR { get; }

            public float ExtinctionG { get; }

            public float ExtinctionB { get; }

            private static float InverseWavelength(float wavelength, float tint)
            {
                // WHY the tint as authored: the sky shader converts it back to gamma before using it, so a tint of
                // 0.5 keeps the default wavelength.
                float shifted = (wavelength - 0.15f) + 0.3f * (1f - tint);
                return 1f / (float)Math.Pow(shifted, 4);
            }
        }
    }
}
