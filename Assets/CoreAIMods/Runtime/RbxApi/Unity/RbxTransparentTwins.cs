using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace CoreAI.Mods.Rbx.Rendering
{
    /// <summary>
    /// Process-wide cache of alpha-blended twins of opaque Rbx materials. A twin is a copy of its source
    /// with blending on, depth writes off, the transparent queue and alpha read from the part colour
    /// (<c>_AlphaFromPartColor</c>), so the binder's per-renderer alpha (1 - Transparency) takes effect.
    /// </summary>
    internal static class RbxTransparentTwins
    {
        internal const string AlphaFromPartColorProperty = "_AlphaFromPartColor";

        private static readonly int AlphaFromPartColorId = Shader.PropertyToID(AlphaFromPartColorProperty);
        private static readonly int SrcBlendId = Shader.PropertyToID("_SrcBlend");
        private static readonly int DstBlendId = Shader.PropertyToID("_DstBlend");
        private static readonly int ZWriteId = Shader.PropertyToID("_ZWrite");
        private static readonly Dictionary<Material, Material> Twins = new();

        /// <summary>How many twins exist.</summary>
        internal static int Count => Twins.Count;

        /// <summary>Resolves the blended twin of <paramref name="source"/>; see the provider seam.</summary>
        internal static bool TryGet(Material source, out Material twin)
        {
            twin = null;
            if (source == null)
            {
                return false;
            }

            if (source.renderQueue > (int)RenderQueue.GeometryLast)
            {
                twin = source;
                return true;
            }

            if (!source.HasProperty(AlphaFromPartColorId) || !source.HasProperty(SrcBlendId) ||
                !source.HasProperty(DstBlendId) || !source.HasProperty(ZWriteId))
            {
                return false;
            }

            if (Twins.TryGetValue(source, out twin) && twin != null)
            {
                return true;
            }

            twin = new Material(source)
            {
                name = source.name + "_Transparent",
                hideFlags = source.hideFlags,
                enableInstancing = source.enableInstancing
            };
            twin.SetFloat(SrcBlendId, (float)BlendMode.SrcAlpha);
            twin.SetFloat(DstBlendId, (float)BlendMode.OneMinusSrcAlpha);
            twin.SetFloat(ZWriteId, 0f);
            twin.SetFloat(AlphaFromPartColorId, 1f);
            twin.SetOverrideTag("RenderType", "Transparent");
            twin.renderQueue = (int)RenderQueue.Transparent;
            Twins[source] = twin;
            return true;
        }

        /// <summary>Destroys the twin of a source that is about to be destroyed.</summary>
        internal static void Forget(Material source)
        {
            if (ReferenceEquals(source, null) || !Twins.TryGetValue(source, out Material twin))
            {
                return;
            }

            Twins.Remove(source);
            Destroy(twin);
        }

        /// <summary>Copies a changed source's properties to its twin, keeping the twin's blend state.</summary>
        internal static void Refresh(Material source)
        {
            if (ReferenceEquals(source, null) || !Twins.TryGetValue(source, out Material twin) || twin == null)
            {
                return;
            }

            twin.CopyPropertiesFromMaterial(source);
            twin.shaderKeywords = source.shaderKeywords;
            twin.SetFloat(SrcBlendId, (float)BlendMode.SrcAlpha);
            twin.SetFloat(DstBlendId, (float)BlendMode.OneMinusSrcAlpha);
            twin.SetFloat(ZWriteId, 0f);
            twin.SetFloat(AlphaFromPartColorId, 1f);
            twin.renderQueue = (int)RenderQueue.Transparent;
        }

        internal static void ResetForTests()
        {
            foreach (Material twin in Twins.Values)
            {
                Destroy(twin);
            }

            Twins.Clear();
        }

        private static void Destroy(Material material)
        {
            if (material == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Object.Destroy(material);
            }
            else
            {
                Object.DestroyImmediate(material);
            }
        }
    }
}
