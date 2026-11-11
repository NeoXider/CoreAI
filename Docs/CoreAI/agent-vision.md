# Agent Vision (camera / screenshot) for CoreAI

> Any LLM agent can **see** the game: capture what a `Camera` renders as a downscaled image, and move,
> aim or orbit a camera that belongs to the agent — one a designer marked for it, or a free camera
> created for it on demand. The player's camera is **never** hijacked.

This document records the design decisions and tradeoffs for the agent-vision feature. Implementation
lives in `Assets/CoreAiUnity/Runtime/Source/Features/Vision/`:

| File | Responsibility |
| --- | --- |
| `CoreAiAgentCamera.cs` | Opt-in marker `MonoBehaviour` that makes a camera agent-controllable. |
| `AgentCameraService.cs` | `IAgentCameraService`: ownership resolution, marker gating, the free agent camera, rate limiting, capture. |
| `AgentCameraOptions.cs` | The camera rules in one place: rate limit, free camera on/off and name, FOV, pitch and orbit-distance limits. |
| `AgentCameraRig.cs` | `IAgentCameraRig`: look (position, rotation, object or point target, FOV) and orbit (yaw, pitch, distance) on a camera that already passed the gate. |
| `CameraPoseMath.cs` | Engine-free aim and orbit math (`System.Numerics`), so the pose rules are deterministic and testable outside Unity. |
| `CameraTargetResolver.cs` | `ICameraTargetResolver`: object name to aim point (rendered-bounds centre); default `SceneCameraTargetResolver`. |
| `CameraFrameReadback.cs` | `ICameraFrameReadback`: render into a pooled target and read it back; `AsyncGpuCameraFrameReadback` (default) with the `SyncCameraFrameReadback` fallback. |
| `CameraLlmTool.cs` | One `ILlmTool` (`camera`) exposing `camera_capture` (alias `screenshot`) / `camera_look` / `camera_orbit` / `camera_list`. |
| `VisionSelfProbe.cs` | Sends the routed model a test image and checks the answer, so vision support is measured instead of guessed from the model name. |

---

## 1. Goals & non-goals

**Goals**
- Capture the frame rendered by the **main camera** by default, **without moving it** (read-only capture
  is always safe).
- Give every agent **its own** camera to look around with: a camera a designer marked for it, or a free
  camera created on demand the first time the agent moves "its" camera.
- **Never** commandeer the player's camera — e.g. a first-person player camera must not be moved. Only
  marked cameras and the agent's own free camera move.
- Capture without a frame hitch: read the frame back asynchronously where the platform allows it.
- Keep token cost low for vision models (downscale, default 512px long edge).

**Non-goals**
- Continuous video streaming / per-frame vision (single-shot captures only, rate limited).

---

## 2. Capture pipeline (decision)

`Camera.Render()` into a pooled offscreen `RenderTexture` → read back → encode (JPEG default, PNG optional) →
`byte[]` → returned as an `LlmToolImageResult` image (no base64 in the tool message; §5). Every camera tool
captures through one seam, `ICameraFrameReadback` (`camera_capture` through `AgentCameraService`, the
deprecated `capture_camera` directly), so there is one capture implementation to configure or replace.

Two readback strategies ship:

| Strategy | When | Main-thread cost |
| --- | --- | --- |
| `AsyncGpuCameraFrameReadback` (default, `CameraFrameReadback.CreateDefault()`) | `SystemInfo.supportsAsyncGPUReadback` is true (desktop, consoles, mobile, WebGPU) | Render submission + encode. The pixels arrive 1-3 frames later through `AsyncGPUReadback`, polled once per frame with `UniTask.Yield`: no stall, no thread, no blocking wait. |
| `SyncCameraFrameReadback` (fallback) | No async readback (WebGL 2), a failed request, a render-target format the encoder cannot read, an encoder error, or no answer within `MaxWaitFrames` (30) | Render + `ReadPixels` + encode in one step, so the GPU stall of the capture frame. The same pipeline as `Camera.CaptureAiAttachment`. |

