using System.Collections;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Adornments;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode.RbxApi.Building
{
    /// <summary>
    /// The adornment backends build their edge mesh from a primitive cube they destroy at once. In Play Mode that destroy
    /// is deferred to the end of the frame, so a probe left active drew a grey cube at the world origin for the first
    /// frame a selection box or a wireframe highlight showed (the Block World's cell outline did, under the crosshair).
    /// The binder's primitive template cache (the first part of a session) did the same. No active, rendered primitive
    /// may exist in the frame the first box or part is drawn.
    /// </summary>
    public sealed class AdornmentProbePlayModeTests
    {
        [UnityTest]
        public IEnumerator TheFirstSelectionBox_LeavesNoVisibleProbeCube()
        {
            using UnityRbxSelectionBoxBackend backend = new();
            backend.Apply(new InstanceId(11), new RbxSelectionBoxRenderState(RbxCFrame.FromPosition(0f, 2f, 0f),
                new RbxVector3(4f, 4f, 4f), new RbxColor3(1f, 1f, 0f), 0f, 0.15f, RbxGuiBase3d.DefaultColor, 1f));

            Assert.AreEqual(0, CountVisiblePrimitiveCubes(), "the probe is hidden in the frame it was made");
            yield return null;
            Assert.AreEqual(0, CountVisiblePrimitiveCubes());
        }

        [UnityTest]
        public IEnumerator TheFirstWireframeHighlight_LeavesNoVisibleProbeCube()
        {
            GameObject root = new("ProbeWorld");
            InstanceGameObjectBinder binder = new(root.transform);
            InstanceRegistry registry = new(null, binder, worldInstanceAdapter: new WorldInstanceAdapter(binder));
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            RbxInstance part = registry.Create("Part");
            part.Parent = registry.WorldRoot;
            using (UnityRbxHighlightBackend backend = new(binder, forceWireframe: true))
            {
                backend.Apply(new InstanceId(12), new RbxHighlightRenderState(new[] { part.Id },
                    RbxHighlightDepthMode.AlwaysOnTop, new RbxColor3(1f, 0f, 0f), 0.5f, new RbxColor3(1f, 1f, 1f), 0f));
                backend.Draw();

                Assert.AreEqual(0, CountVisiblePrimitiveCubes(), "the probe is hidden in the frame it was made");
                yield return null;
            }

            game.Destroy();
            Object.Destroy(root);
        }

        private static int CountVisiblePrimitiveCubes()
        {
            int count = 0;
            foreach (MeshRenderer renderer in Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude,
                         FindObjectsSortMode.None))
            {
                // WHY by name: CreatePrimitive names its objects "Cube" and "Sphere"; a part's own object carries the
                // part's name.
                string name = renderer.gameObject.name;
                if ((name == "Cube" || name == "Sphere") && renderer.enabled && renderer.gameObject.activeInHierarchy)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
