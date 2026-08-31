using System;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Rendering;
using UnityEngine;

namespace CoreAI.Demos.ProceduralMaterials
{
    /// <summary>Applies the runtime catalog's shared, intrinsically colored handles to showcase tiles.</summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class RbxProceduralMaterialsShowcase : MonoBehaviour
    {
        private static readonly RbxProceduralMaterialProvider Provider =
            new RbxProceduralMaterialProvider();

        [SerializeField] private Entry[] entries = Array.Empty<Entry>();

        /// <summary>One serialized renderer-to-catalog mapping authored by the scene builder.</summary>
        [Serializable]
        public struct Entry
        {
            [SerializeField] private Renderer targetRenderer;
            [SerializeField] private string materialName;
            [SerializeField] private int materialValue;

            public Entry(Renderer targetRenderer, string materialName, int materialValue)
            {
                this.targetRenderer = targetRenderer;
                this.materialName = materialName;
                this.materialValue = materialValue;
            }

            public Renderer TargetRenderer => targetRenderer;

            public string MaterialName => materialName;

            public int MaterialValue => materialValue;

        }

        /// <summary>Replaces the serialized grid mappings and immediately refreshes their materials.</summary>
        public void Configure(Entry[] configuredEntries)
        {
            entries = configuredEntries ?? Array.Empty<Entry>();
            ApplyMaterials();
        }

        /// <summary>Reapplies catalog handles after an editor reload or runtime scene load.</summary>
        public void ApplyMaterials()
        {
            if (entries == null || entries.Length == 0)
            {
                return;
            }

            for (int index = 0; index < entries.Length; index++)
            {
                Entry entry = entries[index];
                Renderer targetRenderer = entry.TargetRenderer;
                if (targetRenderer == null)
                {
                    continue;
                }

                RbxMaterialId id = string.IsNullOrEmpty(entry.MaterialName)
                    ? default
                    : new RbxMaterialId(entry.MaterialName, entry.MaterialValue);
                Provider.TryGetMaterial(in id, out Material sharedMaterial);
                targetRenderer.sharedMaterial = sharedMaterial;
                targetRenderer.SetPropertyBlock(null);
            }
        }

        private void OnEnable()
        {
            ApplyMaterials();
        }

        private void OnValidate()
        {
            ApplyMaterials();
        }
    }
}
