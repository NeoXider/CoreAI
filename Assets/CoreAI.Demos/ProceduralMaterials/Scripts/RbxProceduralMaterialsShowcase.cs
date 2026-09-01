using System;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Rendering;
using CoreAI.Mods.Rbx.Spatial;
using UnityEngine;

namespace CoreAI.Demos.ProceduralMaterials
{
    /// <summary>Applies runtime materials and drives the labelled material-judging views.</summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class RbxProceduralMaterialsShowcase : MonoBehaviour
    {
        private static readonly RbxTextureMaterialProvider Provider =
            new RbxTextureMaterialProvider();
        private static readonly int BaseColorPropertyId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorPropertyId = Shader.PropertyToID("_Color");
        private static readonly int TextureScalePropertyId = Shader.PropertyToID("_TextureScale");

        [SerializeField] private Entry[] entries = Array.Empty<Entry>();
        [SerializeField] private Renderer[] diagnosticRenderers = Array.Empty<Renderer>();
        [SerializeField] private MaterialSelection[] materialSelections =
            Array.Empty<MaterialSelection>();
        [SerializeField] private TextMesh materialSelectionLabel;
        [SerializeField] private CameraView[] cameraViews = Array.Empty<CameraView>();
        [SerializeField] private Transform sweepNearMarker;
        [SerializeField] private Transform sweepFarMarker;
        [SerializeField] private Transform sweepTarget;
        [SerializeField] private float sweepSeconds = 9f;
        [SerializeField] private bool sweepEnabled = true;
        [SerializeField] private int selectedMaterialIndex;
        [SerializeField] private int selectedViewIndex;

        private float _sweepClock;

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

        /// <summary>One material option that can be applied to every diagnostic sample.</summary>
        [Serializable]
        public struct MaterialSelection
        {
            [SerializeField] private string materialName;
            [SerializeField] private int materialValue;

            public MaterialSelection(string materialName, int materialValue)
            {
                this.materialName = materialName;
                this.materialValue = materialValue;
            }

            public string MaterialName => materialName;

            public int MaterialValue => materialValue;
        }

        /// <summary>One named camera and its non-IMGUI world-space status label.</summary>
        [Serializable]
        public struct CameraView
        {
            [SerializeField] private string label;
            [SerializeField] private Camera camera;
            [SerializeField] private TextMesh statusLabel;

            public CameraView(string label, Camera camera, TextMesh statusLabel)
            {
                this.label = label;
                this.camera = camera;
                this.statusLabel = statusLabel;
            }

            public string Label => label;

            public Camera Camera => camera;

            public TextMesh StatusLabel => statusLabel;
        }

        /// <summary>Index of the material currently applied to all diagnostic samples.</summary>
        public int SelectedMaterialIndex => selectedMaterialIndex;

        /// <summary>Index of the currently active labelled camera view.</summary>
        public int SelectedViewIndex => selectedViewIndex;

        /// <summary>Whether the mid/far camera is moving to reveal distance shimmer.</summary>
        public bool SweepEnabled => sweepEnabled;

        /// <summary>Configures every generated renderer, material choice, camera, and sweep marker.</summary>
        public void ConfigureRig(Entry[] configuredEntries,
            Renderer[] configuredDiagnosticRenderers,
            MaterialSelection[] configuredMaterialSelections,
            TextMesh configuredMaterialSelectionLabel,
            CameraView[] configuredCameraViews,
            Transform configuredSweepNearMarker,
            Transform configuredSweepFarMarker,
            Transform configuredSweepTarget,
            int initialMaterialIndex)
        {
            entries = configuredEntries ?? Array.Empty<Entry>();
            diagnosticRenderers = configuredDiagnosticRenderers ?? Array.Empty<Renderer>();
            materialSelections = configuredMaterialSelections ?? Array.Empty<MaterialSelection>();
            materialSelectionLabel = configuredMaterialSelectionLabel;
            cameraViews = configuredCameraViews ?? Array.Empty<CameraView>();
            sweepNearMarker = configuredSweepNearMarker;
            sweepFarMarker = configuredSweepFarMarker;
            sweepTarget = configuredSweepTarget;
            selectedMaterialIndex = NormalizeIndex(initialMaterialIndex, materialSelections.Length);
            selectedViewIndex = 0;
            ApplyMaterials();
            ApplySelectedMaterial();
            ApplyViewActivation();
            RefreshLabels();
        }

        /// <summary>Replaces the serialized fixed mappings and immediately refreshes them.</summary>
        public void Configure(Entry[] configuredEntries)
        {
            entries = configuredEntries ?? Array.Empty<Entry>();
            ApplyMaterials();
        }

        /// <summary>Reapplies the catalog handles and neutral Part.Color to fixed samples.</summary>
        public void ApplyMaterials()
        {
            if (entries == null || entries.Length == 0)
            {
                return;
            }

            MaterialPropertyBlock propertyBlock = new MaterialPropertyBlock();
            for (int index = 0; index < entries.Length; index++)
            {
                Entry entry = entries[index];
                ApplyMaterial(entry.TargetRenderer, entry.MaterialName, entry.MaterialValue,
                    propertyBlock);
            }
        }

        /// <summary>Selects one material for every shape, scale, and rotating diagnostic sample.</summary>
        public void SelectMaterial(int index)
        {
            if (materialSelections == null || materialSelections.Length == 0)
            {
                return;
            }

            selectedMaterialIndex = NormalizeIndex(index, materialSelections.Length);
            ApplySelectedMaterial();
            RefreshLabels();
        }

        /// <summary>Moves material selection by a signed offset and wraps around the catalog.</summary>
        public void AdvanceMaterial(int offset)
        {
            SelectMaterial(selectedMaterialIndex + offset);
        }

        /// <summary>Activates exactly one labelled judging camera.</summary>
        public void SelectView(int index)
        {
            if (cameraViews == null || cameraViews.Length == 0)
            {
                return;
            }

            selectedViewIndex = NormalizeIndex(index, cameraViews.Length);
            ApplyViewActivation();
            RefreshLabels();
        }

        /// <summary>Enables or pauses the deterministic mid/far distance sweep.</summary>
        public void SetSweepEnabled(bool enabled)
        {
            sweepEnabled = enabled;
            RefreshLabels();
        }

        private void OnEnable()
        {
            ApplyMaterials();
            ApplySelectedMaterial();
            ApplyViewActivation();
            RefreshLabels();
        }

        private void OnValidate()
        {
            ApplyMaterials();
            ApplySelectedMaterial();
            ApplyViewActivation();
            RefreshLabels();
        }

        private void Update()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            HandleInput();
            UpdateMidFarSweep(Time.unscaledDeltaTime);
        }

