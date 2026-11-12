using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Adornments;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode.RbxApi.Building
{
    /// <summary>
    /// <c>Highlight</c> on screen, through a real world host and its camera: <c>Adornee = part</c> draws the outline in
    /// the next frame and <c>Adornee = nil</c> (the Highlight sitting in an empty folder) removes it; AlwaysOnTop shows the
    /// outline through a wall and Occluded hides it there; the wireframe fallback draws the part's box edges. Each case
    /// is judged on a crop around the part and written as a PNG under <c>artifacts/p5</c>.
    /// </summary>
    public sealed class HighlightCapturePlayModeTests
    {
        private const int ShotWidth = 640;
        private const int ShotHeight = 400;

        private GameObject _hostObject;
        private GameObject _cameraObject;
        private GameObject _sunObject;
        private RbxWorldHost _host;
        private Camera _camera;
        private string _outputFolder;

        [SetUp]
        public void SetUp()
        {
            RbxSpace.Configure(RbxSpace.DefaultMetersPerStud);
            _cameraObject = new GameObject("HighlightCaptureCamera");
            _camera = _cameraObject.AddComponent<Camera>();
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.08f, 0.09f, 0.12f);
            _camera.fieldOfView = 40f;
            _camera.nearClipPlane = 0.05f;
            _camera.farClipPlane = 500f;
            _camera.aspect = (float)ShotWidth / ShotHeight;
            _sunObject = new GameObject("HighlightCaptureSun");
            Light sun = _sunObject.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.1f;
            _sunObject.transform.rotation = Quaternion.Euler(50f, 210f, 0f);

            _hostObject = new GameObject("HighlightCaptureWorld");
            _hostObject.SetActive(false);
            _host = _hostObject.AddComponent<RbxWorldHost>();
            _host.SetCamera(_camera);
            _hostObject.SetActive(true);

            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
            _outputFolder = Path.Combine(projectRoot, "artifacts", "p5");
            Directory.CreateDirectory(_outputFolder);
        }

        [TearDown]
        public void TearDown()
        {
            Object.Destroy(_hostObject);
            Object.Destroy(_cameraObject);
            Object.Destroy(_sunObject);
        }

        [UnityTest]
        public IEnumerator Adornee_ShowsTheOutlineInTheNextFrame_AndNilRemovesIt()
        {
            RbxInstance part = Part("Target", new RbxVector3(0f, 3f, 0f), new RbxVector3(6f, 6f, 6f),
                new RbxColor3(0.55f, 0.55f, 0.58f));
            Aim(new RbxVector3(10f, 9f, 16f), new RbxVector3(0f, 3f, 0f));
            RbxInstance folder = _host.Registry.Create("Folder");
            folder.Name = "Unused";
            folder.Parent = _host.Registry.WorldRoot;
            RbxHighlight highlight = (RbxHighlight)_host.Registry.Create("Highlight");
            highlight.OutlineColor = new RbxColor3(1f, 0f, 1f);
            highlight.FillColor = new RbxColor3(0f, 1f, 0f);
            highlight.Parent = folder;
            StringBuilder report = new();
            yield return null;

            RectInt crop = CropAround(part);
            int before = CountMagenta(Shoot("highlight-0-none.png", crop, report));
            Assert.AreEqual(0, before, "no outline before the Adornee is set");

            highlight.Adornee = part;
            yield return null;
            Color32[] shown = Shoot("highlight-1-adornee-part.png", crop, report);
            int ring = CountMagenta(shown);
            report.AppendLine("outline pixels with Adornee = part: " + ring);
            Assert.Greater(ring, 200, "the outline is drawn in the next frame");

            highlight.Adornee = null;
            yield return null;
            int cleared = CountMagenta(Shoot("highlight-2-adornee-nil.png", crop, report));
            report.AppendLine("outline pixels with Adornee = nil (parent is an empty folder): " + cleared);
            Assert.AreEqual(0, cleared, "Adornee = nil removes it");
            File.WriteAllText(Path.Combine(_outputFolder, "highlight-report.txt"), report.ToString());
        }

        [UnityTest]
        public IEnumerator AlwaysOnTop_ShowsThroughAWall_AndOccludedDoesNot()
        {
            RbxInstance part = Part("Target", new RbxVector3(0f, 3f, 0f), new RbxVector3(6f, 6f, 6f),
                new RbxColor3(0.55f, 0.55f, 0.58f));
            Part("Wall", new RbxVector3(0f, 3f, 5f), new RbxVector3(4f, 12f, 1f), new RbxColor3(0.6f, 0.35f, 0.2f));
            Aim(new RbxVector3(0f, 4f, 18f), new RbxVector3(0f, 3f, 0f));
            RbxHighlight highlight = (RbxHighlight)_host.Registry.Create("Highlight");
            highlight.OutlineColor = new RbxColor3(1f, 0f, 1f);
            highlight.FillTransparency = 1f;
            highlight.Parent = part;
            StringBuilder report = new();
            yield return null;

            RectInt crop = CropAround(part);
            int onTop = CountMagentaInColumns(Shoot("highlight-3-on-top-behind-wall.png", crop, report), crop, 0.4f, 0.6f);
            highlight.DepthMode = RbxHighlightDepthMode.Occluded;
            yield return null;
            int occluded = CountMagentaInColumns(Shoot("highlight-4-occluded-behind-wall.png", crop, report), crop, 0.4f,
                0.6f);
            report.AppendLine("outline pixels over the wall: on top " + onTop + ", occluded " + occluded);
            File.WriteAllText(Path.Combine(_outputFolder, "highlight-depth-report.txt"), report.ToString());

            Assert.Greater(onTop, 20, "AlwaysOnTop draws the outline over the wall");
            Assert.AreEqual(0, occluded, "Occluded hides the outline where the wall stands");
        }

        [UnityTest]
        public IEnumerator WireframeFallback_DrawsThePartsBoxEdges()
        {
            RbxInstance part = Part("Target", new RbxVector3(0f, 3f, 0f), new RbxVector3(6f, 6f, 6f),
                new RbxColor3(0.55f, 0.55f, 0.58f));
            Aim(new RbxVector3(10f, 9f, 16f), new RbxVector3(0f, 3f, 0f));
            yield return null;
            RectInt crop = CropAround(part);
            using UnityRbxHighlightBackend wireframe = new(_host.Binder, forceWireframe: true);
            wireframe.Apply(new InstanceId(4242), new RbxHighlightRenderState(new[] { part.Id },
                RbxHighlightDepthMode.AlwaysOnTop, new RbxColor3(0f, 1f, 0f), 0.5f, new RbxColor3(1f, 0f, 1f), 0f));
            StringBuilder report = new();
            Color32[] pixels = Shoot("highlight-5-wireframe-fallback.png", crop, report, wireframe);
            int edges = CountMagenta(pixels);
            report.AppendLine("wireframe edge pixels: " + edges);
            File.WriteAllText(Path.Combine(_outputFolder, "highlight-wireframe-report.txt"), report.ToString());
            Assert.AreEqual(RbxHighlightDrawMode.Wireframe, wireframe.Mode);
            Assert.Greater(edges, 150, "the fallback draws the box edges");
        }

        private RbxInstance Part(string name, RbxVector3 position, RbxVector3 size, RbxColor3 color)
        {
            RbxInstance part = _host.Registry.Create("Part");
            part.Name = name;
            part.Parent = _host.Registry.WorldRoot;
            _host.Binder.SetAnchored(part.Id, true);
            _host.Binder.SetSize(part.Id, size);
            _host.Binder.SetPosition(part.Id, position);
            _host.Binder.SetColor(part.Id, color);
            return part;
        }

        private void Aim(RbxVector3 from, RbxVector3 at)
        {
            Vector3 eye = RbxSpace.ToUnityPose(RbxCFrame.FromPosition(from.X, from.Y, from.Z)).Item1;
            Vector3 target = RbxSpace.ToUnityPose(RbxCFrame.FromPosition(at.X, at.Y, at.Z)).Item1;
            _cameraObject.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(target - eye, Vector3.up));
        }

        private RectInt CropAround(RbxInstance part)
        {
            Assert.IsTrue(_host.Binder.TryGetPartRenderer(part.Id, out Renderer renderer));
            Bounds bounds = renderer.bounds;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 point = new(
                    (corner & 1) == 0 ? bounds.min.x : bounds.max.x,
                    (corner & 2) == 0 ? bounds.min.y : bounds.max.y,
                    (corner & 4) == 0 ? bounds.min.z : bounds.max.z);
                Vector3 screen = _camera.WorldToViewportPoint(point);
                minX = Mathf.Min(minX, screen.x * ShotWidth);
                maxX = Mathf.Max(maxX, screen.x * ShotWidth);
                minY = Mathf.Min(minY, screen.y * ShotHeight);
                maxY = Mathf.Max(maxY, screen.y * ShotHeight);
            }

            int x = Mathf.Clamp(Mathf.FloorToInt(minX) - 16, 0, ShotWidth - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt(minY) - 16, 0, ShotHeight - 1);
            int width = Mathf.Clamp(Mathf.CeilToInt(maxX) + 16, 1, ShotWidth) - x;
            int height = Mathf.Clamp(Mathf.CeilToInt(maxY) + 16, 1, ShotHeight) - y;
            return new RectInt(x, y, width, height);
        }

        private Color32[] Shoot(string fileName, RectInt crop, StringBuilder report,
            UnityRbxHighlightBackend extra = null)
        {
            RenderTexture target = new(ShotWidth, ShotHeight, 24, RenderTextureFormat.ARGB32);
            Texture2D image = new(crop.width, crop.height, TextureFormat.RGB24, false);
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture previousTarget = _camera.targetTexture;
            try
            {
                _camera.targetTexture = target;
                // WHY drawn here: RenderMesh draws for the frame it is called in, and the host drew in the last
                // LateUpdate, which an off-screen render request in the middle of a frame does not see.
                _host.HighlightBackend.Draw();
                extra?.Draw();
                RenderPipeline.StandardRequest request = new() { destination = target };
                if (RenderPipeline.SupportsRenderRequest(_camera, request))
                {
                    RenderPipeline.SubmitRenderRequest(_camera, request);
                }
                else
                {
                    _camera.Render();
                }

                RenderTexture.active = target;
                image.ReadPixels(new Rect(crop.x, crop.y, crop.width, crop.height), 0, 0);
                image.Apply();
                File.WriteAllBytes(Path.Combine(_outputFolder, fileName), image.EncodeToPNG());
                report.AppendLine("shot " + fileName + " crop " + crop.x.ToString(CultureInfo.InvariantCulture) + ","
                                  + crop.y.ToString(CultureInfo.InvariantCulture) + " " + crop.width + "x" + crop.height);
                return image.GetPixels32();
            }
            finally
            {
                _camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                Object.Destroy(image);
                target.Release();
                Object.Destroy(target);
            }
        }

        private static int CountMagenta(Color32[] pixels)
        {
            int count = 0;
            for (int index = 0; index < pixels.Length; index++)
            {
                if (IsMagenta(pixels[index]))
                {
                    count++;
                }
            }

            return count;
        }

        private static int CountMagentaInColumns(Color32[] pixels, RectInt crop, float from, float to)
        {
            int count = 0;
            int first = Mathf.FloorToInt(crop.width * from);
            int last = Mathf.CeilToInt(crop.width * to);
            for (int y = 0; y < crop.height; y++)
            {
                for (int x = first; x < last && x < crop.width; x++)
                {
                    if (IsMagenta(pixels[y * crop.width + x]))
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        private static bool IsMagenta(Color32 pixel)
        {
            return pixel.r > 170 && pixel.b > 170 && pixel.g < 90;
        }
    }
}
