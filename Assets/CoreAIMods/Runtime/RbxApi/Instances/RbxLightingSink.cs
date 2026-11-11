using System;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Where a world's <c>Lighting</c> service puts its day-cycle state: the sun, the ambient colours and
    /// the sky of whatever renders the world. The engine-free half of the Lighting slice ends here, so Lua
    /// never touches an engine type; the Unity default drives <c>RenderSettings</c> and the scene's sun
    /// light, a headless world keeps none, and a host replaces it with its own (the look of its game).
    /// </summary>
    public interface IRbxLightingSink
    {
        /// <summary>
        /// Puts <paramref name="state"/> on screen. Called after every change that alters it, once the
        /// world drives its lighting (<see cref="RbxLighting.IsDriven"/>), and once when the sink is
        /// attached to a world that already does. Must be idempotent and cheap: a day cycle writes
        /// <c>ClockTime</c> every frame.
        /// </summary>
        void Apply(in RbxLightingState state);
    }

    /// <summary>The day-cycle state of one <c>Lighting</c> service, as a sink receives it.</summary>
    public readonly struct RbxLightingState : IEquatable<RbxLightingState>
    {
        public RbxLightingState(float clockTime, float geographicLatitude, float brightness,
            RbxColor3 ambient, RbxColor3 outdoorAmbient, RbxVector3 sunDirection)
        {
            ClockTime = clockTime;
            GeographicLatitude = geographicLatitude;
            Brightness = brightness;
            Ambient = ambient;
            OutdoorAmbient = outdoorAmbient;
            SunDirection = sunDirection;
        }

        /// <summary>Hours after midnight, [0, 24).</summary>
        public float ClockTime { get; }

        /// <summary>Degrees north.</summary>
        public float GeographicLatitude { get; }

        /// <summary>Intensity of the sun's light as the script set it (never clamped here).</summary>
        public float Brightness { get; }

        /// <summary>The hue of areas the sky does not reach.</summary>
        public RbxColor3 Ambient { get; }

        /// <summary>The hue of outdoor areas.</summary>
        public RbxColor3 OutdoorAmbient { get; }

        /// <summary>Unit vector from the origin towards the sun, in Roblox space (<c>Lighting:GetSunDirection()</c>).</summary>
        public RbxVector3 SunDirection { get; }

        public bool Equals(RbxLightingState other)
        {
            return ClockTime.Equals(other.ClockTime)
                   && GeographicLatitude.Equals(other.GeographicLatitude)
                   && Brightness.Equals(other.Brightness)
                   && Ambient == other.Ambient
                   && OutdoorAmbient == other.OutdoorAmbient
                   && SunDirection == other.SunDirection;
        }

        public override bool Equals(object obj)
        {
            return obj is RbxLightingState other && Equals(other);
        }

        public override int GetHashCode()
        {
            return ClockTime.GetHashCode() ^ (GeographicLatitude.GetHashCode() * 397)
                                           ^ (Brightness.GetHashCode() * 17);
        }
    }

    /// <summary>
    /// The durable part of a <c>Lighting</c> service: the values the world package stores in its optional
    /// <c>lighting</c> block and restores through <see cref="RbxLighting.RestoreDurableState"/>.
    /// </summary>
    public readonly struct RbxLightingDurableState
    {
        public RbxLightingDurableState(float clockTime, float geographicLatitude, float brightness,
            RbxColor3 ambient, RbxColor3 outdoorAmbient)
        {
            ClockTime = clockTime;
            GeographicLatitude = geographicLatitude;
            Brightness = brightness;
            Ambient = ambient;
            OutdoorAmbient = outdoorAmbient;
        }

        /// <summary>Hours after midnight; <c>TimeOfDay</c> is derived from it and not stored.</summary>
        public float ClockTime { get; }

        public float GeographicLatitude { get; }

        public float Brightness { get; }

        public RbxColor3 Ambient { get; }

        public RbxColor3 OutdoorAmbient { get; }

        /// <summary>The class defaults of <c>Full-API-Dump.json</c>.</summary>
        public static RbxLightingDurableState Defaults => new(
            RbxLighting.DefaultClockTime, RbxLighting.DefaultGeographicLatitude, RbxLighting.DefaultBrightness,
            RbxLighting.DefaultAmbient, RbxLighting.DefaultOutdoorAmbient);
    }

    /// <summary>
    /// The sun model behind <c>Lighting:GetSunDirection()</c>, <c>ClockTime</c> and
    /// <c>GeographicLatitude</c>: solar noon at 12:00, the hour angle turning 15 degrees an hour, the
    /// observer at <c>GeographicLatitude</c>, and the sun on the celestial equator (the equinox), so it
    /// rises at 06:00 due east and sets at 18:00 due west at every latitude. East is +X and north is -Z,
    /// so a northern latitude's noon sun leans towards +Z.
    /// </summary>
    /// <remarks>
    /// WHY a documented model and not Roblox's: the mirror says only that the earth's tilt is taken into
    /// account, not the day of the year it is evaluated at nor the axes; the equinox keeps the 06:00 and
    /// 18:00 horizon crossings day-cycle scripts are written around (DEV candidate P9-f).
    /// </remarks>
    public static class RbxSunModel
    {
        private const double DegreesToRadians = Math.PI / 180d;

        /// <summary>Unit vector towards the sun at <paramref name="clockTime"/> hours and <paramref name="latitude"/> degrees.</summary>
        public static RbxVector3 SunDirection(double clockTime, double latitude)
        {
            double hourAngle = (clockTime - 12d) * 15d * DegreesToRadians;
            double phi = latitude * DegreesToRadians;
            double cosHour = Math.Cos(hourAngle);
            double east = -Math.Sin(hourAngle);
            double up = Math.Cos(phi) * cosHour;
            double north = -Math.Sin(phi) * cosHour;
            return new RbxVector3((float)east, (float)up, (float)-north);
        }

        /// <summary>The sun's height above the horizon, degrees (negative below it).</summary>
        public static double ElevationDegrees(RbxVector3 sunDirection)
        {
            double y = Math.Max(-1d, Math.Min(1d, sunDirection.Y));
            return Math.Asin(y) / DegreesToRadians;
        }
    }

    /// <summary>
    /// How the default sinks turn a <see cref="RbxLightingState"/> into light: daylight fades over the
    /// sunrise and sunset band around the horizon, the sun's intensity is <c>Brightness</c> times
    /// daylight, the outdoor ambient follows daylight down to a night floor, and <c>Ambient</c> (the
    /// sky-occluded hue) stays as set at every hour. Engine-free, so a host sink and the tests share it.
    /// </summary>
    /// <remarks>
    /// WHY a night floor on the outdoor ambient and none on <c>Ambient</c>: the mirror documents neither
    /// how bright a Roblox night is nor how its sky light falls, only that occluded areas take
    /// <c>Ambient</c>; an outdoor ambient held at noon strength would light a midnight field like a
    /// cloudy noon (DEV candidate P9-g).
    /// </remarks>
    public static class RbxLightingMapping
    {
        /// <summary>Sun elevation, degrees, at which daylight reaches zero.</summary>
        public const double TwilightEndDegrees = -6d;

        /// <summary>Sun elevation, degrees, at which daylight reaches one.</summary>
        public const double FullDaylightDegrees = 6d;

        /// <summary>The share of <c>OutdoorAmbient</c> that remains at night.</summary>
        public const float NightOutdoorAmbientFloor = 0.15f;

        /// <summary>Daylight in [0, 1]: a smooth step over the twilight band.</summary>
        public static float Daylight(in RbxLightingState state)
        {
            double elevation = RbxSunModel.ElevationDegrees(state.SunDirection);
            double t = (elevation - TwilightEndDegrees) / (FullDaylightDegrees - TwilightEndDegrees);
            t = t < 0d ? 0d : t > 1d ? 1d : t;
            return (float)(t * t * (3d - 2d * t));
        }

        /// <summary>The sun light's intensity: <c>Brightness</c> (never below zero) times daylight.</summary>
        public static float SunIntensity(in RbxLightingState state)
        {
            float brightness = state.Brightness > 0f ? state.Brightness : 0f;
            return brightness * Daylight(state);
        }

        /// <summary>The outdoor ambient after daylight: full by day, <see cref="NightOutdoorAmbientFloor"/> at night.</summary>
        public static RbxColor3 EffectiveOutdoorAmbient(in RbxLightingState state)
        {
            float scale = NightOutdoorAmbientFloor + (1f - NightOutdoorAmbientFloor) * Daylight(state);
            RbxColor3 outdoor = state.OutdoorAmbient;
            return new RbxColor3(outdoor.R * scale, outdoor.G * scale, outdoor.B * scale);
        }
    }
}