        private void HandleInput()
        {
            if (Input.GetKeyDown(KeyCode.Q) || Input.GetKeyDown(KeyCode.LeftArrow))
            {
                AdvanceMaterial(-1);
            }

            if (Input.GetKeyDown(KeyCode.E) || Input.GetKeyDown(KeyCode.RightArrow))
            {
                AdvanceMaterial(1);
            }

            if (Input.GetKeyDown(KeyCode.Space))
            {
                SetSweepEnabled(!sweepEnabled);
            }

            if (Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1))
            {
                SelectView(0);
            }

            if (Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2))
            {
                SelectView(1);
            }

            if (Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3))
            {
                SelectView(2);
            }

            if (Input.GetKeyDown(KeyCode.Alpha4) || Input.GetKeyDown(KeyCode.Keypad4))
            {
                SelectView(3);
            }

            if (Input.GetKeyDown(KeyCode.Alpha5) || Input.GetKeyDown(KeyCode.Keypad5))
            {
                SelectView(4);
            }
        }

        private void ApplySelectedMaterial()
        {
            if (materialSelections == null || materialSelections.Length == 0 ||
                diagnosticRenderers == null || diagnosticRenderers.Length == 0)
            {
                return;
            }

            selectedMaterialIndex = NormalizeIndex(selectedMaterialIndex, materialSelections.Length);
            MaterialSelection selection = materialSelections[selectedMaterialIndex];
            MaterialPropertyBlock propertyBlock = new MaterialPropertyBlock();
            for (int index = 0; index < diagnosticRenderers.Length; index++)
            {
                ApplyMaterial(diagnosticRenderers[index], selection.MaterialName,
                    selection.MaterialValue, propertyBlock);
            }
        }

        private static void ApplyMaterial(Renderer targetRenderer, string materialName,
            int materialValue, MaterialPropertyBlock propertyBlock)
        {
            if (targetRenderer == null)
            {
                return;
            }

            RbxMaterialId id = string.IsNullOrEmpty(materialName)
                ? default(RbxMaterialId)
                : new RbxMaterialId(materialName, materialValue);
            Provider.TryGetMaterial(in id, out Material sharedMaterial);
            targetRenderer.sharedMaterial = sharedMaterial;

            propertyBlock.Clear();
            propertyBlock.SetColor(BaseColorPropertyId, Color.white);
            propertyBlock.SetColor(ColorPropertyId, Color.white);
            if (sharedMaterial != null && sharedMaterial.HasProperty(TextureScalePropertyId))
            {
                propertyBlock.SetFloat(TextureScalePropertyId,
                    RbxSpace.LengthToUnity(sharedMaterial.GetFloat(TextureScalePropertyId)));
            }

            targetRenderer.SetPropertyBlock(propertyBlock);
        }

        private void ApplyViewActivation()
        {
            if (cameraViews == null || cameraViews.Length == 0)
            {
                return;
            }

            selectedViewIndex = NormalizeIndex(selectedViewIndex, cameraViews.Length);
            for (int index = 0; index < cameraViews.Length; index++)
            {
                CameraView view = cameraViews[index];
                bool active = index == selectedViewIndex;
                if (view.Camera != null)
                {
                    view.Camera.gameObject.SetActive(active);
                }

                if (view.StatusLabel != null)
                {
                    view.StatusLabel.gameObject.SetActive(active);
                }
            }
        }

        private void RefreshLabels()
        {
            string materialName = materialSelections == null || materialSelections.Length == 0
                ? "UNCONFIGURED"
                : materialSelections[NormalizeIndex(selectedMaterialIndex,
                    materialSelections.Length)].MaterialName;
            if (materialSelectionLabel != null)
            {
                materialSelectionLabel.text = "SELECTED MATERIAL: " + materialName +
                                              "\nPART.COLOR: NEUTRAL WHITE";
            }

            if (cameraViews == null)
            {
                return;
            }

            for (int index = 0; index < cameraViews.Length; index++)
            {
                CameraView view = cameraViews[index];
                if (view.StatusLabel == null)
                {
                    continue;
                }

                string sweepState = sweepEnabled ? "RUNNING" : "PAUSED";
                view.StatusLabel.text = "VIEW " + (index + 1) + "/" + cameraViews.Length +
                                        ": " + view.Label + "\nMATERIAL: " + materialName +
                                        " | Q/E SELECT | 1-5 VIEW | SPACE SWEEP " + sweepState;
            }
        }

        private void UpdateMidFarSweep(float deltaTime)
        {
            if (!sweepEnabled || selectedViewIndex != 0 || cameraViews == null ||
                cameraViews.Length == 0 ||
                cameraViews[0].Camera == null || sweepNearMarker == null ||
                sweepFarMarker == null || sweepTarget == null)
            {
                return;
            }

            float duration = Mathf.Max(1f, sweepSeconds);
            _sweepClock = Mathf.Repeat(_sweepClock + deltaTime / duration, 2f);
            float linearPhase = _sweepClock <= 1f ? _sweepClock : 2f - _sweepClock;
            float smoothPhase = linearPhase * linearPhase * (3f - 2f * linearPhase);
            Transform cameraTransform = cameraViews[0].Camera.transform;
            cameraTransform.position = Vector3.Lerp(sweepNearMarker.position,
                sweepFarMarker.position, smoothPhase);
            cameraTransform.LookAt(sweepTarget.position);
        }

        private static int NormalizeIndex(int index, int count)
        {
            if (count <= 0)
            {
                return 0;
            }

            int normalized = index % count;
            return normalized < 0 ? normalized + count : normalized;
        }
    }
}