The async path encodes straight from the readback's native buffer (`ImageConversion.EncodeNativeArrayToJPG`,
no `Texture2D`, no managed copy of the raw pixels). Both return a `CameraFrameReadbackResult` with the
bytes, the path taken (`Mode`), the frames waited and the main-thread milliseconds the capture blocked;
the frame-budget check reads that number (§9).

Decisions & tradeoffs:
- **Render to an offscreen RT, not `ScreenCapture`.** Rendering a specific `Camera` to our own RT lets us
  capture *any* camera at *any* size without touching the display, and never disturbs the player's view.
  We save/restore `Camera.targetTexture` and `RenderTexture.active` so the camera keeps rendering to the
  screen afterward. Targets come from `RenderTexture.GetTemporary` (Unity's pool) and go back in a `finally`.
- **End of frame + async-friendly.** The readback yields to `PlayerLoopTiming.LastPostLateUpdate`
  (`UniTask.Yield`) before the explicit `Render()` so a capture reflects a fully-drawn frame. The tool hops to
  the main thread (`UniTask.SwitchToMainThread`), captures, then returns to the thread pool (not on WebGL,
  which has no thread pool), matching the existing tools (`SceneLlmTool`, etc.).
- **Fallback per capture, not per session.** A device that rejects one request still answers: that capture
  takes the sync path and costs one slower frame.
- **Downscale by long edge.** Default `maxSize = 512` px on the longer edge, clamped to `[64, 1024]`.
  Aspect ratio is derived from `Camera.pixelWidth/pixelHeight` (fallback 16:9). 512px keeps a JPEG at
  roughly a few thousand vision tokens; 1024 is the hard cap so a model can request more detail without
  blowing the budget or memory.
- **JPEG by default.** Photographic 3D frames compress far better as JPEG (quality 75) than PNG. PNG is
  supported by the service API (`CaptureImageFormat.Png`, opaque on both paths) for crisp UI/pixel-art
  captures but is not exposed as an LLM parameter to keep the tool schema lean.
- **Main-camera default without moving it.** Capture resolves the agent's own camera if it has one, else
  `Camera.main`, and only *renders* it: position/rotation are untouched. Capture is therefore always safe on
  any camera, marked or not.

```csharp
// Replace the readback (a game's own capture, a test fake) or force the sync path:
IAgentCameraService service = new AgentCameraService(
    AgentCameraOptions.CreateDefault(),
    new SyncCameraFrameReadback(),   // or new AsyncGpuCameraFrameReadback(fallback, isSupported, maxWaitFrames)
    null,                            // rig: the default AgentCameraRig over the same options
    null);                           // clock: monotonic Stopwatch seconds
```

---

## 3. Ownership model (decision)

An agent moves only **its own** camera:

1. a camera carrying a `CoreAiAgentCamera` marker with `allowMove = true` whose `agentRoleId` is empty (any
   agent) or equals the calling agent's role; or
2. its **free camera**: a camera `AgentCameraService` creates on demand the first time the agent asks to move
   its own camera and has no marked one (or names `agent`/`free`).

Everything else, including `Camera.main` with no marker, is **capture-only**.

`CoreAiAgentCamera : MonoBehaviour` (opt-in marker):

| Field | Meaning |
| --- | --- |
| `agentRoleId` (string, optional) | Restrict control to one agent role (e.g. `Programmer`). Empty = any agent. |
| `allowMove` (bool, default `true`) | Whether agents may move/rotate this camera. `false` = capture-only but still "assigned" for capture defaulting. |

The free camera:
- is a new GameObject named `CoreAI Agent Camera (<role>)` (`AgentCameraOptions.FreeCameraName`), one per role;
- starts at the main camera's pose and settings (`Camera.CopyFrom`), so its first frame shows what the player sees;
- stays **disabled**: it never draws to the screen and never becomes `Camera.main`; captures call `Render()` on it;
- carries a role-scoped marker, so another role cannot move it (`WrongRole`);
- is `HideFlags.DontSave` in edit mode, so it never lands in a saved scene; `AgentCameraService.Dispose()`
  destroys every free camera (the container disposes the singleton with its scope);
- can be switched off: `AgentCameraOptions.AllowFreeCamera = false` restores the designer-marked-only rule.

Reserved camera names (case-insensitive): `main` = `Camera.main`; `agent` = the agent's own camera (a marked
one, else the free camera); `free` = always the free camera. An empty name means the own camera when one
exists, else the main camera for a capture and the free camera for a move.

