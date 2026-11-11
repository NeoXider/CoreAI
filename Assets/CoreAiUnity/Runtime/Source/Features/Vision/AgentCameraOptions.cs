using System;

namespace CoreAI.Vision
{
    /// <summary>
    /// The rules of the agent camera, shared by <see cref="AgentCameraService"/> (capture rate limit, the free
    /// agent camera) and <see cref="AgentCameraRig"/> (field-of-view, pitch and orbit-distance limits). Create one
    /// with <see cref="CreateDefault"/>, change what the game needs and pass the same instance to both:
    /// <code>
    /// AgentCameraOptions options = AgentCameraOptions.CreateDefault();
    /// options.AllowFreeCamera = false;          // agents may only move cameras a designer marked
    /// options.MaxOrbitDistance = 200f;
    /// IAgentCameraService service = new AgentCameraService(options);
    /// </code>
    /// Values are read on every call, so changing them at runtime takes effect on the next tool call.
    /// </summary>
    public sealed class AgentCameraOptions
    {
        /// <summary>Default base name of the free agent camera GameObject; the role id is appended in parentheses.</summary>
        public const string DefaultFreeCameraName = "CoreAI Agent Camera";

        /// <summary>Minimum seconds between two captures of the same agent role; 0 or less turns the limit off.</summary>
        public double MinCaptureIntervalSeconds { get; set; } = AgentCameraService.DefaultMinCaptureIntervalSeconds;

        /// <summary>
        /// When true (default), an agent that asks to move its own camera and has none gets a free camera created on
        /// demand: a new, disabled camera that starts at the main camera's pose and never renders to the screen.
        /// When false, agents may only move cameras a designer marked with <see cref="CoreAiAgentCamera"/>.
        /// </summary>
        public bool AllowFreeCamera { get; set; } = true;

        /// <summary>Base name of the free agent camera GameObject (<see cref="DefaultFreeCameraName"/> by default).</summary>
        public string FreeCameraName { get; set; } = DefaultFreeCameraName;

        /// <summary>Smallest vertical field of view, in degrees, an agent may set.</summary>
        public float MinFieldOfView { get; set; } = 10f;

        /// <summary>Largest vertical field of view, in degrees, an agent may set.</summary>
        public float MaxFieldOfView { get; set; } = 120f;

        /// <summary>Largest orbit pitch magnitude in degrees; kept below 90 so the view never flips over the pole.</summary>
        public float MaxPitch { get; set; } = 89f;

        /// <summary>Closest orbit distance in world units.</summary>
        public float MinOrbitDistance { get; set; } = 0.5f;

        /// <summary>Farthest orbit distance in world units.</summary>
        public float MaxOrbitDistance { get; set; } = 1000f;

        /// <summary>Orbit distance used when none is given and the camera sits on the pivot of a point target.</summary>
        public float DefaultOrbitDistance { get; set; } = 10f;

        /// <summary>A new instance with the shipped values.</summary>
        public static AgentCameraOptions CreateDefault()
        {
            return new AgentCameraOptions();
        }

        /// <summary>Clamps a field of view into <see cref="MinFieldOfView"/>..<see cref="MaxFieldOfView"/>.</summary>
        public float ClampFieldOfView(float degrees)
        {
            float min = Math.Max(1f, Math.Min(MinFieldOfView, MaxFieldOfView));
            float max = Math.Min(179f, Math.Max(MinFieldOfView, MaxFieldOfView));
            return Clamp(degrees, min, max);
        }

        /// <summary>Clamps a pitch into <c>-MaxPitch..MaxPitch</c> (<see cref="MaxPitch"/> itself capped at 89.9).</summary>
        public float ClampPitch(float degrees)
        {
            float limit = Math.Max(0f, Math.Min(89.9f, Math.Abs(MaxPitch)));
            return Clamp(degrees, -limit, limit);
        }

        /// <summary>Clamps an orbit distance into <see cref="MinOrbitDistance"/>..<see cref="MaxOrbitDistance"/>.</summary>
        public float ClampOrbitDistance(float distance)
        {
            float min = Math.Max(0.01f, Math.Min(MinOrbitDistance, MaxOrbitDistance));
            float max = Math.Max(min, Math.Max(MinOrbitDistance, MaxOrbitDistance));
            return Clamp(distance, min, max);
        }

        private static float Clamp(float value, float min, float max)
        {
            if (float.IsNaN(value))
            {
                return min;
            }

            return value < min ? min : value > max ? max : value;
        }
    }
}
