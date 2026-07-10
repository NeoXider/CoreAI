# Agent Vision (camera / screenshot) for CoreAI

> Any LLM agent can **see** the game: capture what a `Camera` renders as a downscaled image, and —
> only when explicitly permitted — move a camera that belongs to the agent. The player's camera is
> **never** hijacked.

This document records the design decisions and tradeoffs for the agent-vision feature. Implementation
lives in `Assets/CoreAiUnity/Runtime/Source/Features/Vision/`:

| File | Responsibility |
| --- | --- |
| `CoreAiAgentCamera.cs` | Opt-in marker `MonoBehaviour` that makes a camera agent-controllable. |
| `AgentCameraService.cs` | Ownership resolution, marker gating, rate limiting, and the capture/render pipeline. |
| `CameraLlmTool.cs` | One `ILlmTool` (`camera`) exposing `camera_capture` / `camera_look` / `camera_list`. |

---

## 1. Goals & non-goals

**Goals**
- Capture the frame rendered by the **main camera** by default, **without moving it** (read-only capture
  is always safe).
- Optionally assign an agent **its own** camera which it may move/rotate to look around.
- **Never** commandeer the player's camera — e.g. a first-person player camera must not be moved. Only
  cameras explicitly marked agent-controllable may be moved.
- Keep token cost low for vision models (downscale, default 512px long edge).

**Non-goals**
- Continuous video streaming / per-frame vision (single-shot captures only, rate limited).
- Replacing the existing capture-only `CoreAI.Infrastructure.World.CameraLlmTool`
  (`capture_camera`) — see §8 on how they relate.

---

## 2. Capture pipeline (decision)

`Camera.Render()` into an offscreen `RenderTexture` → `Texture2D.ReadPixels` → encode (JPEG default,
PNG optional) → `byte[]` → base64 `data:` URL.

Decisions & tradeoffs:
- **Render to an offscreen RT, not `ScreenCapture`.** Rendering a specific `Camera` to our own RT lets us
  capture *any* camera at *any* size without touching the display, and never disturbs the player's view.
  We save/restore `Camera.targetTexture` and `RenderTexture.active` so the camera keeps rendering to the
  screen afterward.
- **End of frame + async-friendly.** The service awaits `UniTask.WaitForEndOfFrame()` before the explicit
  `Render()` so a capture reflects a fully-drawn frame and does not stall the LLM worker thread. The tool
  hops to the main thread (`UniTask.SwitchToMainThread`), captures, then returns to the thread pool —
  matching the existing tools (`SceneLlmTool`, etc.).
- **Downscale by long edge.** Default `maxSize = 512` px on the longer edge, clamped to `[64, 1024]`.
  Aspect ratio is derived from `Camera.pixelWidth/pixelHeight` (fallback 16:9). 512px keeps a JPEG at
  roughly a few thousand vision tokens; 1024 is the hard cap so a model can request more detail without
  blowing the budget or memory.
- **JPEG by default.** Photographic 3D frames compress far better as JPEG (quality 75) than PNG. PNG is
  supported by the service API (`CaptureImageFormat.Png`) for crisp UI/pixel-art captures but is not
  exposed as an LLM parameter to keep the tool schema lean.
- **Main-camera default without moving it.** Capture resolves `Camera.main` (or the agent's own camera if
  assigned) and only *renders* it — position/rotation are untouched. Capture is therefore always safe on
  any camera, marked or not.

---

## 3. Ownership model (decision)

A camera is **movable by an agent only if** it carries a `CoreAiAgentCamera` marker with `allowMove = true`
**and** the marker's `agentRoleId` is empty (any agent) or equals the calling agent's role. Everything
else — including `Camera.main` with no marker — is **capture-only**.

`CoreAiAgentCamera : MonoBehaviour` (opt-in marker):

| Field | Meaning |
| --- | --- |
| `agentRoleId` (string, optional) | Restrict control to one agent role (e.g. `Programmer`). Empty = any agent. |
| `allowMove` (bool, default `true`) | Whether agents may move/rotate this camera. `false` = capture-only but still "assigned" for capture defaulting. |

