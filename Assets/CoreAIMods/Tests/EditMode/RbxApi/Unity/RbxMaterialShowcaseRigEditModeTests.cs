using System;
using System.IO;
using CoreAI.Demos.ProceduralMaterials;
using NUnit.Framework;
using UnityEngine;

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
    }
}