Design rationale, **secure by default**:
- The player's camera has no marker and is never the free camera, so it is invisible to movement: an agent
  asking to move it gets a structured error that names the next step (leave `cameraName` empty or use
  `agent`). A hijack requires a deliberate designer action (adding the component).
- A designer who marks the agent's camera capture-only (`allowMove = false`) is respected: an empty name is
  refused with `MovementDisabled`, and the agent must ask for `free` explicitly to look around.
- Role scoping (`agentRoleId`) lets a game give each agent its own drone camera without one agent grabbing
  another's.
- The marker uses `[RequireComponent(typeof(Camera))]` and `[AddComponentMenu("CoreAI/Agent Camera")]` so it
  is discoverable in the inspector and can only be attached where a `Camera` exists (no separate Editor
  assembly needed, which keeps the runtime asmdef clean).

`AgentCameraService` centralizes the rules so the tool stays thin and the logic is unit-testable:
- `ListCameras(agentRoleId)`: every scene camera as an `AgentCameraInfo` (`Name`, `IsMain`, `IsMarked`,
  `Movable` for this agent, `Pose`, `IsFree`); `camera_list` reports them as `name`, `isMain`, `marked`,
  `movable`, `free`, `pose`.
- `ResolveCaptureCamera(cameraName, agentRoleId)`: read-only resolution, reserved name or explicit
  `cameraName` → the agent's own camera → `Camera.main` → first active camera.
- `TryResolveMovableCamera(cameraName, agentRoleId, out cam, out denial)`: the movement gate; creates the
  free camera on demand and returns a typed denial (reason + a message with the next step) when the camera is
  unmarked, movement-disabled, owned by another role, or the free camera is switched off.
- Look and orbit are not the service's job: they go through `IAgentCameraRig` (§4), which
  `AgentCameraService` also implements by delegating to its own `AgentCameraRig`, so the tool and the
  service share one set of `AgentCameraOptions`.

---

## 4. Tool surface (decision)

One `ILlmTool` named **`camera`** (`IAIFunctionsLlmTool`) expanding to five native MEAI functions (one of them an alias):

| Function | Purpose | Params |
| --- | --- | --- |
| `camera_capture` | Screenshot a camera (read-only). | `cameraName?` (empty = own camera, else main; `main`; `agent`; a name from `camera_list`), `maxSize?` (long edge px, 64..1024, default 512). |
| `screenshot` | Alias of `camera_capture`: the same behavior under the literal name models search for. | same as `camera_capture`. |
| `camera_look` | Move and aim the agent's **own** camera. | `cameraName?`, `posX/Y/Z?`, `rotX/Y/Z?` (euler), `lookAt?` (object name), `targetX/Y/Z?` (a point; all three), `fov?` (degrees). |
| `camera_orbit` | Circle the agent's **own** camera around an object or point, aimed at it. | `cameraName?`, `target?` (object name), `targetX/Y/Z?`, `yaw?`, `pitch?`, `distance?`, `fov?`. |
| `camera_list` | List cameras: name, isMain, marked, movable, free, pose. | none |

`camera_look` precedence: the position is applied first, then the aim; an object (`lookAt`) wins over a
point, and a point wins over `rotX/Y/Z`. `fov` is clamped to `AgentCameraOptions.MinFieldOfView..MaxFieldOfView`
(10..120 by default; it has no visible effect on an orthographic camera).