Design rationale — **secure by default**:
- The player's camera has no marker, so it is invisible to movement: an agent asking to move it gets a
  structured error explaining the marker requirement. There is no way to opt a camera *out*; there is only
  a way to opt *in*. A hijack requires a deliberate designer action (adding the component).
- Role scoping (`agentRoleId`) lets a game give each agent its own drone camera without one agent grabbing
  another's.
- The marker uses `[RequireComponent(typeof(Camera))]` and `[AddComponentMenu("CoreAI/Agent Camera")]` so it
  is discoverable in the inspector and can only be attached where a `Camera` exists (no separate Editor
  assembly needed — keeps the runtime asmdef clean).

`AgentCameraService` centralizes the rules so the tool stays thin and the logic is unit-testable:
- `ListCameras(agentRoleId)` — every scene camera with `name`, `isMain`, `isMarked`, `movable` (for this
  agent), and pose.
- `ResolveCaptureCamera(cameraName, agentRoleId)` — read-only resolution: explicit `cameraName` →
  the agent's own marked camera → `Camera.main` → first active camera.
- `TryResolveMovableCamera(cameraName, agentRoleId, out cam, out denial)` — the movement gate; returns a
  typed denial (reason + human message) when the camera is unmarked, movement-disabled, or owned by
  another role.

---

## 4. Tool surface (decision)

One `ILlmTool` named **`camera`** (`IAIFunctionsLlmTool`) expanding to three native MEAI functions:

| Function | Purpose | Params |
| --- | --- | --- |
| `camera_capture` | Screenshot a camera (read-only). | `cameraName?` (defaults to own/main), `maxSize?` (long edge px). |
| `camera_look` | Move/rotate the agent's **own** (marked) camera. | `cameraName?`, `posX/Y/Z?`, `rotX/Y/Z?` (euler), `lookAt?` (target GameObject name). |
| `camera_list` | List cameras: name, isMain, marked, movable, pose. | — |

Why three functions instead of one function with an `action` discriminator: the codebase and the LLM
tool-calling path rely on **native** per-function JSON schemas built from `[Description]`-annotated
parameters (see `Docs/tool-description-native-schema`). Splitting into three functions gives each action a
clean, unambiguous schema instead of a pile of conditionally-relevant optional params on a single
function. It is still "one tool" in the sense of one registered `ILlmTool`, mirroring `SceneLlmTool`
(one `scene_tool` → `find_objects`/`get_hierarchy`/…). The tradeoff (three schema entries instead of one)
is small because each schema is tiny.

**`camera_capture` result.** Because OpenAI tool-result messages cannot carry images (see §5), the function
returns a compact JSON **string**:

```json
{
  "ok": true,
  "summary": "Captured 'DroneCam' (agent camera) at 512x288, pos (3.0, 5.0, -8.0), looking (15, 90, 0).",
  "camera": "DroneCam",
  "isMain": false,
  "width": 512,
  "height": 288,
  "format": "jpg",
  "sizeBytes": 21430,
  "pose": { "position": {"x":3,"y":5,"z":-8}, "rotation": {"x":15,"y":90,"z":0}, "fieldOfView": 60 },
  "dataUrl": "data:image/jpeg;base64,/9j/4AAQ..."
}
```

- **`summary`** is always present so a **text-only** model still gets value (which camera, where it is,
  what it is looking at) even though it can't see the pixels.
- **`dataUrl`** is the image the host lifts into a vision message (§5). Errors return
  `{ "ok": false, "error": "<code>", "message": "<human text>" }`.

Failures are structured: `no_camera`, `rate_limited` (`retryAfterMs`), `scene_loading`,
`camera_not_movable` (with the marker instructions), `target_not_found`, `no_change`.

---

## 5. How the image gets back to the model (investigation)

Grepping `FunctionResultContent` usage (`ToolExecutionPolicy`, `MeaiOpenAiChatClient`, tests) confirms:

- `MeaiOpenAiChatClient` serializes MEAI `DataContent` with an `image/*` media type to an OpenAI
  `image_url` content part **only for user/assistant messages**.
