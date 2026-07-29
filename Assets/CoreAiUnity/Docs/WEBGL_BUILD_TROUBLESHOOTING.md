# WebGL build troubleshooting (IL2CPP / LLVM / Editor)

Symptoms and mitigations when **Player → WebGL** fails in a large sample project (e.g. this repo with Roguelite example + CoreAI).

## `CS0103: The name 'WebGLInput' does not exist in the current context`

**Switch the active build target to WebGL *first*, then build.** This is the single most common way
to lose an hour here.

`UnityEngine.WebGLInput` lives in `UnityEngine.WebGLModule.dll`, and Unity adds that assembly to the
compilation reference set **only when the editor's active build target is already WebGL**. A
`#if UNITY_WEBGL` guard does not save you: the define is on while building for WebGL, so the code is
compiled, but the reference is absent when the build was started from a different active target
(Android, Standalone…). The result is `CS0103` on a symbol that plainly exists.

**Fix:** switch the platform to WebGL (also make sure the selected build target *group* is WebGL),
let the domain reload and recompilation finish, and only then start the build. Do **not** work around
it in source with reflection — that hides a real project-configuration error.

## `Error building Player because scripts are compiling`

Starting a build right after a platform switch races the recompilation that switch triggers. Wait
until both `EditorApplication.isCompiling` and `EditorApplication.isUpdating` are false, confirm the
console is error-free, then build. Automated build scripts must poll for this explicitly.

## Managed stripping and DI (what is actually verified)

CoreAI ships WebGL as **IL2CPP** with managed stripping level **Medium** and
`il2cppCodeGeneration = OptimizeSize`, compressed **Brotli** with the JS decompression fallback on —
hence the `*.unityweb` file names, which a plain static HTTP server can serve as-is.

At that stripping level the VContainer registrations, `RbxWorldHost` instance binding, bundled-mod
seeding, the Lua VM and mod-driven `Instance.new` part spawning are all **verified working in a real
WebGL player**. The protection comes from `Assets/CoreAiUnity/link.xml`, which preserves the Lua VM,
the CoreAI assemblies, the Rbx API assemblies and VContainer.

**If DI or reflection resolves types from your own assemblies, add them to your project's own
`link.xml`** — CoreAI's covers CoreAI's assemblies only. A container that builds in the editor but
fails to resolve only in the WebGL player is almost always this.

## `[CoreAI] Build aborted: … API key` (BuildFailedException)

CoreAI refuses to build a player that would ship a provider key. Two guards throw `BuildFailedException` before compilation starts:

- **`CoreAIResourcesApiKeyBuildGuard`** — **any platform**: a `CoreAISettings` asset under a `Resources/` folder (default `Assets/Resources/CoreAISettings.asset`) has a non-empty `apiKey` or `secondaryApiKey`. Even a local placeholder (`lm-studio`) fails. `Resources` assets are packed into the player and the string is recoverable.
- **`CoreAIProductionSettingsValidator`** — **WebGL only**: a non-empty `ApiKey` combined with `ClientOwnedApi`, `ClientLimited`, or `ServerManagedApi`. Every `CoreAISettings` asset in the project is checked, not just one.

**Fix:** clear the key field on the committed asset(s) and supply it at runtime — `CoreAiBackend.SetApiKey`, an environment variable, or secure storage — or move to `ServerManagedApi` with no client-side key. The message names the offending asset path. Run `CoreAI/Validate Production Settings` to see the same findings without starting a build.

## `LLVM ERROR: out of memory` (clang on `Il2CppGenericMethodPointerTable.c`)

The WebGL toolchain compiles huge generated C files with **clang**. **32-bit** `clang.exe` is limited to ~2–4 GB address space; **Il2CppGenericMethodPointerTable.c** is often very large, so the compiler can exhaust memory even if the machine has free RAM.

**Mitigations (try in order):**

1. **Close other heavy apps** (browsers, IDEs on the same drive) and retry; ensure **page file** is not disabled on Windows.
2. **Player Settings → Publishing Settings → Enable Exceptions** — use **Explicitly Thrown** or **None** instead of **Full** to reduce generated code size where acceptable.
3. **Managed stripping** — **High** (or at least Medium) in Player Settings to shrink IL2CPP output.
4. **Reduce managed code in the WebGL build** — disable optional **assemblies** / define constraints so test-only or Editor-only code is not referenced from player code.
5. **IL2CPP additional arguments** (Project Settings → Player → Other Settings) — e.g. limit parallel compile jobs to lower peak memory (Unity documents vary by version; search Unity manual for *IL2CPP additional arguments* and *max parallel jobs* for your editor).
6. **Build on a machine with more free RAM** or use a **CI** agent sized for WebGL IL2CPP.

This is **not** a CoreAI package defect; it is a toolchain / project-size constraint.

## `IOException: … com.unity.dedicated-server` (“insufficient system resources”)

Often appears alongside **FileSystemWatcher** stacks while Unity or the OS enumerates `ProjectSettings/Packages`. Typical causes:

- **Handle / thread exhaustion** during a long WebGL build (many file watchers).
- **Antivirus / sync** (OneDrive, Defender real-time scan) locking paths under `ProjectSettings`.

**Mitigations:** pause sync on the repo folder, add Unity + project path to Defender exclusions, reboot and rebuild with fewer parallel tools. Removing unused **Dedicated Server** / Multiplayer packages if you do not need them can reduce churn under `ProjectSettings/Packages`.

## `[CoreAI] WebGL build: temporarily excluded … StreamingAssets`

Comes from **`CoreAIWebGlStreamingAssetsGuard`** (Editor preprocess). It intentionally skips certain StreamingAssets folders for WebGL so the build stays valid. **Informational**, not an error.

## Invalid folder `.meta` GUID

Unity requires **`guid:`** to be **exactly 32 hexadecimal characters**. A typo (33rd character) makes Unity ignore the folder asset and log **“does not have a valid GUID”**. Fix the line or delete the `.meta` and let Unity regenerate it.
