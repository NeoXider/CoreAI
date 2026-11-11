#if COREAI_HAS_INPUT_SYSTEM
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.ChatOverlay;
using CoreAI.Composition;
using CoreAI.Look;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Spatial;
using CoreAI.Mods.WorldPackages;
using CoreAI.UiFocus;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using VContainer;

namespace CoreAI.Tests.PlayMode.RbxApi
{
    /// <summary>
    /// The one-click play world as the menu builds it (<see cref="CoreAiPlayWorldBootstrap"/> with the controls,
    /// look, default content and overlay chat slots filled), composed in Play Mode with virtual Input System
    /// devices, checked for what a player notices: one player with one character, standing on the baseplate,
    /// W walks, a double-tapped Space flies, the wheel zooms to first person, the overlay opens on T and typing
    /// moves nothing, <c>/fly</c> toggles flight and <c>/undo</c> on a fresh world answers why it cannot, and in
    /// first person the local character is hidden from every camera of this machine and comes back in third
    /// person. The first frame, the first- and third-person body crops and a day-cycle pair under the default look
    /// are written to <c>artifacts/playworld/</c>.
    /// </summary>
    /// <remarks>
    /// WHY one world per test and soft findings: composing the world is the expensive part, and a player's
    /// checks are independent, so every check runs and the test fails once, listing each finding. WHY a fixed
    /// frame time: the double tap is a 0.3 s window of game time, and a loaded batchmode Editor can take longer
    /// than that for the four frames between the taps. WHY input is pressed only after <c>yield return null</c>:
    /// in a batchmode Editor the input state of <see cref="InputTestFixture"/>'s devices is readable in the
    /// Update phase, while a FixedUpdate continuation sees the saved input manager's buffers.
    /// </remarks>
    [TestFixture]
    [Category("InputDevice")]
    public sealed class ReadyToPlayWorldPlayModeTests : InputTestFixture
    {
        private const int ShotWidth = 640;
        private const int ShotHeight = 360;

        // WHY ten minutes: the first world a fresh Editor composes compiles every shader variant it draws
        // (the Rbx surfaces, the sky, the look's post-processing); on a loaded batchmode machine that took
        // 178 s of the default 180 s budget once and went over it the next time.
        private const int ComposeTimeoutMilliseconds = 600000;

        private readonly List<GameObject> _created = new();
        private readonly List<string> _findings = new();
        private readonly List<string> _problems = new();
        private Keyboard _keyboard;
        private Mouse _mouse;
        private CoreAiPlayWorldBootstrap _bootstrap;
        private float _savedCaptureDeltaTime;
        private string _outputFolder;

        public override void Setup()
        {
            base.Setup();
            // WHY cleared here: NUnit reuses one fixture instance for every test in it.
            _findings.Clear();
            _problems.Clear();
            _savedCaptureDeltaTime = Time.captureDeltaTime;
            Time.captureDeltaTime = 1f / 60f;
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName
                                 ?? Directory.GetCurrentDirectory();
            _outputFolder = Path.Combine(projectRoot, "artifacts", "playworld");
            Directory.CreateDirectory(_outputFolder);
        }

        [UnityTearDown]
        public IEnumerator DestroyWorld()
        {
            foreach (GameObject created in _created)
            {
                if (created != null)
                {
                    Object.Destroy(created);
                }
            }

            _created.Clear();
            yield return null;
            Time.captureDeltaTime = _savedCaptureDeltaTime;
        }

        [UnityTest]
        [Timeout(ComposeTimeoutMilliseconds)]
        public IEnumerator OneClickWorld_IsReadyToPlay()
        {
            yield return ComposeWorld();
            LuaCsRbxApiBindings world = CurrentWorld();
            RbxHumanoid humanoid = CoreAiLocalPlayer.FindHumanoid(world);

            CheckOnePlayerWithOneCharacter(world);
            yield return WaitUntilStanding(humanoid, world);
            WriteFirstFrame();

            yield return CheckWalk(world, humanoid);
            yield return CheckTypingMovesNothing(world, humanoid);
            yield return CheckDoubleTapFlies(humanoid);
            yield return CheckChatCommands(humanoid);
            yield return CheckWheelZoom(world, humanoid);
            yield return CheckFirstPersonHidesTheBody(world, humanoid);

            Report();
        }