- Tool results flow back as `FunctionResultContent` whose payload is serialized to a **text** tool
  message. The OpenAI Chat Completions schema has **no image content part for `role: "tool"` messages** —
  so an image returned *as a tool result* is not delivered to the model as an image.

Consequence (and it matches the existing capture-only tool): the tool returns the image as a `dataUrl`
inside its JSON result, and the **host lifts it** into a follow-up **user** message so the next model call
receives it as an `image_url` part. Wiring (documented, host-side, not owned by this tool):

1. Subscribe to the tool-call-completed event.
2. Match `ToolName == "camera_capture"`.
3. Parse the result's `dataUrl` (a `data:image/...;base64,` URI) into a MEAI `DataContent` — the tool
   exposes `CameraLlmTool.TryGetImageDataUrl(resultJson, out string dataUrl)` and
   `CameraLlmTool.TryParseImageDataUrl(dataUrl, out DataContent image)` for exactly this.
4. Send a follow-up user message `[text prompt, image]`; `MeaiOpenAiChatClient` turns the `DataContent`
   into `image_url`.

For a chat use case that does not need the model to *decide* to look, the simpler existing path
(`CoreAi.AskWithCameraAsync`) captures and sends the image in one user message directly — no tool round-trip.

---

## 6. Safety

- **Rate limit.** Minimum 1s between captures **per agent role** (configurable). A too-soon capture returns
  `rate_limited` with `retryAfterMs`. The clock is injected (`Func<double>`, `Stopwatch`-based default) so
  the limit is deterministically unit-testable.
- **Size cap.** `maxSize` clamped to `[64, 1024]` px long edge; the RT/`Texture2D` are always destroyed in a
  `finally` so repeated captures don't leak GPU/native memory.
- **No capture during scene load.** Capture is refused (`scene_loading`) while
  `SceneManager.GetActiveScene().isLoaded` is false, avoiding a null/half-built camera.
- **No player-camera hijack.** Movement is impossible without the opt-in marker (§3); capture never mutates
  camera transforms.
- **Main-thread correctness.** Render/`ReadPixels`/transform writes run on the Unity main thread; the tool
  switches to it and back.

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
- **SmartChat is not auto-registered.** The chat role already has a capture path
  (`CoreAi.AskWithCameraAsync` + on-demand `CoreAi.RegisterCameraVisionTool`), and adding a second vision
  tool by default would duplicate surface and grow the chat token budget. Hosts that want it call
  `policy.AddToolForRole(BuiltInAgentRoleIds.SmartChat, new CoreAI.Vision.CameraLlmTool(service, BuiltInAgentRoleIds.SmartChat))`.

The tool is constructed with the role id it is registered for, which is how it knows *whose* camera is
"the agent's own" and which key to rate-limit.

---

## 8. Relationship to the existing `capture_camera` tool

`CoreAI.Infrastructure.World.CameraLlmTool` (`camera_tool` → `capture_camera`) predates this feature. It is
capture-only, has no ownership model, and is registered on demand for SmartChat. The new
`CoreAI.Vision.CameraLlmTool` (`camera` → `camera_capture`/`camera_look`/`camera_list`) is a superset for
autonomous agents: it adds the ownership/marker model, camera movement, and listing. They can coexist
(different tool/function names, different default roles). The vision tool is self-contained and does not
depend on the old one. Long term, hosts can standardize on the new tool and retire the old one.

---

## 9. Testing

- **EditMode** (`AgentCameraServiceEditModeTests`): marker gating (unmarked/`allowMove=false`/role-mismatch
  denied, marked+allowed permitted), capture-camera resolution defaulting, rate limiting via injected
  clock, and `ListCameras` shape. No play mode required — GameObjects with `Camera` + `CoreAiAgentCamera`
  are created directly.
- **PlayMode FastNoLlm** (`AgentCameraCapturePlayModeTests`): render a real camera and assert non-empty
  JPEG bytes with a valid SOI marker — `Camera.Render` + `ReadPixels` need a live pipeline.