`camera_orbit` places the camera `distance` from the pivot and looks straight at it, with the rotation
`(pitch, yaw, 0)`: `yaw` is the heading (0 looks along +Z, 90 along +X), a positive `pitch` is above the
pivot looking down (clamped to ±89), `distance` is in world units (clamped to 0.5..1000). An object pivot is
the centre of the object's rendered bounds, children included, so a build made of many parts is circled as a
whole. Omitted values keep the current ones (measured from where the camera is), and an omitted target keeps
circling the last pivot of that camera (its last orbit target or look target), so
`camera_orbit(target: "Castle")` or `camera_look(lookAt: "Castle")` followed by `camera_orbit(yaw: 90)`
walks around the castle. The applied pose matches the request within 0.01.

Look and orbit move the camera only; the agent then calls `camera_capture` to see the result (the success
summary says so). They do not return images on purpose: the camera prompt and the replace-the-previous-frame
rule (§5) are keyed to the capture function names, and that wording is part of the benchmark contract.

Why separate functions instead of one function with an `action` discriminator: the codebase and the LLM
tool-calling path rely on **native** per-function JSON schemas built from `[Description]`-annotated
parameters (see [MEAI_TOOL_CALLING.md](../../Assets/CoreAI/Docs/MEAI_TOOL_CALLING.md)). Splitting the actions
gives each one a clean, unambiguous schema instead of a pile of conditionally-relevant optional params on a
single function, and keeps `camera_capture` under the name the image lift recognises. It is still "one tool"
in the sense of one registered `ILlmTool`, mirroring `SceneLlmTool` (one `scene_tool` →
`find_objects`/`get_hierarchy`/…). Every description fits the 500-character tool-contract limit (pinned by
`CameraToolV2EditModeTests`).

The model calls the **function** names (`camera_look`, not `camera`). The tool policy accepts them, runs
each call under the wrapper's `ToolTimeoutMsOverride` / `EndsTurn` / `IsMutating` / `AllowDuplicates`, and
lists the function names under "Available tools" in the prompt and in refusals.

Replaceable blocks behind the tool, each with a replace-with-fake test:

| Seam | Default | Replace to |
| --- | --- | --- |
| `IAgentCameraService` | `AgentCameraService` | change who may move which camera. |
| `ICameraFrameReadback` | `AsyncGpuCameraFrameReadback` → `SyncCameraFrameReadback` | capture differently (a custom pipeline, a fake). |
| `IAgentCameraRig` | `AgentCameraRig` (or the service itself) | change how look/orbit move a camera (smoothing, constraints). |
| `ICameraTargetResolver` | `SceneCameraTargetResolver` (active GameObject by name, exact then case-insensitive) | map a game's own names (an Rbx path, an entity id) to points. |
| `AgentCameraOptions` | `AgentCameraOptions.CreateDefault()` | configure the rules without replacing anything. |

**`camera_capture` result.** Because OpenAI tool-result messages cannot carry images (see §5), the function
returns an `LlmToolImageResult`: the tool message is this compact JSON summary, and the JPEG travels next to it as
an image:

```json
{
  "ok": true,
  "summary": "Captured 'DroneCam' (agent camera) at 512x288, pos (3, 5, -8), rot (15, 90, 0).",
  "camera": "DroneCam",
  "isMain": false,
  "width": 512,
  "height": 288,
  "format": "jpg",
  "sizeBytes": 21430,
  "pose": { "position": {"x":3,"y":5,"z":-8}, "rotation": {"x":15,"y":90,"z":0}, "fieldOfView": 60 },
  "imageAttached": true
}
```

- **`summary`** is always present so a **text-only** model still gets value (which camera, where it is,
  what it is looking at) even though it can't see the pixels.
- **`imageAttached`** tells the model the picture follows in the next user message (§5). Errors are plain JSON
  strings: `{ "ok": false, "error": "<code>", "message": "<human text>" }`.

**`camera_orbit` result.** `{ "ok": true, "summary": "Orbited '...' around 'Castle': yaw 45, pitch 30,
distance 20. Call camera_capture to see the view.", "camera", "target", "pivot": {x,y,z}, "yaw", "pitch",
"distance", "pose" }`; the orbit values are measured from the camera's final position. `camera_look`
returns `{ ok, summary, camera, pose }` as before; its summary now also names the FOV, the target and the next
step.

