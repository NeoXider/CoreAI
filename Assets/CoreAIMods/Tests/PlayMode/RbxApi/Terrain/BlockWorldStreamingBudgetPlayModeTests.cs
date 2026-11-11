using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Binding.Terrain;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CoreAI.Tests.PlayMode.RbxApi.Terrain
{
    /// <summary>
    /// Measures the Block World's streaming at a view radius of 4 and 8 columns: the time to the full radius,
    /// the frame time while streaming and while standing, the chunks meshed per frame and the time the Terrain
    /// pump takes, and what is drawn (chunks, the draw-call bound, triangles, and how much of it lies past the
    /// fog's end), with the memory in use. Numbers only: they go to <c>artifacts/streaming/numbers.txt</c>.
    /// </summary>
    [TestFixture]
    [Category("StreamingCapture")]
    public sealed class BlockWorldStreamingBudgetPlayModeTests
    {
        private const string ActorId = "streaming-player";

        private static readonly string[] Mods =
        {
            "blockworld_world", "blockworld_build", "blockworld_daynight", "blockworld_torches"
        };

        private static readonly StringBuilder Report = new();

        private readonly List<GameObject> _created = new();
        private RbxWorldHost _host;
        private LuaCsRbxApiBindings _roblox;
        private LuaCsModStack _stack;
        private RbxTerrainPresenter _presenter;
        private Camera _camera;
        private double _pumpMilliseconds;

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject created in _created)
            {
                if (created != null)
                {
                    Object.Destroy(created);
                }
            }

            _created.Clear();
            _roblox?.Dispose();
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
            string folder = Path.Combine(projectRoot, "artifacts", "streaming");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "numbers.txt"), Report.ToString());
        }

        [UnityTest]
        [Timeout(1500000)]
        public IEnumerator StreamTheBlockWorld_AtARadiusOf([Values(4, 8)] int radius)
        {
            yield return BuildWorld(radius);
            RbxTerrainRenderer renderer = (RbxTerrainRenderer)_presenter.Renderer;
            List<double> streaming = new();
            List<double> pump = new();
            long remeshStart = renderer.RemeshCount;
            Stopwatch total = Stopwatch.StartNew();
            Stopwatch frame = new();
            double lastColumns = -1d;
            int stable = 0;
            int frames = 0;
            int maxMeshedInAFrame = 0;
            for (; frames < 40000 && stable < 150; frames++)
            {
                long before = renderer.RemeshCount;
                frame.Restart();
                yield return Frames(1);
                frame.Stop();
                streaming.Add(frame.Elapsed.TotalMilliseconds);
                pump.Add(_pumpMilliseconds);
                maxMeshedInAFrame = Math.Max(maxMeshedInAFrame, (int)(renderer.RemeshCount - before));
                double columns = Convert.ToDouble(Workspace.GetAttribute("BlockWorldColumns") ?? 0d);
                stable = Math.Abs(columns - lastColumns) < 0.5d && renderer.PendingChunkCount == 0 ? stable + 1 : 0;
                lastColumns = columns;
            }

            total.Stop();
            double fullSeconds = total.Elapsed.TotalSeconds - stable * Median(streaming) / 1000d;
            long meshed = renderer.RemeshCount - remeshStart;
            List<double> standing = new();
            for (int index = 0; index < 300; index++)
            {
                frame.Restart();
                yield return Frames(1);
                frame.Stop();
                standing.Add(frame.Elapsed.TotalMilliseconds);
            }

            Draw(renderer, out int drawBound, out long triangles, out long vertices, out long meshBytes,
                out int beyondFog, out long trianglesBeyondFog, out float fogEnd);
            double renderMs = TimeCameraRender();
            Record("radius " + radius + ": columns " + lastColumns + ", full in " + F(fullSeconds) + " s / " + frames
                   + " frames (time scale: fixed step " + F(Time.fixedDeltaTime * 1000f) + " ms per frame)");
            Record("  streaming frame ms: median " + F(Median(streaming)) + ", p95 " + F(Percentile(streaming, 0.95))
                   + ", max " + F(Percentile(streaming, 1d)) + "; Terrain pump ms: median " + F(Median(pump)) + ", p95 "
                   + F(Percentile(pump, 0.95)) + ", max " + F(Percentile(pump, 1d)));
            Record("  chunks meshed " + meshed + " (" + F(meshed / (double)Math.Max(1, frames)) + " per frame, at most "
                   + maxMeshedInAFrame + " in one), chunk objects " + renderer.ChunkObjectCount);
            Record("  standing frame ms: median " + F(Median(standing)) + ", p95 " + F(Percentile(standing, 0.95))
                   + "; one camera render " + F(renderMs) + " ms (CPU side, 1280x720)");
            Record("  drawn: draw-call bound " + drawBound + ", triangles " + triangles + ", vertices " + vertices
                   + ", mesh memory " + F(meshBytes / 1048576d) + " MB; past the fog's end (" + F(fogEnd) + " m): "
                   + beyondFog + " chunk renderers, " + trianglesBeyondFog + " triangles");
            Record("  memory: total allocated " + F(Profiler.GetTotalAllocatedMemoryLong() / 1048576d) + " MB, mono heap "
                   + F(GC.GetTotalMemory(false) / 1048576d) + " MB, Lua problems " + _problems.Count);
            Assert.Greater(lastColumns, 10d, "the world streamed in");
        }

        // ---- the world ------------------------------------------------------------------------------------------

        private readonly List<string> _problems = new();

        private RbxInstance Workspace => _host.Game.FindFirstChildOfClass("Workspace");

        private IEnumerator BuildWorld(int radius)
        {
            ProblemLogger logger = new(_problems);
            GameObject cameraObject = new("Streaming camera");
            _created.Add(cameraObject);
            _camera = cameraObject.AddComponent<Camera>();
            GameObject hostObject = new("Streaming world host");
            _created.Add(hostObject);
            _host = hostObject.AddComponent<RbxWorldHost>();
            _host.Initialize();
            yield return null;

            _presenter = _host.TerrainPresenter;
            // WHY the pump is run here: the presenter's LateUpdate does exactly this; running it inside the frame
            // loop lets the test time it.
            _presenter.enabled = false;
            UnityCameraRig rig = new(cameraObject.transform, _host.Binder);
            _roblox = new LuaCsRbxApiBindings(_host.Registry, _host.Game, log: line => _problems.Add(line),
                partSink: _host.Binder, cameraRig: rig, inputSource: new InMemoryInputSource());
            _roblox.WorldPhysics.AttachPort(_host.PhysicsPort);
            InstanceGameObjectBinder binder = _host.Binder;
            _roblox.AttachCharacterMotorFactory(humanoid =>
            {
                RbxInstance root = humanoid.RootPart;
                return root != null && binder.TryGetBoundObject(root.Id, out GameObject body)
                       && body.TryGetComponent(out Rigidbody rigidbody)
                    ? new UnityRbxCharacterMotor(rigidbody)
                    : null;
            });
            _stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = logger,
                ModStore = new MemoryStore(),
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = _roblox
            });
            Workspace.SetAttribute("BlockWorldViewRadius", (double)radius);
            _roblox.LocalPlayerActorId = ActorId;
            _roblox.Players.EnsureActor(_roblox.Registry, ActorId);
            foreach (string mod in Mods)
            {
                ActorContext loader = new LocalActorIdentityProvider(ActorId).GetActorContext(null);
                TextAsset asset = Resources.Load<TextAsset>("CoreAIMods/BlockWorld/" + mod);
                Assert.IsNotNull(asset, mod + " ships with the mods package");
                _stack.Runtime.LoadMod(loader, mod, asset.text, LuaCapabilities.All, false);
            }
        }

        private IEnumerator Frames(int count)
        {
            for (int frame = 0; frame < count; frame++)
            {
                yield return new WaitForFixedUpdate();
                _roblox.WorldPhysics.BeginPhysicsStep();
                _roblox.StepCharacterMotors(Time.fixedDeltaTime);
                _host.PhysicsPort.ApplyGravity();
                _roblox.Scheduler.Advance(Time.fixedDeltaTime);
                Stopwatch pump = Stopwatch.StartNew();
                _presenter.Pump();
                _presenter.PumpPartExposure();
                pump.Stop();
                _pumpMilliseconds = pump.Elapsed.TotalMilliseconds;
            }
        }

        private void Draw(RbxTerrainRenderer renderer, out int drawBound, out long triangles, out long vertices,
            out long meshBytes, out int beyondFog, out long trianglesBeyondFog, out float fogEnd)
        {
            drawBound = 0;
            triangles = 0;
            vertices = 0;
            meshBytes = 0;
            beyondFog = 0;
            trianglesBeyondFog = 0;
            fogEnd = RenderSettings.fog ? RenderSettings.fogEndDistance : float.PositiveInfinity;
            Vector3 eye = _camera.transform.position;
            foreach (MeshRenderer chunk in renderer.Root.GetComponentsInChildren<MeshRenderer>())
            {
                if (!chunk.enabled || !chunk.TryGetComponent(out MeshFilter filter) || filter.sharedMesh == null)
                {
                    continue;
                }

                Mesh mesh = filter.sharedMesh;
                long chunkTriangles = 0;
                for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                {
                    chunkTriangles += (long)mesh.GetIndexCount(submesh) / 3;
                }

                drawBound += chunk.sharedMaterials.Length;
                triangles += chunkTriangles;
                vertices += mesh.vertexCount;
                meshBytes += Profiler.GetRuntimeMemorySizeLong(mesh);
                Vector3 nearest = chunk.bounds.ClosestPoint(eye);
                if (Vector3.Distance(nearest, eye) > fogEnd)
                {
                    beyondFog++;
                    trianglesBeyondFog += chunkTriangles;
                }
            }
        }

        private double TimeCameraRender()
        {
            RenderTexture target = new(1280, 720, 24);
            RenderTexture saved = _camera.targetTexture;
            _camera.targetTexture = target;
            _camera.Render();
            Stopwatch clock = Stopwatch.StartNew();
            for (int index = 0; index < 10; index++)
            {
                _camera.Render();
            }

            clock.Stop();
            _camera.targetTexture = saved;
            target.Release();
            Object.Destroy(target);
            return clock.Elapsed.TotalMilliseconds / 10d;
        }

        private static double Median(List<double> values)
        {
            return Percentile(values, 0.5d);
        }

        private static double Percentile(List<double> values, double share)
        {
            if (values.Count == 0)
            {
                return 0d;
            }

            List<double> sorted = new(values);
            sorted.Sort();
            return sorted[Math.Min(sorted.Count - 1, (int)(share * sorted.Count))];
        }

        private static void Record(string line)
        {
            Report.AppendLine(line);
            TestContext.Out.WriteLine(line);
        }

        private static string F(double value)
        {
            return value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }

        private sealed class MemoryStore : ILuaModStore
        {
            private readonly Dictionary<string, string> _values = new();

            public string Get(string modId, string key)
            {
                return _values.TryGetValue(modId + "/" + key, out string value) ? value : "";
            }

            public void Set(string modId, string key, string value)
            {
                _values[modId + "/" + key] = value;
            }

            public void Clear(string modId)
            {
            }
        }

        private sealed class ProblemLogger : IGameLogger
        {
            private readonly List<string> _problems;

            public ProblemLogger(List<string> problems)
            {
                _problems = problems;
            }

            public void LogDebug(GameLogFeature feature, string message, Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message, Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message, Object context = null)
            {
            }

            public void LogError(GameLogFeature feature, string message, Object context = null)
            {
                _problems.Add("error: " + message);
            }
        }
    }
}
