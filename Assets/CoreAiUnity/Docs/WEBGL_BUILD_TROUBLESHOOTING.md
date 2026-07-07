# WebGL build troubleshooting (IL2CPP / LLVM / Editor)

Symptoms and mitigations when **Player → WebGL** fails in a large sample project (e.g. this repo with Roguelite example + CoreAI).

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