Failures are structured and name the next step: `no_camera`, `rate_limited` (`retryAfterMs`),
`scene_loading`, `capture_failed` (with the readback's reason), `camera_not_movable` (leave `cameraName` empty
or use `agent`; for designers, the marker instructions), `target_not_found`, `invalid_target` (a partial point
or a target at the camera position), `no_target` (orbit with no target yet), `no_change`, `look_failed`,
`orbit_failed`, and `exception` for an unexpected error. Cancellation is not a failure result: it propagates as
`OperationCanceledException`.

---

## 5. How the image gets back to the model

Why a tool result cannot carry the image itself:

- `MeaiOpenAiChatClient` serializes MEAI `DataContent` with an `image/*` media type to an OpenAI
  `image_url` content part **only for user/assistant messages**.
- Tool results flow back as `FunctionResultContent` whose payload is serialized to a **text** tool
  message. The OpenAI Chat Completions schema has **no image content part for `role: "tool"` messages** —
  so an image returned *as a tool result* is not delivered to the model as an image.

Consequence: the image is **lifted** into a follow-up **user** message so the next model call receives it as
an `image_url` part. This is automatic — `SmartToolCallingChatClient.LiftCameraImages` runs before every
provider request on both the streaming and non-streaming paths; the host wires nothing.

Current shape (lean path): `camera_capture` / `screenshot` return an `LlmToolImageResult` — the JSON summary
(`imageAttached: true`) is the tool message, the JPEG/PNG bytes travel out-of-band and are moved into the image
message as-is. No base64, no data URL, no JSON parse or decode per capture (the former base64-in-JSON path cost
about 7 MB of garbage for a 100 KB frame; the typed path about 12 KB). Any tool can return images this way —
see `TOOL_AUTHORING_GUIDE.md` → "Returning images from a tool". An older tool that still returns a base64
`dataUrl` inside its JSON is decoded once by `ToolExecutionPolicy` and delivered the same way (`"ok"` must be the
JSON boolean `true`; `"true"`, `1` or `"yes"` leave it ordinary text).

How the lifted message reads:

- **Camera tools** (`camera_capture`, `screenshot`, and the scene tool `capture_camera`): one user message with the
  fixed prompt *"Camera frame from your latest capture. Inspect the image and continue the requested scene work
  using what you see."* and the frame. A new frame **replaces** the previous camera message, so a long build
  session carries one frame, not all of them. This wording is part of the benchmark contract (G6) and does not
  change.
- **Any other tool** returning images: one user message per call, *"Image returned by tool 'render_after'.
  Inspect it and continue with the task."* (or *"2 images returned by tool '...'"*). Earlier ones are **kept**, so a
  model can compare a before and an after render; the newest 8 such messages stay per history. Both kinds leave
  together with their tool exchange when the tool history is trimmed.

**Hosts observing tool calls.** `CoreAi.OnToolExecuted` (and any `IToolExecutionNotifier`) receives the tool's own
return value as `result`. For the camera tools that is now the `LlmToolImageResult`, **not** the JSON string with a
`dataUrl` it used to be:

```csharp
CoreAi.OnToolExecuted += (role, tool, args, result) =>
{
    if (result is LlmToolImageResult shot && shot.Images.Count > 0)
        preview.texture = LoadPreview(shot.Images[0].Memory);   // the JPEG/PNG bytes, no base64
};
```

`result.ToString()` is only the summary text, and `LlmToolCallCompleted.ResultJson` is that summary too. The static
`CameraLlmTool.TryGetImageDataUrl` / `TryParseImageDataUrl` (and the scene tool's
`TryExtractImageContentFromResult`) only understand legacy JSON strings that still carry a data URL; they return
`false` for today's camera results. To ask a one-off question about a frame, pass the image itself:
`CoreAi.AskWithImageFollowUpAsync("Is the roof done?", shot.Images[0])`.

