using System.Collections;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Building;
using CoreAI.Mods.Rbx.Instances.Terrain;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode.RbxApi.Building
{
    /// <summary>
    /// The richer pick (<c>IClickPickSource.TryPickWorld</c>) of a real world host: a part is picked with its hit point
    /// and face normal, an instance the caller refuses is looked through, the reach cuts the pick, and the Terrain is
    /// picked with the solid cell behind the face and the empty cell in front of it.
    /// </summary>
    public sealed class TerrainPickPlayModeTests
    {
        private GameObject _hostObject;
        private GameObject _cameraObject;
        private RbxWorldHost _host;
        private Camera _camera;

        [SetUp]
        public void SetUp()
        {
            RbxSpace.Configure(RbxSpace.DefaultMetersPerStud);
            _cameraObject = new GameObject("PickCamera");
            _camera = _cameraObject.AddComponent<Camera>();
            _camera.nearClipPlane = 0.05f;
            _camera.farClipPlane = 500f;
            _hostObject = new GameObject("PickWorld");
            _hostObject.SetActive(false);
            _host = _hostObject.AddComponent<RbxWorldHost>();
            _host.SetCamera(_camera);
            _hostObject.SetActive(true);
        }

        [TearDown]
        public void TearDown()
        {
            Object.Destroy(_hostObject);
            Object.Destroy(_cameraObject);
        }

        [UnityTest]
        public IEnumerator APart_IsPickedWithItsSurface_AndARefusedOneIsLookedThrough()
        {
            RbxInstance front = Part("Front", new RbxVector3(0f, 2f, -10f), new RbxVector3(4f, 4f, 1f));
            RbxInstance back = Part("Back", new RbxVector3(0f, 2f, -20f), new RbxVector3(8f, 8f, 1f));
            Aim(new RbxVector3(0f, 2f, 0f), new RbxVector3(0f, 2f, -20f));
            yield return null;
            RbxVector2 centre = ScreenOf(new RbxVector3(0f, 2f, -10f));

            Assert.IsTrue(_host.PickSource.TryPickWorld(centre, null, 100d, out RbxWorldPick pick));
            Assert.AreEqual(front.Id, pick.Instance);
            Assert.IsTrue(pick.HasSurface);
            Assert.IsFalse(pick.IsTerrain);
            Assert.AreEqual(-9.5f, pick.Position.Z, 0.05f, "the hit lies on the front face");
            Assert.AreEqual(new RbxVector3(0f, 0f, 1f), pick.AxisNormal, "the face looks at the camera");
            Assert.AreEqual(9.5d, pick.DistanceStuds, 0.25d, "from the camera (the ray starts on its near plane)");

            Assert.IsTrue(_host.PickSource.TryPickWorld(centre, id => id.Value != front.Id.Value, 100d,
                out RbxWorldPick through));
            Assert.AreEqual(back.Id, through.Instance, "a refused instance is looked through");

            Assert.IsFalse(_host.PickSource.TryPickWorld(centre, null, 5d, out _), "twin: beyond the reach nothing");
        }

        [UnityTest]
        public IEnumerator TheTerrain_IsPickedWithTheCellBehindTheFaceAndTheCellInFront()
        {
            RbxTerrain terrain = RbxTerrainBootstrap.Find(_host.Game.FindFirstChildOfClass("Workspace"));
            Assert.IsNotNull(terrain);
            Assert.IsTrue(TerrainMaterials.TryGetSlot("Grass", out byte grass));
            terrain.FillBlock(RbxCFrame.FromPosition(0f, -2f, 0f), new RbxVector3(32f, 4f, 32f), grass);
            Aim(new RbxVector3(6f, 20f, 14f), new RbxVector3(6f, 0f, 2f));
            for (int frame = 0; frame < 3; frame++)
            {
                yield return null;
            }

            RbxVector2 point = ScreenOf(new RbxVector3(6f, 0f, 2f));
            Assert.IsTrue(_host.PickSource.TryPickWorld(point, null, 200d, out RbxWorldPick pick));
            Assert.IsTrue(pick.IsTerrain, "the chunk collider resolves to the Terrain");
            Assert.AreEqual(terrain.Id, pick.Instance);
            Assert.AreEqual(new RbxVector3(0f, 1f, 0f), pick.AxisNormal);
            Assert.AreEqual(new RbxVector3(1f, -1f, 0f), pick.Cell, "the grass cell under the point");
            Assert.AreEqual(new RbxVector3(1f, 0f, 0f), pick.PlaceCell, "the empty cell above it");

            Assert.IsFalse(_host.PickSource.TryPickWorld(point, id => id.Value != terrain.Id.Value, 200d, out _),
                "twin: with the Terrain refused nothing is under the point");
        }

        private RbxInstance Part(string name, RbxVector3 position, RbxVector3 size)
        {
            RbxInstance part = _host.Registry.Create("Part");
            part.Name = name;
            part.Parent = _host.Registry.WorldRoot;
            _host.Binder.SetAnchored(part.Id, true);
            _host.Binder.SetSize(part.Id, size);
            _host.Binder.SetPosition(part.Id, position);
            return part;
        }

        private void Aim(RbxVector3 from, RbxVector3 at)
        {
            Vector3 eye = RbxSpace.ToUnity(from);
            Vector3 target = RbxSpace.ToUnity(at);
            _cameraObject.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(target - eye, Vector3.up));
            Physics.SyncTransforms();
        }

        private RbxVector2 ScreenOf(RbxVector3 point)
        {
            Vector3 screen = _camera.WorldToScreenPoint(RbxSpace.ToUnity(point));
            return new RbxVector2(screen.x, Screen.height - screen.y);
        }
    }
}