        [UnityTest]
        [Timeout(ComposeTimeoutMilliseconds)]
        public IEnumerator OneClickWorld_DayCycleAndTorch_UnderTheDefaultLook()
        {
            yield return ComposeWorld();
            LuaCsRbxApiBindings world = CurrentWorld();
            yield return WaitUntilStanding(CoreAiLocalPlayer.FindHumanoid(world), world);
            RunAsPlayer("gfx_scene", @"
                local wall = Instance.new('Part')
                wall.Name = 'TorchWall'
                wall.Anchored = true
                wall.Size = Vector3.new(24, 12, 1)
                wall.Position = Vector3.new(0, 6, -40)
                wall.Material = Enum.Material.Concrete
                wall.Color = Color3.new(0.8, 0.8, 0.8)
                wall.Parent = workspace
                local holder = Instance.new('Part')
                holder.Name = 'Torch'
                holder.Anchored = true
                holder.CanCollide = false
                holder.Transparency = 1
                holder.Size = Vector3.new(1, 1, 1)
                holder.Position = Vector3.new(0, 4, -37)
                holder.Parent = workspace
                local light = Instance.new('PointLight')
                light.Name = 'TorchLight'
                light.Range = 16
                light.Brightness = 4
                light.Color = Color3.new(1, 0.7, 0.4)
                light.Enabled = false
                light.Parent = holder
                local camera = workspace.CurrentCamera
                camera.CameraType = Enum.CameraType.Scriptable
                camera.CFrame = CFrame.lookAt(Vector3.new(0, 9, -6), Vector3.new(0, 3, -40))");
            RunAsPlayer("gfx_noon", "game:GetService('Lighting').ClockTime = 12");
            yield return Frames(10);
            Color[] noon = Shoot("daycycle_noon.png");
            RunAsPlayer("gfx_midnight", "game:GetService('Lighting').ClockTime = 0");
            yield return Frames(10);
            Color[] midnight = Shoot("daycycle_midnight.png");
            RunAsPlayer("gfx_torch_on", "workspace.Torch.TorchLight.Enabled = true");
            yield return Frames(10);
            Color[] torchOn = Shoot("daycycle_midnight_torch_on.png");

            IGraphicsOptionsService options = _bootstrap.Composition.CoreScope.Container
                .Resolve<IGraphicsOptionsService>();
            options.SetOptions(options.Options.WithFog(!options.Effective.Fog));
            yield return Frames(5);
            Color[] afterLookApply = Shoot("daycycle_midnight_torch_on_after_look_apply.png");
            options.SetOptions(options.Options.WithFog(null));
            RunAsPlayer("gfx_torch_off", "workspace.Torch.TorchLight.Enabled = false");
            yield return Frames(10);
            Color[] torchOff = Shoot("daycycle_midnight_torch_off.png");

            RectInt ground = ProjectedRect(new RbxVector3(-10f, 0f, -14f), new RbxVector3(10f, 0f, -24f));
            RectInt wallNearTorch = ProjectedRect(new RbxVector3(-4f, 1.5f, -39.4f), new RbxVector3(4f, 6.5f, -39.4f));
            float groundNoon = Luminance(noon, ground);
            float groundMidnight = Luminance(midnight, ground);
            float wallOn = Luminance(torchOn, wallNearTorch);
            float wallOff = Luminance(torchOff, wallNearTorch);
            float groundAfterLookApply = Luminance(afterLookApply, ground);
            float groundTorchOn = Luminance(torchOn, ground);
            Record("day cycle: ground crop " + ground + " noon " + groundNoon + ", midnight " + groundMidnight +
                   "; wall crop " + wallNearTorch + " torch on " + wallOn + ", off " + wallOff +
                   "; ground at midnight with the torch on " + groundTorchOn + ", after a look apply " +
                   groundAfterLookApply + "; ambient mode now " + RenderSettings.ambientMode + ", claimed " +
                   LookAmbientOwnership.Shared.IsClaimed);
            Expect(groundMidnight < groundNoon * 0.5f, "midnight darkens the ground crop to under half of noon");
            Expect(wallOn > wallOff + 0.02f, "the torch lights the wall crop at night, and removing it darkens it");
            Expect(Mathf.Abs(groundAfterLookApply - groundTorchOn) < 0.02f,
                "a look apply at midnight (a Graphics option change) leaves the night's ground as dark as it was");
            Report();
        }

        private IEnumerator ComposeWorld()
        {
            _keyboard = InputSystem.AddDevice<Keyboard>();
            _mouse = InputSystem.AddDevice<Mouse>();
            _keyboard.MakeCurrent();
            _mouse.MakeCurrent();
            GameObject root = new("CoreAI Play World");
            root.SetActive(false);
            _created.Add(root);
            _bootstrap = root.AddComponent<CoreAiPlayWorldBootstrap>();
            SetSlot("controlsModule", root.AddComponent<CoreAiPlayerControlsModule>());
            SetSlot("lookModule", root.AddComponent<CoreAiPlayWorldLookModule>());
            SetSlot("worldContentModule", root.AddComponent<CoreAiDefaultWorldContent>());
            SetSlot("overlayChatModule", root.AddComponent<CoreAiOverlayChatModule>());
            root.SetActive(true);
            Assert.IsTrue(_bootstrap.IsComposed, "the play world composes: " + _bootstrap.Failure);
            RbxHumanoid humanoid = null;
            for (int frame = 0; frame < 900 && humanoid == null; frame++)
            {
                yield return null;
                LuaCsRbxApiBindings world = CurrentWorld();
                humanoid = world != null ? CoreAiLocalPlayer.FindHumanoid(world) : null;
            }

            Assert.IsNotNull(humanoid, "the local character spawns within 900 frames");
        }

        private void SetSlot(string field, MonoBehaviour module)
        {
            FieldInfo slot = typeof(CoreAiPlayWorldBootstrap).GetField(field,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(slot, "the bootstrap lost its '" + field + "' slot");
            slot.SetValue(_bootstrap, module);
        }

        private RbxWorldRuntimeSessionController Session()
        {
            return _bootstrap.Composition.ModsScope.Container.Resolve<RbxWorldRuntimeSessionController>();
        }

        private LuaCsRbxApiBindings CurrentWorld()
        {
            IObjectResolver container = _bootstrap?.Composition?.ModsScope?.Container;
            return container == null ? null : container.Resolve<RbxWorldRuntimeSessionController>().CurrentRbxApi;
        }

        private ActorContext PlayerActor()
        {
            return _bootstrap.Composition.ModsScope.Container.Resolve<IActorIdentityProvider>()
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
        }

        private void RunAsPlayer(string id, string code)
        {
            Session().CurrentConcreteRuntime.LoadMod(PlayerActor(), id, code, LuaCapabilities.All, false);
        }

        private void CheckOnePlayerWithOneCharacter(LuaCsRbxApiBindings world)
        {
            StringBuilder players = new();
            int characters = 0;
            foreach (RbxPlayer player in world.Players.GetPlayers())
            {
                if (player.Character != null)
                {
                    characters++;
                }

                players.Append(player.Name).Append(" (actor ").Append(player.NetworkActorId).Append(", character ")
                    .Append(player.Character != null ? player.Character.Name : "none").Append(") ");
            }

            int humanoids = 0;
            foreach (RbxInstance descendant in world.Registry.WorldRoot.GetDescendants())
            {
                if (descendant is RbxHumanoid)
                {
                    humanoids++;
                }
            }

            StringBuilder mods = new();
            foreach (LuaModInfo mod in Session().CurrentConcreteRuntime.ListMods(PlayerActor()))
            {
                mods.Append(mod.Id).Append(" by ").Append(mod.OwnerActorId).Append("; ");
            }

            Record("players: " + players + "| humanoids in workspace: " + humanoids + " | mods: " + mods);
            Expect(world.Players.GetPlayers().Count == 1, "exactly one player sits at this screen");
            Expect(characters == 1 && humanoids == 1, "exactly one character stands in the world");
        }

        private IEnumerator WaitUntilStanding(RbxHumanoid humanoid, LuaCsRbxApiBindings world)
        {
            RbxVector3 previous = RootPosition(world, humanoid);
            int still = 0;
            for (int frame = 0; frame < 600 && still < 30; frame++)
            {
                yield return null;
                RbxVector3 now = RootPosition(world, humanoid);
                still = Mathf.Abs(now.Y - previous.Y) < 0.01f ? still + 1 : 0;
                previous = now;
            }

            RbxVector3 root = RootPosition(world, humanoid);
            Record("standing: root " + root + ", state " + humanoid.GetState() + ", still for " + still + " frames");
            Expect(still >= 30, "the character comes to rest");
            Expect(root.Y > 0.5f && root.Y < 6f, "the character stands on the baseplate (top at 0), not under it or above it");
        }

        private IEnumerator CheckWalk(LuaCsRbxApiBindings world, RbxHumanoid humanoid)
        {
            RbxCFrame camera = world.CameraRig.GetCFrame();
            RbxVector3 flat = new(camera.LookVector.X, 0f, camera.LookVector.Z);
            if (flat.Magnitude < 0.1f)
            {
                // WHY: a camera looking straight down walks along its screen-up, as Humanoid:Move resolves it.
                flat = new RbxVector3(-camera.UpVector.X, 0f, -camera.UpVector.Z);
            }

            RbxVector3 forward = flat.Unit;
            RbxVector3 start = RootPosition(world, humanoid);
            yield return Hold(_keyboard.wKey, 60);
            yield return Frames(10);
            RbxVector3 moved = RootPosition(world, humanoid) - start;
            float along = moved.X * forward.X + moved.Z * forward.Z;
            Record("W: moved " + moved + ", " + along + " studs along the camera's forward " + forward);
            Expect(along > 3f, "holding W for a second walks the character away from the camera");
        }

        private IEnumerator CheckTypingMovesNothing(LuaCsRbxApiBindings world, RbxHumanoid humanoid)
        {
            ChatOverlayController overlay = _bootstrap.Composition.CoreScope.Container
                .Resolve<ChatOverlayController>();
            ICoreAiUiFocus focus = _bootstrap.Composition.CoreScope.Container.Resolve<ICoreAiUiFocus>();
            yield return Hold(_keyboard.tKey, 2);
            yield return Frames(5);
            bool opened = overlay.IsInputOpen;
            bool typing = focus.HasTextFocus;
            RbxVector3 start = RootPosition(world, humanoid);
            yield return Hold(_keyboard.wKey, 40);
            yield return Frames(10);
            RbxVector3 moved = RootPosition(world, humanoid) - start;
            Record("overlay: open " + opened + ", text focus " + typing + ", W typed into it moved " + moved);
            Expect(opened, "T opens the chat overlay's input");
            Expect(typing, "the open input holds text focus (ICoreAiUiFocus)");
            Expect(new RbxVector3(moved.X, 0f, moved.Z).Magnitude < 0.25f, "typing W into the chat moves nothing");
            overlay.CloseInput();
            yield return Frames(10);
        }

        private IEnumerator CheckDoubleTapFlies(RbxHumanoid humanoid)
        {
            yield return Hold(_keyboard.spaceKey, 3);
            yield return Frames(3);
            yield return Hold(_keyboard.spaceKey, 3);
            yield return Frames(5);
            RbxHumanoidState state = humanoid.GetState();
            Record("double Space: state " + state);
            Expect(state == RbxHumanoidState.Flying, "a double-tapped Space enters flight");
            yield return Hold(_keyboard.spaceKey, 3);
            yield return Frames(3);
            yield return Hold(_keyboard.spaceKey, 3);
            yield return Frames(60);
            Record("double Space again: state " + humanoid.GetState());
        }

        private IEnumerator CheckChatCommands(RbxHumanoid humanoid)
        {
            ChatOverlayController overlay = _bootstrap.Composition.CoreScope.Container
                .Resolve<ChatOverlayController>();
            bool flyingBefore = humanoid.GetState() == RbxHumanoidState.Flying;
            overlay.Submit("/fly");
            yield return Frames(10);
            bool flyingAfter = humanoid.GetState() == RbxHumanoidState.Flying;
            overlay.Submit("/fly");
            yield return Frames(30);
            bool flyingAgain = humanoid.GetState() == RbxHumanoidState.Flying;
            int linesBefore = overlay.Feed.Lines.Count;
            overlay.Submit("/undo");
            yield return Frames(30);
            StringBuilder replies = new();
            for (int index = linesBefore; index < overlay.Feed.Lines.Count; index++)
            {
                ChatFeedLine line = overlay.Feed.Lines[index];
                replies.Append(line.Kind).Append(": ").Append(line.Text).Append(" | ");
            }

            Record("/fly: flying " + flyingBefore + " -> " + flyingAfter + " -> " + flyingAgain + "; /undo answered: " +
                   replies);
            Expect(flyingAfter != flyingBefore && flyingAgain == flyingBefore, "/fly toggles flight on and off");
            Expect(replies.Length > 0, "/undo on a fresh world answers with a reason");
        }

        private IEnumerator CheckWheelZoom(LuaCsRbxApiBindings world, RbxHumanoid humanoid)
        {
            for (int notch = 0; notch < 10; notch++)
            {
                Set(_mouse.scroll, new Vector2(0f, 120f));
                yield return Frames(2);
            }

            yield return Frames(5);
            RbxVector3 focus = RootPosition(world, humanoid) + LuaCsRbxApiBindings.RigCameraSubjectOffset;
            float distance = (focus - world.CameraRig.GetCFrame().Position).Magnitude;
            Record("wheel: camera " + distance + " studs from the head after 10 notches");
            Expect(distance < 1f, "the wheel zooms the default camera into first person");
        }

        /// <summary>
        /// Shoots the character from an observer camera beside it while the player's camera is in first person,
        /// then again after the wheel zoomed back out: LocalTransparencyModifier hides the parts on this machine,
        /// so the observer sees the body vanish and return.
        /// </summary>
        private IEnumerator CheckFirstPersonHidesTheBody(LuaCsRbxApiBindings world, RbxHumanoid humanoid)
        {
            RbxVector3 root = RootPosition(world, humanoid);
            GameObject observerObject = new("First-person observer");
            _created.Add(observerObject);
            Camera observer = observerObject.AddComponent<Camera>();
            observer.fieldOfView = 40f;
            observer.aspect = (float)ShotWidth / ShotHeight;
            observer.depth = -100f;
            observer.transform.position = RbxSpace.ToUnity(root + new RbxVector3(9f, 2f, 9f));
            observer.transform.LookAt(RbxSpace.ToUnity(root));
            RectInt body = ProjectedBox(observer, root + new RbxVector3(-1.5f, -3f, -1.5f),
                root + new RbxVector3(1.5f, 2.5f, 1.5f));
            yield return Frames(5);
            int hiddenInFirstPerson = CountHiddenCharacterParts(world, humanoid, out int characterParts);
            Color[] firstPerson = Shoot(observer, "body_first_person.png");
            for (int notch = 0; notch < 12; notch++)
            {
                Set(_mouse.scroll, new Vector2(0f, -120f));
                yield return Frames(2);
            }

            yield return Frames(10);
            RbxVector3 focus = RootPosition(world, humanoid) + LuaCsRbxApiBindings.RigCameraSubjectOffset;
            float distance = (focus - world.CameraRig.GetCFrame().Position).Magnitude;
            int hiddenInThirdPerson = CountHiddenCharacterParts(world, humanoid, out int _);
            Color[] thirdPerson = Shoot(observer, "body_third_person.png");
            float change = MeanAbsoluteDifference(firstPerson, thirdPerson, body);
            Record("first person: " + hiddenInFirstPerson + " of " + characterParts + " character parts hidden; after " +
                   "zooming out to " + distance + " studs, " + hiddenInThirdPerson + " hidden; observer body crop " + body +
                   " mean " + Mean(firstPerson, body) + " -> " + Mean(thirdPerson, body) + ", change " + change);
            Expect(characterParts > 0 && hiddenInFirstPerson == characterParts,
                "first person hides every part of the local character");
            Expect(distance > 4f && hiddenInThirdPerson == 0, "zooming back out shows the whole character again");
            Expect(change > 0.03f, "an observer sees the body vanish in first person and come back in third person");
        }

        private static int CountHiddenCharacterParts(LuaCsRbxApiBindings world, RbxHumanoid humanoid, out int parts)
        {
            parts = 0;
            int hidden = 0;
            RbxInstance character = humanoid.Parent;
            if (character == null)
            {
                return 0;
            }

            foreach (RbxInstance descendant in character.GetDescendants())
            {
                if (descendant is RbxBasePart part && world.PartSink.TryGetPartProperties(part.Id, out PartProperties drawn)
                    && drawn.Transparency < 1f)
                {
                    parts++;
                    if (part.LocalTransparencyModifier >= 0.99f)
                    {
                        hidden++;
                    }
                }
            }

            return hidden;
        }

        private IEnumerator Hold(ButtonControl button, int frames)
        {
            Press(button);
            yield return Frames(frames);
            Release(button);
        }

        private static IEnumerator Frames(int count)
        {
            for (int frame = 0; frame < count; frame++)
            {
                yield return null;
            }
        }

        private static RbxVector3 RootPosition(LuaCsRbxApiBindings world, RbxHumanoid humanoid)
        {
            return world.PartSink.GetLivePositionStuds(humanoid.RootPart.Id);
        }

        private void WriteFirstFrame()
        {
            Color[] frame = Shoot("first_frame.png");
            RectInt sky = new(0, ShotHeight - 30, ShotWidth, 30);
            RectInt bottom = new(0, 0, ShotWidth, 30);
            Color skyMean = Mean(frame, sky);
            Color bottomMean = Mean(frame, bottom);
            Record("first frame: top band " + skyMean + ", bottom band " + bottomMean);
            Expect(skyMean.b > skyMean.r && skyMean.maxColorComponent > 0.2f, "the first frame shows a blue sky on top");
            Expect(Mathf.Abs(bottomMean.b - skyMean.b) + Mathf.Abs(bottomMean.g - skyMean.g) > 0.05f,
                "the bottom of the first frame is the ground, not more sky");
        }

        /// <summary>Renders the composed camera into a half-float target and returns the frame as a display shows it.</summary>
        private Color[] Shoot(string fileName)
        {
            return Shoot(_bootstrap.Composition.Camera, fileName);
        }

        private Color[] Shoot(Camera camera, string fileName)
        {
            RenderTexture target = new(ShotWidth, ShotHeight, 24, RenderTextureFormat.DefaultHDR);
            Texture2D linear = new(ShotWidth, ShotHeight, TextureFormat.RGBAFloat, false, true);
            Texture2D png = new(ShotWidth, ShotHeight, TextureFormat.RGBA32, false);
            RenderTexture previousActive = RenderTexture.active;
            try
            {
                RenderPipeline.StandardRequest request = new() { destination = target };
                RenderPipeline.SubmitRenderRequest(camera, request);
                RenderTexture.active = target;
                linear.ReadPixels(new Rect(0, 0, ShotWidth, ShotHeight), 0, 0);
                linear.Apply();
                Color[] pixels = linear.GetPixels();
                for (int index = 0; index < pixels.Length; index++)
                {
                    Color pixel = pixels[index];
                    pixels[index] = new Color(Mathf.Clamp01(pixel.r), Mathf.Clamp01(pixel.g), Mathf.Clamp01(pixel.b),
                        1f).gamma;
                }

                png.SetPixels(pixels);
                png.Apply();
                File.WriteAllBytes(Path.Combine(_outputFolder, fileName), png.EncodeToPNG());
                return pixels;
            }
            finally
            {
                RenderTexture.active = previousActive;
                Object.Destroy(target);
                Object.Destroy(linear);
                Object.Destroy(png);
            }
        }

        /// <summary>The screen rectangle spanned by two opposite corners given in studs.</summary>
        private RectInt ProjectedRect(RbxVector3 firstStuds, RbxVector3 secondStuds)
        {
            Camera camera = _bootstrap.Composition.Camera;
            Vector3 first = camera.WorldToViewportPoint(RbxSpace.ToUnity(firstStuds));
            Vector3 second = camera.WorldToViewportPoint(RbxSpace.ToUnity(secondStuds));
            int xMin = Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(first.x, second.x) * ShotWidth), 0, ShotWidth - 1);
            int xMax = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(first.x, second.x) * ShotWidth), xMin + 1, ShotWidth);
            int yMin = Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(first.y, second.y) * ShotHeight), 0, ShotHeight - 1);
            int yMax = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(first.y, second.y) * ShotHeight), yMin + 1,
                ShotHeight);
            return new RectInt(xMin, yMin, xMax - xMin, yMax - yMin);
        }

        /// <summary>The screen rectangle around the eight corners of a box given by two opposite corners in studs.</summary>
        private static RectInt ProjectedBox(Camera camera, RbxVector3 minStuds, RbxVector3 maxStuds)
        {
            float xMin = 1f;
            float xMax = 0f;
            float yMin = 1f;
            float yMax = 0f;
            for (int corner = 0; corner < 8; corner++)
            {
                RbxVector3 point = new((corner & 1) == 0 ? minStuds.X : maxStuds.X,
                    (corner & 2) == 0 ? minStuds.Y : maxStuds.Y, (corner & 4) == 0 ? minStuds.Z : maxStuds.Z);
                Vector3 viewport = camera.WorldToViewportPoint(RbxSpace.ToUnity(point));
                xMin = Mathf.Min(xMin, viewport.x);
                xMax = Mathf.Max(xMax, viewport.x);
                yMin = Mathf.Min(yMin, viewport.y);
                yMax = Mathf.Max(yMax, viewport.y);
            }

            int left = Mathf.Clamp(Mathf.RoundToInt(xMin * ShotWidth), 0, ShotWidth - 1);
            int right = Mathf.Clamp(Mathf.RoundToInt(xMax * ShotWidth), left + 1, ShotWidth);
            int bottom = Mathf.Clamp(Mathf.RoundToInt(yMin * ShotHeight), 0, ShotHeight - 1);
            int top = Mathf.Clamp(Mathf.RoundToInt(yMax * ShotHeight), bottom + 1, ShotHeight);
            return new RectInt(left, bottom, right - left, top - bottom);
        }

        private static float MeanAbsoluteDifference(Color[] first, Color[] second, RectInt crop)
        {
            double sum = 0d;
            int count = 0;
            for (int y = crop.yMin; y < crop.yMax; y++)
            {
                for (int x = crop.xMin; x < crop.xMax; x++)
                {
                    int index = y * ShotWidth + x;
                    sum += Mathf.Abs(first[index].r - second[index].r) + Mathf.Abs(first[index].g - second[index].g) +
                           Mathf.Abs(first[index].b - second[index].b);
                    count++;
                }
            }

            return count == 0 ? 0f : (float)(sum / (3d * count));
        }

        private static Color Mean(Color[] shot, RectInt crop)
        {
            double r = 0d;
            double g = 0d;
            double b = 0d;
            int count = 0;
            for (int y = crop.yMin; y < crop.yMax; y++)
            {
                for (int x = crop.xMin; x < crop.xMax; x++)
                {
                    Color pixel = shot[y * ShotWidth + x];
                    r += pixel.r;
                    g += pixel.g;
                    b += pixel.b;
                    count++;
                }
            }

            return count == 0 ? Color.black : new Color((float)(r / count), (float)(g / count), (float)(b / count));
        }

        private static float Luminance(Color[] shot, RectInt crop)
        {
            Color mean = Mean(shot, crop);
            return 0.2126f * mean.r + 0.7152f * mean.g + 0.0722f * mean.b;
        }

        private void Record(string finding)
        {
            _findings.Add(finding);
            TestContext.WriteLine("[ReadyToPlay] " + finding);
        }

        private void Expect(bool holds, string what)
        {
            if (!holds)
            {
                _problems.Add(what);
            }
        }

        private void Report()
        {
            Assert.IsEmpty(_problems, "a player would notice: " + string.Join("; ", _problems) +
                                      "\nfindings: " + string.Join("\n", _findings));
        }
    }
}
#endif
