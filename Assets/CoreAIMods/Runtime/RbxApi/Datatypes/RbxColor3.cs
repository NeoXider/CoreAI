using System;
using System.Globalization;

namespace CoreAI.Mods.Rbx.Datatypes
{
    /// <summary>Pure-spec Roblox Color3 (float RGB in 0..1).</summary>
    public readonly struct RbxColor3 : IEquatable<RbxColor3>
    {
        public float R { get; }
        public float G { get; }
        public float B { get; }

        public RbxColor3(float r, float g, float b)
        {
            R = r;
            G = g;
            B = b;
        }

        /// <summary>Color3.fromRGB(0..255) — integer channels scaled to 0..1.</summary>
        public static RbxColor3 FromRGB(float r = 0f, float g = 0f, float b = 0f)
        {
            return new RbxColor3(r / 255f, g / 255f, b / 255f);
        }

        /// <summary>Color3.fromHSV(hue, saturation, value) — all in 0..1.</summary>
        public static RbxColor3 FromHSV(float h, float s, float v)
        {
            // WHY: standard HSV sextant conversion; hue 1.0 wraps to 0 like Roblox.
            h = h - MathF.Floor(h);
            float c = v * s;
            float sector = h * 6f;
            float x = c * (1f - MathF.Abs(sector % 2f - 1f));
            float m = v - c;

            float r, g, b;
            if (sector < 1f)
            {
                r = c;
                g = x;
                b = 0f;
            }
            else if (sector < 2f)
            {
                r = x;
                g = c;
                b = 0f;
            }
            else if (sector < 3f)
            {
                r = 0f;
                g = c;
                b = x;
            }
            else if (sector < 4f)
            {
                r = 0f;
                g = x;
                b = c;
            }
            else if (sector < 5f)
            {
                r = x;
                g = 0f;
                b = c;
            }
            else
            {
                r = c;
                g = 0f;
                b = x;
            }

            return new RbxColor3(r + m, g + m, b + m);
        }

        /// <summary>Color3.fromHex("#RGB" / "#RRGGBB", leading '#' optional).</summary>
        public static RbxColor3 FromHex(string hex)
        {
            if (string.IsNullOrEmpty(hex))
            {
                throw RbxApiStubException.BadArgument(
                    "Color3.fromHex expects a hex string.",
                    "pass a string like \"#FF7800\" at argument 1");
            }

            string s = hex[0] == '#' ? hex.Substring(1) : hex;
            if (s.Length == 3)
            {
                s = new string(new[] { s[0], s[0], s[1], s[1], s[2], s[2] });
            }

            if (s.Length != 6 ||
                !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
            {
                throw RbxApiStubException.BadArgument(
                    $"'{hex}' is not a valid hex color.",
                    "use 3 or 6 hex digits, e.g. \"#F80\" or \"#FF7800\"");
            }

            return FromRGB((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }

        public RbxColor3 Lerp(RbxColor3 goal, float alpha)
        {
            return new RbxColor3(
                R + (goal.R - R) * alpha,
                G + (goal.G - G) * alpha,
                B + (goal.B - B) * alpha);
        }

        /// <summary>Color3:ToHSV() — (hue, saturation, value) in 0..1.</summary>
        public (float h, float s, float v) ToHSV()
        {
            float max = MathF.Max(R, MathF.Max(G, B));
            float min = MathF.Min(R, MathF.Min(G, B));
            float delta = max - min;

            float h = 0f;
            if (delta > 0f)
            {
                if (max == R)
                {
                    h = (G - B) / delta % 6f;
                }
                else if (max == G)
                {
                    h = (B - R) / delta + 2f;
                }
                else
                {
                    h = (R - G) / delta + 4f;
                }

                h /= 6f;
                if (h < 0f)
                {
                    h += 1f;
                }
            }

            float s = max > 0f ? delta / max : 0f;
            return (h, s, max);
        }

        /// <summary>Color3:ToHex() — uppercase "RRGGBB" without the leading '#' (Roblox parity).</summary>
        public string ToHex()
        {
            int r = (int)MathF.Round(Math.Clamp(R, 0f, 1f) * 255f);
            int g = (int)MathF.Round(Math.Clamp(G, 0f, 1f) * 255f);
            int b = (int)MathF.Round(Math.Clamp(B, 0f, 1f) * 255f);
            return string.Format(CultureInfo.InvariantCulture, "{0:X2}{1:X2}{2:X2}", r, g, b);
        }

        public static bool operator ==(RbxColor3 a, RbxColor3 b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(RbxColor3 a, RbxColor3 b)
        {
            return !a.Equals(b);
        }

        public bool Equals(RbxColor3 other)
        {
            return R == other.R && G == other.G && B == other.B;
        }

        public override bool Equals(object obj)
        {
            return obj is RbxColor3 c && Equals(c);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(R, G, B);
        }

        /// <summary>Roblox tostring format: "r, g, b".</summary>
        public override string ToString()
        {
            return string.Format(
                CultureInfo.InvariantCulture, "{0}, {1}, {2}", R, G, B);
        }
    }
}
