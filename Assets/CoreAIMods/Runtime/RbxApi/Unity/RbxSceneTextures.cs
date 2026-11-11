using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace CoreAI.Mods.Rbx.Rendering
{
    /// <summary>
    /// Switches the scene-texture features of the Rbx water and Glass shaders: refraction from the camera
    /// opaque texture and depth tint from the camera depth texture. They need a render pipeline that
    /// produces both textures (the PC tier); without them the shaders keep the alpha-blended look, which
    /// is also what Roblox shows on mobile.
    /// </summary>
    /// <remarks>
    /// WHY a global shader keyword set from here: the Rbx runtime assemblies do not reference URP. The
    /// default asks the active pipeline asset for its opaque and depth texture switches by name and, when
    /// it cannot tell, follows the platform tier; a host that knows better overrides it with
    /// <see cref="SetAvailable"/>.
    /// </remarks>
    public static class RbxSceneTextures
    {
        /// <summary>Global shader keyword the water and Glass passes compile against.</summary>
        public const string Keyword = "_RBX_SCENE_TEXTURES";

        private const string OpaqueTextureProperty = "supportsCameraOpaqueTexture";
        private const string DepthTextureProperty = "supportsCameraDepthTexture";

        private static bool? _explicitAvailability;
        private static bool _applied;

        /// <summary>Whether the scene-texture features are on.</summary>
        public static bool Available => _explicitAvailability
                                        ?? PipelineSupport(GraphicsSettings.currentRenderPipeline)
                                        ?? DefaultFor(Application.platform);

        /// <summary>
        /// The platform default when the pipeline cannot be asked: on for desktop players and the Editor,
        /// off for WebGL and mobile, whose pipeline tier renders no opaque or depth texture.
        /// </summary>
        public static bool DefaultFor(RuntimePlatform platform)
        {
            switch (platform)
            {
                case RuntimePlatform.WebGLPlayer:
                case RuntimePlatform.Android:
                case RuntimePlatform.IPhonePlayer:
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Whether <paramref name="pipeline"/> renders both scene textures, read from its public
        /// <c>supportsCameraOpaqueTexture</c> and <c>supportsCameraDepthTexture</c> switches, or null when
        /// it has no such switches (or there is no pipeline asset).
        /// </summary>
        public static bool? PipelineSupport(object pipeline)
        {
            if (pipeline == null)
            {
                return null;
            }

            bool? opaque = ReadSwitch(pipeline, OpaqueTextureProperty);
            bool? depth = ReadSwitch(pipeline, DepthTextureProperty);
            if (!opaque.HasValue || !depth.HasValue)
            {
                return null;
            }

            return opaque.Value && depth.Value;
        }

        /// <summary>Overrides the detected default and applies the keyword at once.</summary>
        public static void SetAvailable(bool available)
        {
            _explicitAvailability = available;
            Apply();
        }

        /// <summary>Drops the override, detects the default again and applies it.</summary>
        public static void ClearOverride()
        {
            _explicitAvailability = null;
            Apply();
        }

        /// <summary>Applies the keyword once; materials call this before their first draw.</summary>
        internal static void EnsureApplied()
        {
            if (!_applied)
            {
                Apply();
            }
        }

        private static bool? ReadSwitch(object pipeline, string propertyName)
        {
            try
            {
                PropertyInfo property = pipeline.GetType().GetProperty(propertyName,
                    BindingFlags.Instance | BindingFlags.Public);
                if (property == null || property.PropertyType != typeof(bool))
                {
                    return null;
                }

                return (bool)property.GetValue(pipeline);
            }
            catch (Exception)
            {
                // WHY: a stripped or changed pipeline type must not stop materials from loading; the
                // platform default applies instead.
                return null;
            }
        }

        private static void Apply()
        {
            _applied = true;
            if (Available)
            {
                Shader.EnableKeyword(Keyword);
            }
            else
            {
                Shader.DisableKeyword(Keyword);
            }
        }
    }
}
