using UnityEngine;

namespace CoreAI.Mods.Rbx.Rendering
{
    /// <summary>
    /// Clock of the animated Rbx water. By default the waves follow the engine shader time; a capture test
    /// pins them to an exact time with <see cref="SetOverride"/> so two frames taken a known interval apart
    /// are reproducible.
    /// </summary>
    public static class RbxWaterTime
    {
        /// <summary>Global shader float that holds the pinned time in seconds.</summary>
        public const string OverrideProperty = "_RbxWaterTimeOverride";

        /// <summary>Global shader float that is 1 while the time is pinned.</summary>
        public const string OverrideEnabledProperty = "_RbxWaterTimeOverrideEnabled";

        /// <summary>True while the water time is pinned.</summary>
        public static bool IsOverridden { get; private set; }

        /// <summary>Pins the water animation to <paramref name="seconds"/>.</summary>
        public static void SetOverride(float seconds)
        {
            IsOverridden = true;
            Shader.SetGlobalFloat(OverrideProperty, seconds);
            Shader.SetGlobalFloat(OverrideEnabledProperty, 1f);
        }

        /// <summary>Lets the water follow the engine time again.</summary>
        public static void ClearOverride()
        {
            IsOverridden = false;
            Shader.SetGlobalFloat(OverrideEnabledProperty, 0f);
        }
    }
}
