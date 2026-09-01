using System;
using System.Collections.Generic;
using System.IO;
using CoreAI.Demos.ProceduralMaterials;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Rendering;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.EditMode.RbxApi.Unity
{
    public sealed class RbxMaterialShowcaseRigEditModeTests
    {
        [Test]
        public void SelectView_ActivatesExactlyOneCameraAndWraps()
        {
            GameObject root = new GameObject("Showcase Test Root");
            try
            {
                RbxProceduralMaterialsShowcase showcase =
                    root.AddComponent<RbxProceduralMaterialsShowcase>();
                Camera firstCamera = CreateCamera(root.transform, "First Camera");
                Camera secondCamera = CreateCamera(root.transform, "Second Camera");
                TextMesh firstLabel = CreateLabel(firstCamera.transform, "First Label");
                TextMesh secondLabel = CreateLabel(secondCamera.transform, "Second Label");
                RbxProceduralMaterialsShowcase.CameraView[] views =
                {
                    new RbxProceduralMaterialsShowcase.CameraView(
                        "MID/FAR SHIMMER SWEEP", firstCamera, firstLabel),
                    new RbxProceduralMaterialsShowcase.CameraView(
                        "FACE-ON CLOSE-UP", secondCamera, secondLabel)
                };

                showcase.ConfigureRig(Array.Empty<RbxProceduralMaterialsShowcase.Entry>(),
                    Array.Empty<Renderer>(),
                    Array.Empty<RbxProceduralMaterialsShowcase.MaterialSelection>(), null,
                    views, null, null, null, 0);

                Assert.IsTrue(firstCamera.gameObject.activeSelf);
                Assert.IsFalse(secondCamera.gameObject.activeSelf);
                showcase.SelectView(-1);
                Assert.AreEqual(1, showcase.SelectedViewIndex);
                Assert.IsFalse(firstCamera.gameObject.activeSelf);
                Assert.IsTrue(secondCamera.gameObject.activeSelf);
                Assert.IsTrue(secondLabel.text.Contains("FACE-ON CLOSE-UP"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SelectMaterial_WrapsAndKeepsNeutralPartColorLabel()
        {
            GameObject root = new GameObject("Showcase Test Root");
            try
            {
                RbxProceduralMaterialsShowcase showcase =
                    root.AddComponent<RbxProceduralMaterialsShowcase>();
                TextMesh selectionLabel = CreateLabel(root.transform, "Selection Label");
                RbxProceduralMaterialsShowcase.MaterialSelection[] selections =
                {
                    new RbxProceduralMaterialsShowcase.MaterialSelection("Metal", 1088),
                    new RbxProceduralMaterialsShowcase.MaterialSelection("Brick", 848)
                };

                showcase.ConfigureRig(Array.Empty<RbxProceduralMaterialsShowcase.Entry>(),
                    Array.Empty<Renderer>(), selections, selectionLabel,
                    Array.Empty<RbxProceduralMaterialsShowcase.CameraView>(), null, null, null, 0);
                showcase.SelectMaterial(-1);

                Assert.AreEqual(1, showcase.SelectedMaterialIndex);
                Assert.IsTrue(selectionLabel.text.Contains("Brick"));
                Assert.IsTrue(selectionLabel.text.Contains("NEUTRAL WHITE"));
                showcase.AdvanceMaterial(1);
                Assert.AreEqual(0, showcase.SelectedMaterialIndex);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SetSweepEnabled_UpdatesPublicState()
        {
            GameObject root = new GameObject("Showcase Test Root");
            try
            {
                RbxProceduralMaterialsShowcase showcase =
                    root.AddComponent<RbxProceduralMaterialsShowcase>();
                showcase.SetSweepEnabled(false);
                Assert.IsFalse(showcase.SweepEnabled);
                showcase.SetSweepEnabled(true);
                Assert.IsTrue(showcase.SweepEnabled);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void DumpMaterialApiEvidence_ExhaustsSupportedCatalogPlusExplicitFallback()
        {
            RbxTextureMaterialProvider.ResetSharedCacheForTests();
            RbxProceduralMaterialProvider.ResetSharedCacheForTests();
            GameObject root = new GameObject("Showcase Test Root");
            try
            {
                RbxProceduralMaterialsShowcase showcase =
                    root.AddComponent<RbxProceduralMaterialsShowcase>();
                Renderer firstRenderer = CreateRenderer(root.transform, "First Sample");
                Renderer secondRenderer = CreateRenderer(root.transform, "Second Sample");
                IReadOnlyList<RbxMaterialId> supported =
                    RbxProceduralMaterialProvider.SupportedMaterials;
                RbxProceduralMaterialsShowcase.MaterialSelection[] selections =
                    new RbxProceduralMaterialsShowcase.MaterialSelection[supported.Count + 1];
                for (int index = 0; index < supported.Count; index++)
                {
                    RbxMaterialId id = supported[index];
                    selections[index] = new RbxProceduralMaterialsShowcase.MaterialSelection(
                        id.Name,
                        id.Value);
                }

                selections[supported.Count] =
                    new RbxProceduralMaterialsShowcase.MaterialSelection("FALLBACK", -1);
                showcase.ConfigureRig(
                    Array.Empty<RbxProceduralMaterialsShowcase.Entry>(),
                    new[] { firstRenderer, secondRenderer },
                    selections,
                    null,
                    Array.Empty<RbxProceduralMaterialsShowcase.CameraView>(),
                    null,
                    null,
                    null,
                    5);

                LogAssert.Expect(
                    LogType.Log,
                    "[CoreAI.MaterialQA] MATERIAL_CATALOG complete slots=46 mapped=45 " +
                    "fallback=1 failures=0 result=PASS");
                showcase.DumpMaterialApiEvidence();

                Assert.AreEqual(5, showcase.SelectedMaterialIndex);
                Assert.IsNotNull(firstRenderer.sharedMaterial);
                Assert.IsNotNull(secondRenderer.sharedMaterial);
                Assert.AreSame(firstRenderer.sharedMaterial, secondRenderer.sharedMaterial);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                RbxTextureMaterialProvider.ResetSharedCacheForTests();
                RbxProceduralMaterialProvider.ResetSharedCacheForTests();
            }
        }

        [Test]
        public void RealtimeProbe_CapturesGeneratedReflectionCards()
        {
            string builderPath = Path.Combine(Application.dataPath, "CoreAI.Demos",
                "ProceduralMaterials", "Editor", "RbxProceduralMaterialsShowcaseBuilder.cs");
            string scenePath = Path.Combine(Application.dataPath, "CoreAI.Demos",
                "ProceduralMaterials", "ProceduralMaterialsShowcase.unity");
            string builderSource = File.ReadAllText(builderPath);
            string sceneSource = File.ReadAllText(scenePath);

            StringAssert.Contains("probe.renderDynamicObjects = true;", builderSource);
            StringAssert.Contains("m_RenderDynamicObjects: 1", sceneSource);
        }

        private static Camera CreateCamera(Transform parent, string name)
        {
            GameObject gameObject = new GameObject(name);
            gameObject.transform.SetParent(parent, false);
            return gameObject.AddComponent<Camera>();
        }

        private static TextMesh CreateLabel(Transform parent, string name)
        {
            GameObject gameObject = new GameObject(name);
            gameObject.transform.SetParent(parent, false);
            return gameObject.AddComponent<TextMesh>();
        }

        private static Renderer CreateRenderer(Transform parent, string name)
        {
            GameObject gameObject = new GameObject(name);
            gameObject.transform.SetParent(parent, false);
            return gameObject.AddComponent<MeshRenderer>();
        }
    }
}