For a chat use case that does not need the model to *decide* to look, the simpler existing path
(`CoreAi.AskWithCameraAsync`) captures and sends the image in one user message directly — no tool round-trip.

---

## 6. Safety

- **Rate limit.** Minimum 1s between captures **per agent role** (`AgentCameraOptions.MinCaptureIntervalSeconds`).
  A too-soon capture returns `rate_limited` with `retryAfterMs`. The clock is injected (`Func<double>`,
  `Stopwatch`-based default) so the limit is deterministically unit-testable.
- **Size cap.** `maxSize` clamped to `[64, 1024]` px long edge. Render targets come from
  `RenderTexture.GetTemporary` and are released in a `finally`, also when a capture is cancelled mid-wait; the
  sync path reuses one cached readback `Texture2D` (resized when the capture size changes), so repeated
  captures don't leak GPU/native memory.
- **No frame hitch.** Where the platform has async readback, a capture never waits for the GPU on the main
  thread; a request that does not finish within 30 frames is captured through the sync path instead of
  waiting forever.
- **No capture during scene load.** Capture is refused (`scene_loading`) while
  `SceneManager.GetActiveScene().isLoaded` is false, avoiding a null/half-built camera.
- **No player-camera hijack.** Movement is impossible without the opt-in marker or the agent's own free camera
  (§3); capture never mutates camera transforms; the free camera never renders to the screen.
- **Main-thread correctness.** Render/readback/transform writes run on the Unity main thread; the tool
  switches to it and back. WebGL takes the sync path and never touches a thread pool.

---

## 7. Registration (decision)

This is a **core** Unity feature, so it is wired in the core installer, not in Mods. `WorldCommandsInstaller`
(`Assets/CoreAiUnity/Runtime/Source/Composition/`) registers the service as a singleton and, in a
`RegisterBuildCallback`, attaches the tool to the **Programmer** role via
`AgentMemoryPolicy.AddToolForRole` — the same pattern the Mods installer uses for the Lua tools. The
callback swallows `VContainerException` so minimal/headless containers (which omit the orchestration
services) are unaffected.

- **Programmer** gets the tool by default: autonomous world-manipulating agents benefit most from
  look/list/capture.
- **SmartChat is not registered by the installer.** The chat role already has a capture path
  (`CoreAi.AskWithCameraAsync` + on-demand `CoreAi.RegisterCameraVisionTool`). In a chat-panel host the
  panel attaches the tool itself: `CoreAiChatPanel` calls `CoreAiChatService.TryEnsureCameraToolForRole`
  for the active chat role on start and on every role switch, when **Enable Camera Tool** is on in its
  `CoreAiChatConfig` (default on); the call is idempotent. Other hosts call
  `policy.AddToolForRole(BuiltInAgentRoleIds.SmartChat, new CoreAI.Vision.CameraLlmTool(service, BuiltInAgentRoleIds.SmartChat))`.

The tool is constructed with the role id it is registered for, which is how it knows *whose* camera is
"the agent's own" and which key to rate-limit.

The shipped installer registers the blocks and builds the service from them, as below; a block (or the whole
`IAgentCameraService`) registered before `RegisterWorldCommands` is kept, so a game configures or replaces one
without touching the others:

```csharp
builder.RegisterInstance(AgentCameraOptions.CreateDefault());
builder.Register<ICameraFrameReadback>(_ => CameraFrameReadback.CreateDefault(), Lifetime.Singleton);
builder.Register(r => new AgentCameraService(r.Resolve<AgentCameraOptions>(), r.Resolve<ICameraFrameReadback>(),
        null, null), Lifetime.Singleton)
    .As<IAgentCameraService>();
```

---

## 8. Relationship to the existing `capture_camera` tool (deprecated alias)

