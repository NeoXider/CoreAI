using System;
using System.Globalization;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Roblox <c>Lighting</c>, the day-cycle slice: <c>ClockTime</c> and <c>TimeOfDay</c> (one clock, two
    /// views), <c>GeographicLatitude</c>, <c>Brightness</c>, <c>Ambient</c>, <c>OutdoorAmbient</c>,
    /// <c>EnvironmentDiffuseScale</c>, <c>EnvironmentSpecularScale</c>, <c>GetMinutesAfterMidnight</c>,
    /// <c>SetMinutesAfterMidnight</c>, <c>GetSunDirection</c> and
    /// <c>LightingChanged</c>. Defaults are the class defaults of <c>Full-API-Dump.json</c>. Every real
    /// change fires <c>Changed</c> with the property name, that property's
    /// <c>GetPropertyChangedSignal</c> and <c>LightingChanged(false)</c>; an equal write fires nothing.
    /// What the state looks like on screen is the attached <see cref="IRbxLightingSink"/>'s business.
    /// </summary>
    public sealed class RbxLighting : RbxInstance
    {
        /// <summary><c>Full-API-Dump.json</c>: <c>ClockTime</c> 14.</summary>
        public const float DefaultClockTime = 14f;

        /// <summary><c>Full-API-Dump.json</c>: <c>GeographicLatitude</c> 41.7332993.</summary>
        public const float DefaultGeographicLatitude = 41.7332993f;

        /// <summary><c>Full-API-Dump.json</c>: <c>Brightness</c> 1.</summary>
        public const float DefaultBrightness = 1f;

        /// <summary>
        /// <c>EnvironmentDiffuseScale</c> of a new place: 1.
        /// </summary>
        /// <remarks>
        /// WHY 1 and not the dump's class default 0: this service stands for the Lighting of a new place, and
        /// Roblox's place templates set both environment scales to 1; at 0 every world would lose the sky
        /// light and sky reflections it has always had.
        /// </remarks>
        public const float DefaultEnvironmentDiffuseScale = 1f;

        /// <summary><c>EnvironmentSpecularScale</c> of a new place: 1 (see <see cref="DefaultEnvironmentDiffuseScale"/>).</summary>
        public const float DefaultEnvironmentSpecularScale = 1f;

        private const double SecondsPerDay = 86400d;
        private const double HoursPerDay = 24d;
        private const double MinutesPerDay = 1440d;
        private static readonly object[] NoSkyChange = { false };

        private float _clockTime = DefaultClockTime;
        private float _geographicLatitude = DefaultGeographicLatitude;
        private float _brightness = DefaultBrightness;
        private RbxColor3 _ambient = DefaultAmbient;
        private RbxColor3 _outdoorAmbient = DefaultOutdoorAmbient;
        private float _environmentDiffuseScale = DefaultEnvironmentDiffuseScale;
        private float _environmentSpecularScale = DefaultEnvironmentSpecularScale;
        private IRbxLightingSink _sink;
        private bool _sinkFaultReported;
        private bool _restoring;

        internal RbxLighting(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Name = "Lighting";
        }

        /// <summary><c>Full-API-Dump.json</c>: <c>Ambient</c> (0.5, 0.5, 0.5).</summary>
        public static RbxColor3 DefaultAmbient => new(0.5f, 0.5f, 0.5f);

        /// <summary><c>Full-API-Dump.json</c>: <c>OutdoorAmbient</c> (0.5, 0.5, 0.5).</summary>
        public static RbxColor3 DefaultOutdoorAmbient => new(0.5f, 0.5f, 0.5f);

        /// <summary>
        /// True once this world drives its lighting: a script changed a day-cycle property, a saved
        /// <c>lighting</c> block was restored, or the host called <see cref="Drive"/>. Until then the
        /// sink is never called, so a host scene's own lighting stays as authored.
        /// </summary>
        public bool IsDriven { get; private set; }

        /// <summary>The sink the state goes to; null keeps it in memory only.</summary>
        public IRbxLightingSink Sink => _sink;

        /// <summary>How many times the state reached the sink (diagnostics and tests).</summary>
        public int SinkApplyCount { get; private set; }

        /// <summary>Mirror <c>Lighting.LightingChanged(skyChanged)</c>; skyChanged is false for every property this slice models.</summary>
        public RbxScriptSignal LightingChanged => GetOrCreateSignal("LightingChanged");

        /// <summary>Hours after midnight in [0, 24); a write outside it wraps into the day it names.</summary>
        public float ClockTime => _clockTime;

        /// <summary><c>ClockTime</c> as a 24-hour <c>"HH:MM:SS"</c> string, to the nearest second.</summary>
        public string TimeOfDay => FormatTimeOfDay(_clockTime);

        /// <summary>Degrees north; tilts the sun's path (see <see cref="RbxSunModel"/>).</summary>
        public float GeographicLatitude => _geographicLatitude;

        /// <summary>The sun's intensity.</summary>
        public float Brightness => _brightness;

        /// <summary>The hue of areas the sky does not reach.</summary>
        public RbxColor3 Ambient => _ambient;

        /// <summary>The hue of outdoor areas.</summary>
        public RbxColor3 OutdoorAmbient => _outdoorAmbient;

        /// <summary>Share of the sky's own light in the ambient, [0, 1].</summary>
        public float EnvironmentDiffuseScale => _environmentDiffuseScale;

        /// <summary>Share of the sky's reflections on smooth and metal surfaces, [0, 1].</summary>
        public float EnvironmentSpecularScale => _environmentSpecularScale;

        /// <summary>Attaches the sink (null detaches); a world that already drives its lighting pushes to it at once.</summary>
        public void AttachSink(IRbxLightingSink sink)
        {
            _sink = sink;
            _sinkFaultReported = false;
            if (IsDriven)
            {
                PushToSink();
            }
        }

        /// <summary>Makes the world drive its lighting from now on and pushes the current state.</summary>
        public void Drive()
        {
            IsDriven = true;
            PushToSink();
        }

        /// <summary>Sets <c>ClockTime</c> in hours; any finite value wraps into [0, 24).</summary>
        public void SetClockTime(double hours)
        {
            RequireFinite(hours, "ClockTime");
            SetClock(WrapHours(hours));
        }

        /// <summary>Sets the clock from a <c>"H"</c>, <c>"H:M"</c> or <c>"H:M:S"</c> string of whole numbers.</summary>
        public void SetTimeOfDay(string timeOfDay)
        {
            if (!TryParseTimeOfDay(timeOfDay, out double hours))
            {
                throw RbxError.BadArgument(
                    "Lighting.TimeOfDay expects a 24-hour time such as \"06:30:00\", got \""
                    + Shorten(timeOfDay) + "\"",
                    "assign hours, minutes and seconds as whole numbers separated by colons, e.g. "
                    + "Lighting.TimeOfDay = \"21:30\", or set Lighting.ClockTime = 21.5");
            }

            SetClock(WrapHours(hours));
        }

        /// <summary>Mirror <c>Lighting:GetMinutesAfterMidnight()</c>: minutes into the current day.</summary>
        public double GetMinutesAfterMidnight()
        {
            return _clockTime * 60d;
        }

        /// <summary>Mirror <c>Lighting:SetMinutesAfterMidnight(minutes)</c>: values past a day land in the next one.</summary>
        public void SetMinutesAfterMidnight(double minutes)
        {
            RequireFinite(minutes, "SetMinutesAfterMidnight");
            double wrapped = minutes % MinutesPerDay;
            if (wrapped < 0d)
            {
                wrapped += MinutesPerDay;
            }

            SetClock(WrapHours(wrapped / 60d));
        }

        /// <summary>Mirror <c>Lighting:GetSunDirection()</c>: unit vector towards the sun, below the horizon at night.</summary>
        public RbxVector3 GetSunDirection()
        {
            return RbxSunModel.SunDirection(_clockTime, _geographicLatitude);
        }

        public void SetGeographicLatitude(double degrees)
        {
            RequireFinite(degrees, "GeographicLatitude");
            float next = (float)degrees;
            if (_geographicLatitude.Equals(next))
            {
                return;
            }

            _geographicLatitude = next;
            Changed(nameof(GeographicLatitude));
        }

        public void SetBrightness(double brightness)
        {
            RequireFinite(brightness, "Brightness");
            float next = (float)brightness;
            if (_brightness.Equals(next))
            {
                return;
            }

            _brightness = next;
            Changed(nameof(Brightness));
        }

        public void SetAmbient(RbxColor3 color)
        {
            RequireFinite(color, "Ambient");
            if (_ambient == color)
            {
                return;
            }

            _ambient = color;
            Changed(nameof(Ambient));
        }

        public void SetOutdoorAmbient(RbxColor3 color)
        {
            RequireFinite(color, "OutdoorAmbient");
            if (_outdoorAmbient == color)
            {
                return;
            }

            _outdoorAmbient = color;
            Changed(nameof(OutdoorAmbient));
        }

        /// <summary>Sets <c>EnvironmentDiffuseScale</c>; Roblox clamps it into [0, 1].</summary>
        public void SetEnvironmentDiffuseScale(double scale)
        {
            RequireFinite(scale, "EnvironmentDiffuseScale");
            float next = (float)Math.Max(0d, Math.Min(1d, scale));
            if (_environmentDiffuseScale.Equals(next))
            {
                return;
            }

            _environmentDiffuseScale = next;
            Changed(nameof(EnvironmentDiffuseScale));
        }

        /// <summary>Sets <c>EnvironmentSpecularScale</c>; Roblox clamps it into [0, 1].</summary>
        public void SetEnvironmentSpecularScale(double scale)
        {
            RequireFinite(scale, "EnvironmentSpecularScale");
            float next = (float)Math.Max(0d, Math.Min(1d, scale));
            if (_environmentSpecularScale.Equals(next))
            {
                return;
            }

            _environmentSpecularScale = next;
            Changed(nameof(EnvironmentSpecularScale));
        }

        /// <summary>The current state as a sink receives it.</summary>
        public RbxLightingState CaptureState()
        {
            return new RbxLightingState(_clockTime, _geographicLatitude, _brightness, _ambient,
                _outdoorAmbient, GetSunDirection(), _environmentDiffuseScale, _environmentSpecularScale);
        }

        /// <summary>What the world package stores in its <c>lighting</c> block.</summary>
        public RbxLightingDurableState CaptureDurableState()
        {
            return new RbxLightingDurableState(_clockTime, _geographicLatitude, _brightness, _ambient,
                _outdoorAmbient, _environmentDiffuseScale, _environmentSpecularScale);
        }

        /// <summary>
        /// Puts a saved <c>lighting</c> block back through the public setters (so a world loaded into a
        /// running session fires the change signals a script would), then drives the lighting. The
        /// registry revision of the service is left where the restored tree stamped it.
        /// </summary>
        /// <remarks>
        /// WHY no revision advance: the revision is part of the saved world, so advancing it here made a
        /// load followed by a save differ from the file it loaded; a restore reproduces saved state, it
        /// is not a new change.
        /// </remarks>
        public void RestoreDurableState(in RbxLightingDurableState state)
        {
            _restoring = true;
            try
            {
                SetClockTime(state.ClockTime);
                SetGeographicLatitude(state.GeographicLatitude);
                SetBrightness(state.Brightness);
                SetAmbient(state.Ambient);
                SetOutdoorAmbient(state.OutdoorAmbient);
                SetEnvironmentDiffuseScale(state.EnvironmentDiffuseScale);
                SetEnvironmentSpecularScale(state.EnvironmentSpecularScale);
            }
            finally
            {
                _restoring = false;
            }

            Drive();
        }

        /// <summary>
        /// Parses Roblox's <c>TimeOfDay</c> text: one to three colon-separated whole numbers (hours,
        /// minutes, seconds), surrounding spaces allowed; the total wraps into one day.
        /// </summary>
        public static bool TryParseTimeOfDay(string text, out double hours)
        {
            hours = 0d;
            if (text == null)
            {
                return false;
            }

            string[] parts = text.Trim().Split(':');
            if (parts.Length < 1 || parts.Length > 3)
            {
                return false;
            }

            double seconds = 0d;
            double[] scale = { 3600d, 60d, 1d };
            for (int index = 0; index < parts.Length; index++)
            {
                string part = parts[index];
                if (part.Length == 0 || part.Length > 9)
                {
                    return false;
                }

                for (int c = 0; c < part.Length; c++)
                {
                    if (part[c] < '0' || part[c] > '9')
                    {
                        return false;
                    }
                }

                seconds += int.Parse(part, NumberStyles.None, CultureInfo.InvariantCulture) * scale[index];
            }

            hours = WrapHours((seconds % SecondsPerDay) / 3600d);
            return true;
        }

        /// <summary><c>"HH:MM:SS"</c> for <paramref name="clockTime"/> hours, rounded to the second.</summary>
        public static string FormatTimeOfDay(double clockTime)
        {
            long total = (long)Math.Round(clockTime * 3600d, MidpointRounding.AwayFromZero);
            total %= (long)SecondsPerDay;
            if (total < 0)
            {
                total += (long)SecondsPerDay;
            }

            long hours = total / 3600;
            long minutes = total / 60 % 60;
            long seconds = total % 60;
            return hours.ToString("00", CultureInfo.InvariantCulture) + ":"
                   + minutes.ToString("00", CultureInfo.InvariantCulture) + ":"
                   + seconds.ToString("00", CultureInfo.InvariantCulture);
        }

        private void SetClock(float hours)
        {
            if (_clockTime.Equals(hours))
            {
                return;
            }

            string timeBefore = TimeOfDay;
            _clockTime = hours;
            NotifyPropertyChanged(nameof(ClockTime));
            if (!string.Equals(timeBefore, TimeOfDay, StringComparison.Ordinal))
            {
                NotifyPropertyChanged(nameof(TimeOfDay));
            }

            AfterChange();
        }

        private void Changed(string propertyName)
        {
            NotifyPropertyChanged(propertyName);
            AfterChange();
        }

        private void AfterChange()
        {
            if (!_restoring)
            {
                Registry?.AdvanceRevision(Id);
            }

            RbxScriptSignal lightingChanged = LightingChanged;
            if (lightingChanged.HasConnections)
            {
                lightingChanged.Fire(NoSkyChange);
            }

            IsDriven = true;
            PushToSink();
        }

        private void PushToSink()
        {
            IRbxLightingSink sink = _sink;
            if (sink == null || IsDestroyed)
            {
                return;
            }

            try
            {
                SinkApplyCount++;
                sink.Apply(CaptureState());
            }
            catch (Exception exception)
            {
                // WHY contained: the sink is host rendering code reached from a script's property write,
                // and a renderer fault must not become that script's error (it would be quarantined for
                // a host problem it cannot fix); it is reported once per attached sink instead.
                if (!_sinkFaultReported)
                {
                    _sinkFaultReported = true;
                    Registry?.ReportDiagnostic("[CoreAI.RbxApi] the Lighting sink "
                                               + sink.GetType().Name + " failed and was skipped: "
                                               + exception.Message);
                }
            }
        }

        private static float WrapHours(double hours)
        {
            double wrapped = hours % HoursPerDay;
            if (wrapped < 0d)
            {
                wrapped += HoursPerDay;
            }

            float narrowed = (float)wrapped;
            return narrowed >= (float)HoursPerDay ? 0f : narrowed;
        }

        private static void RequireFinite(double value, string member)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw RbxError.BadArgument(
                    "Lighting." + member + " needs a finite number, got " + value.ToString(CultureInfo.InvariantCulture),
                    "check the arithmetic that produced it (0/0, math.huge) before assigning");
            }
        }

        private static void RequireFinite(RbxColor3 color, string member)
        {
            if (float.IsNaN(color.R) || float.IsInfinity(color.R) || float.IsNaN(color.G)
                || float.IsInfinity(color.G) || float.IsNaN(color.B) || float.IsInfinity(color.B))
            {
                throw RbxError.BadArgument(
                    "Lighting." + member + " needs a Color3 with finite components",
                    "check the arithmetic that produced the color before assigning");
            }
        }

        private static string Shorten(string text)
        {
            if (text == null)
            {
                return "";
            }

            return text.Length > 32 ? text.Substring(0, 32) + "..." : text;
        }
    }
}