`CoreAI.Infrastructure.World.CameraLlmTool` (`camera_tool` → `capture_camera`) predates this feature and is
**deprecated onto the `camera` tool**: it keeps working, unchanged, for one release (7.57.x) and is scheduled
for removal after it. It is capture-only, has no ownership model, and is registered on demand for SmartChat
(`CoreAi.RegisterCameraVisionTool`). It now captures through the same implementation, the shared
`ICameraFrameReadback`, so it gets the async readback too (`new CameraLlmTool(readback)` injects one; the
parameterless constructor uses `CameraFrameReadback.CreateDefault()`). Its name, its parameters
(`cameraName` default `main`, `width`/`height` each clamped 64..1024) and its summary (`success`, `resolution`,
`camera`, `format`, `sizeBytes`, `imageAttached`) are unchanged, and its frame reaches the model with the camera
prompt, as before. It used to put a base64 `dataUri` into the tool text, which was never lifted and was cut to
`MaxToolResultChars` characters of base64.

Name map for the alias period:

| Old name | Use instead | Status |
| --- | --- | --- |
| `camera_tool` → `capture_camera(cameraName, width, height)` | `camera` → `camera_capture(cameraName, maxSize)` | deprecated alias, one release |
| `screenshot` | `camera_capture` | permanent discoverability alias (same function) |
| `camera_look(cameraName, posX..rotZ, lookAt)` | the same function; new optional `targetX/Y/Z`, `fov` | unchanged, extended |

The two tools can coexist on one role (different tool and function names). Prefer the `camera` tool for
every new registration: it adds the agent's own camera, look, orbit and listing.

---

## 9. Testing

- **EditMode, engine-free** (`CameraPoseMathEditModeTests`): orbit position and yaw/pitch/distance round trip,
  aim direction, degenerate cases, yaw wrapping. It uses no `UnityEngine` type, so it also runs under
  `dotnet test`.
- **EditMode** (`AgentCameraServiceEditModeTests`): marker gating (unmarked/`allowMove=false`/role-mismatch
  denied, marked+allowed permitted), capture-camera resolution defaulting, rate limiting via injected
  clock, and `ListCameras` shape. No play mode required: GameObjects with `Camera` + `CoreAiAgentCamera`
  are created directly.
- **EditMode** (`AgentFreeCameraOwnershipEditModeTests`): the free camera is created on demand, disabled,
  untagged, role-scoped and at the main camera's pose; `main` is refused and never moves (negative twin); the
  free camera can be switched off; a marked camera wins; a capture-only marked camera is respected; capture
  defaulting before and after; listing; `Dispose`.
- **EditMode** (`AgentCameraRigEditModeTests`): orbit pose within 0.01 of the requested yaw/pitch/distance,
  object pivots at the rendered centre, omitted values, pivot memory, clamps, the FOV range, every refusal
  leaves the camera unchanged, injected target resolver.
- **EditMode** (`CameraToolV2EditModeTests`): the five function names, the schemas (old parameters kept), the
  500-character description limit for both camera tools, the unchanged `camera_capture` and `capture_camera`
  summaries, the alias forwarding to the shared readback, fake readback / fake rig replacement, the player
  camera untouched through the tool, and the async readback's fallback.
- **PlayMode FastNoLlm** (`AgentCameraCapturePlayModeTests`, `CameraLlmToolPlayModeTests`): the synchronous
  helpers render a real camera to non-empty JPEG/PNG bytes.
- **PlayMode FastNoLlm** (`CameraFrameReadbackPlayModeTests`): async and sync readback each produce a decodable
  frame of the requested size; both agree on orientation and colour (a top-blue/bottom-red frame); the tool
  orbits its free camera and captures it end to end.
- **PlayMode, explicit** (`CameraCaptureFrameBudgetPlayModeTests`): logs the main-thread cost and the longest
  frame of async vs sync captures and fails when an async capture blocks longer than
  `COREAI_CAMERA_CAPTURE_BUDGET_MS` (default 16.7). Run it by name on a GPU machine and once in a built player.
- **PlayMode LlmVerification, explicit** (`AgentVisionLiveRoundTripPlayModeTests`): a real vision model orbits
  its free camera around a two-block build with random colours, captures it and names both colours; frames are
  saved to `COREAI_VISUAL_CAPTURE_DIR` when set.
