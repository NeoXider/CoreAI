# Dependency versions check — 2026-11-11

Read-only survey: nothing in `Packages/`, `Assets/Packages`, `Assets/packages.config` or `Assets/CoreAIMods/Plugins`
was changed, and no git, Unity or restore command ran. Pins were read from `Packages/manifest.json`,
`Packages/packages-lock.json`, `Assets/packages.config`, `Assets/Mirror/version.txt` and the DLL version resources.
Upstream facts come from the GitHub REST API (releases, tags, compare, PRs), the NuGet v3 API and
`packages.unity.com`, all read on 2026-11-11. A changelog that could not be opened is marked **not checked**. The
previous survey, [dependency-update-2026-10-26/PLAN.md](dependency-update-2026-10-26/PLAN.md), is the baseline;
this note covers what changed since.

## Summary table

| Dependency | Pinned here | Latest stable (date) | Behind | Recommendation |
|---|---|---|---|---|
| Lua-CSharp (`Lua.dll`, NuGet `LuaCSharp`) | 0.5.6 = tag `v0.5.6` `d9fb336` (2026-07-29) | **0.5.8** (2026-10-25); `main` is 105 commits past it, unreleased | 2 releases | **Update now to 0.5.8**, behind the Lua gates listed below |
| Microsoft.Extensions.AI / .Abstractions | 9.10.2 (2025-11-02) | 11.0.0 (2026-11-10); 10.11.0 (2026-10-14); 9.10.2 is the last 9.x | 2 majors | **Stay**: 10.x/11.x need System.Text.Json 10/11, and Unity substitutes its own 8.0.0.0 |
| System.Text.Json | 8.0.6 | 11.0.0 (2026-11-10); 8.0.6 is the last 8.x | 3 majors | **Stay** (engine-bound) |
| System.Numerics.Tensors + Microsoft.Bcl.Numerics | 9.0.22 (2026-10-25) | 11.0.0; **9.0.23** (2026-11-10, the last .NET 9 servicing build) | 1 patch | **Next release**: move both to 9.0.23 as one pair |
| Microsoft.Bcl.AsyncInterfaces | 8.0.0 | 11.0.0; 8.0.0 is the last 8.x | majors only | **Stay** (engine assembly 8) |
| Microsoft.Extensions.Logging.Abstractions | 8.0.3 | 11.0.0; 8.0.3 is the last 8.x | majors only | **Stay** (MEAI 9.10.2 binds to 8.x) |
| System.Threading.Channels | 8.0.0 | 11.0.0; 8.0.0 is the last 8.x | majors only | **Stay** |
| Microsoft.Bcl.TimeProvider (needed by `Lua.dll`) | 8.0.1 | 11.0.0; 8.0.1 is the last 8.x | majors only | **Stay** |
| VContainer | 1.19.0 `5401e5a` | 1.19.0 (2026-07-01) | 0 (master +4 unreleased fixes) | **Stay** |
| MessagePipe (+ .VContainer) | 1.8.2 `58516c3` | 1.8.2 (2026-06-08) | 0 (CI-only commits since) | **Stay** |
| UniTask | 2.5.11 `2e993ff` | 2.5.11 (2026-05-19) | 0 (CI-only commits since) | **Stay** |
| R3 | 1.3.1 `f6eed2d` | 1.3.1 (2026-05-19) | 0 (CI/docs commits since) | **Stay** |
| LLMUnity | 3.0.3 `2c30b44` | 3.0.3 (2026-03-08) | 0 (pin is `main` HEAD) | **Stay** |
| MCP for Unity (CoplayDev/unity-mcp) | 10.3.0 `aa5fc63` | 10.3.0 (2026-10-04); `beta` carries 10.3.1-beta.11 | 0 stable | **Stay**; take 10.3.1 once it ships stable |
| NeoxiderTools | 10.19.0 `ca19855` (2026-10-23) | **12.2.0** (tag 2026-11-11, `#upm` branch); last GitHub release object is 12.1.0 (2026-11-06) | 2 majors | **Next release**; only the example game uses it |
| Mirror (per-host, git-ignored `Assets/Mirror`) | 96.0.1 (2025-02-27) | **96.11.4** (2026-11-07) | 67 releases | **Update now** on every host: 96.11.0 fixes two network-input security bugs |
| Input System | 1.20.1 | 1.20.1 (2026-10-05) | 0 | **Stay** |
| Netcode for GameObjects | 2.13.3 | **2.14.0** (registry 2026-11-10); 3.x needs Unity 6000.7 beta | 1 minor | **Next release** (fixes only; only the example game uses it) |
| Multiplayer Services | 2.4.1 | 2.4.1 (2026-10-16) | 0 | **Stay** |
| Multiplayer Tools | 2.2.12 | 2.2.12 (2026-09-16) | 0 | **Stay** |
| AI Navigation | 2.0.16 | 2.0.16 (2026-10-07) | 0 | **Stay** |
| Test Framework (+ Unity NUnit 2.1.0) | 1.8.0, built into the editor | engine-bound; the public registry's 1.4.6 is an older line | — | **Stay** |
| URP | 17.6.0, built into the editor | engine-bound | — | **Stay** |
| `com.neoxider.markdownrenderer` | not a CoreAI dependency | 1.2.1 (2026-10-28) | — | Nothing to do here |

Two inventory findings that are not version questions:

- `Assets/packages.config` lists `VContainer` 1.0.2 and `VContainerCore` 1.0.4. They are **unrelated NuGet
  packages** (author `nazgul`/`nazgul30`; `VContainerCore` depends on `GodotSharp` 4.3.0 and `PdEventBus`), and their
  folders hold only nuspec, readme, licence and icon, no DLL. The real VContainer comes from the hadashiA UPM git
  package. Remove both entries at the next dependency pass so NuGetForUnity does not try to resolve Godot packages.
  The 2026-10-26 plan kept them deliberately.
- **Mirror version conflict.** This checkout's `Assets/Mirror/version.txt` says 96.0.1, and its sources agree (no
  `UniqueNameAuthenticator`, which arrived in 96.9.0). The October review notes, however, describe a
  verification-only install of Mirror 96.11.3
  ([fix-wave-2026-10-24/16-unity-mirror-integration.md](fix-wave-2026-10-24/16-unity-mirror-integration.md),
  [pr-review-2026-10-25/11-mirror.md](pr-review-2026-10-25/11-mirror.md)). Mirror is git-ignored (`.gitignore`
  lines 173–176), so each host can differ.

## Lua-CSharp

### Where it lives and what we bundle

- The repository is [nuskey8/Lua-CSharp](https://github.com/nuskey8/Lua-CSharp), MIT, still active (last push
  2026-11-11). `AnnulusGames/Lua-CSharp` redirects to the same repository id (HTTP 301).
- Distribution: the NuGet package [LuaCSharp](https://www.nuget.org/packages/LuaCSharp), which targets net10.0, net8.0,
  net6.0 and netstandard2.1. On netstandard2.1 it depends on `Microsoft.Bcl.TimeProvider` >= 8.0.0 and
  `System.Runtime.CompilerServices.Unsafe` >= 6.0.0.
- There is also an optional Unity extension package, `com.nuskey8.lua.unity` 0.5.8. It is installed from the git URL
  `https://github.com/nuskey8/Lua-CSharp.git?path=src/Lua.Unity/Assets/Lua.Unity`, adds `.lua` assets and module
  loaders, and still needs the core DLL from NuGet. CoreAI does not use it.
- **What we ship:**
  - The pinned commit `d9fb336931…` is exactly tag `v0.5.6` (published 2026-07-29).
  - The product version `1.0.0+d9fb336…` is what every NuGet build of this project carries: `Directory.Build.props`
    sets only `PackageVersion`, so the assembly stays at 1.0.0 with the commit appended.
  - `PROGRESS.research-vmfork.md` (2026-08-31) records that `Assets/CoreAIMods/Plugins/Lua.dll` is byte-identical to
    `lib/netstandard2.1/Lua.dll` of the official NuGet `LuaCSharp` 0.5.6 package (438,272 bytes, SHA-256
    `BE5ACC6819888E3EBDC2509EB7779D52E43B437C8EB1130E24C5CDA0407BAF3F`). Today's NuGet catalog lists the same size.
    `Lua.Annotations.dll` is presumably `LuaCSharp.Annotations` 0.5.6; its size was **not checked**.
  - The bump to stock 0.5.6 is recorded in `Assets/CoreAI/CHANGELOG.md` §6.13.1 (2026-07-31). Before that, CoreAI
    shipped a locally patched 0.5.5.
- **No build or copy script exists in the repository** (none found). The update procedure is therefore manual. To
  repeat it:
  1. Download `LuaCSharp.<ver>.nupkg` and `LuaCSharp.Annotations.<ver>.nupkg` from nuget.org.
  2. Take `lib/netstandard2.1/Lua.dll` and `Lua.Annotations.dll`, and record their SHA-256.
  3. Overwrite the two files in `Assets/CoreAIMods/Plugins/` **keeping the existing `.meta` files**, so the GUIDs and
     import settings stay.
  4. Check that the TimeProvider and Unsafe dependency floors are still met (they are for 0.5.8).
  5. Update `IScriptEngine.EngineVersion` (it reports `0.5.6` today).
  6. Update the docs that name the version, and record the hash in the CHANGELOG.

  Consider committing a small script for steps 1–2, so the next bump is one command.

### Releases after the pin

- **v0.5.7** (2026-09-26, [release notes](https://github.com/nuskey8/Lua-CSharp/releases/tag/v0.5.7)): allocation
  fix #339, `LuaMember` AllowNil and nullable members (#312, #341; these are source generator features that CoreAI does
  not use), the Unity sample project moved to 6000.5.5f1, CI added, and trusted NuGet publishing.
- **v0.5.8** (2026-10-25, [release notes](https://github.com/nuskey8/Lua-CSharp/releases/tag/v0.5.8)): 25 PRs,
  listed by topic below.
- **`main`** is 105 commits past v0.5.8 (2026-10-27 → 2026-11-03,
  [compare](https://github.com/nuskey8/Lua-CSharp/compare/v0.5.8...main)). It is **unreleased**, and PR #400 (opened
  2026-11-11) is still changing runtime edge cases.

### What matters to CoreAI in 0.5.7–0.5.8

**Instruction hooks: the guard's mechanism is touched directly**

- **#353 Restore count hooks after aborted callbacks.** `IsInHook` is now restored in a `finally` when a hook throws,
  is cancelled, or fails frame setup. At v0.5.8, `LuaVirtualMachine.Debug.cs` has the `finally` around the count hook.
  - This is the defect behind CoreAI's cancel-instead-of-throw design: the facts listed in `LuaCsCoroutineHandle.cs`
    lines 553–571 and the `ClearInHookFlag` workaround in `LuaCsExecutionGuard.cs` lines 988–1023.
  - The workaround becomes redundant. It should stay harmless, but that is **unverified**.
- **#354 Restore hooks after frame setup failures.** This also **suppresses the immediate return-hook dispatch when
  `debug.sethook` is called inside a hook**. `ClearInHookFlag` relies on that immediate "return" dispatch, but calls it
  from outside a hook, so it should be unaffected. Verify with the existing trip and re-arm tests.
- **#355 Inherit host hooks when creating Lua threads and coroutines.**
  - `CreateThread`, `CreateCoroutine`, `coroutine.create` and `coroutine.wrap` now copy the creator's host hook. The
    child gets the same callback with a fresh counter, and its own `context.State`.
  - CoreAI re-arms its own hook on every resume: `LuaCsSecureEnvironment.cs` :536/:579 and `LuaCsCoroutineHandle.cs`
    :372/:388/:485. But a coroutine created during a guarded run now starts with the guard's callback already
    installed.
  - Check that the guard's per-run state cannot be charged or tripped from a child before the re-arm.
  - Check that `coroutine.wrap` is still correctly removed. Its stated reason, that the resumer bypasses the guard
    hook, is weaker now, but the token and budget reasons still hold.
  - Upstream notes that inherited monitoring adds the normal hook cost to children that used to run unmonitored.
- **The hook cost is not improved.**
  - The hook is still dispatched as a full Lua function call. Issue
    [#338](https://github.com/nuskey8/Lua-CSharp/issues/338), opened by us on 2026-07-31 with the 3.2x measurement, is
    **still open**.
  - #353 and #355 say the lightweight delegate and budget API is "deferred". #353's own benchmark: no hook 9.2 ms,
    empty `count=4` hook 27.7 ms, the same ~3x as before.
  - So the hook tax in `PERF_BASELINE_2026-11.md` (H2 and item 6) will **not** shrink from this update.

**Allocations and speed**

- **#339 (v0.5.7)**: `LightAsyncValueTaskMethodBuilder<T>` allocated a `Task<T>` every time a generic internal async
  method completed synchronously.
  - Upstream found it in a Unity game: every `__index`/`__newindex` dispatch, about 2.7 KB per frame per scripted
    entity, now zero.
  - CoreAI's Rbx userdata (`LuaCsRbxInstanceProxy`, `LuaCsRbxValueBox`, the UI proxies) goes through exactly these
    metamethods. This is the most likely measurable gain. Re-measure the per-dispatch allocation rows in
    `PERF_BASELINE_2026-11.md`; the size of the gain is **unverified** for CoreAI.
- Other measured costs, all upstream BenchmarkDotNet on .NET 8: #347 makes C# `__index` lookup 3% faster and Lua
  `__index` lookup 3.5% slower; #317 makes a C# call 3.8% slower (native-stack check) and tail calls 3.9% faster; #348
  and #355 are neutral.

**Correctness fixes that mods can hit today on 0.5.6**

- **#346 / issue #345**: after filling a table backwards with 9 or more elements, `#t` is wrong (8 instead of 9), and
  `table.sort` misbehaves. A side effect is visible to mods: `table.insert` no longer shifts values past the first nil,
  which matches Lua 5.2.
- **#347 / issue #344**: `obj:Method()` could receive a stale `self` (a `Vector3` left by an earlier `__newindex`)
  when `__index` is a function. That is the Roblox-style pattern our bindings serve.
- **#348**: concurrent first access to generated metatables. This affects `[LuaObject]` only, which CoreAI does not
  use.
- **#362**: `table.insert/remove/sort/concat/unpack` now honour `__len`.
- **#357, #358, #360, #366**: pattern class escapes, zero-length match at the end of a string, `gsub` with an
  `__index` replacement table, `gsub` replacement strings and `%f`.
- **#351 Align Lua parser diagnostics and validate missing closers**: **parse error texts change**. Re-run every
  CoreAI test that pins a parser or engine message (parse gate, LuauDownlevel, repair prompts, sandbox
  error-line tests).
- **#317**: adds `LuaState.MaxCallDepth` (default 100,000 frames per thread, shared with coroutines) and a native-stack
  check at the common delegate entry.
  - It makes `LuaStackOverflowException` public, possibly wrapped in `LuaRuntimeException`.
    `IsEngineStackOverflow` (`LuaCsSecureEnvironment.cs` :1122–1137) already walks inner exceptions by type name, so
    it keeps working and could now use a typed check.
  - #317 also "preserves wrapped C# exception messages in `coroutine.resume`", so the text a protected resume returns
    can change. Check the one-line error contract tests.
  - The native check is still `RuntimeHelpers`' heuristic, so CoreAI's 128 weighted levels (`MaxCCallDepth`, the
    IL2CPP concern in `LUA_SANDBOX_SECURITY.md`) remain necessary.
- **#367 / #370**: opt-in byte strings (`LuaPlatform.ByteStrings`, default off, so UTF-16 behaviour is unchanged).
  `EncodeText`/`DecodeText` helpers are added and the UTF-8 converters are renamed, with the released v0.5.7 API
  preserved.
  - #367 notes a **binary** break for the `FileSystem(string?)`, `LuaStream(mode, stream)` and `ConsoleStandardIO()`
    constructors. CoreAI does not call them (none found in `Scripting/LuaCs/`), and it compiles from source in any case.

**What does not change**

- **API surface used by the adapter layer.** Checked at v0.5.8: the public `LuaState` members are unchanged apart from
  the added `MaxCallDepth` (`Load(ReadOnlySpan<char>, string, LuaTable?)` only lost its `unsafe` modifier).
  `SetHook(LuaFunction?, string, int)`, `CreateCoroutine(LuaFunction, bool)` and `DebugLibrary.Instance.SetHook` are
  unchanged.
- **Cancellation facts the guard relies on.** These hold at v0.5.8: `pcall` rethrows `LuaCanceledException` and
  converts other `OperationCanceledException`s; `xpcall` checks its token before the handler; the VM calls
  `ThrowIfCancellationRequested` right after a hook returns; `CoroutineCore` catches only non-cancellation exceptions.
  The internals were re-read; Mono and IL2CPP behaviour is **not tested**.
- **AOT, IL2CPP and WebGL.** There is no IL2CPP- or WebGL-specific change in 0.5.7 or 0.5.8. Upstream verified #255
  and #354 in a Unity 6000.5.5f1 editor. #353 and #348 state that IL2CPP, WebGL and Mono player runs were **not**
  verified. The 0.5.6 WebGL deadlock fix (#329) is unchanged.
- **Luau-style syntax.** Still none. `Keywords.cs` at v0.5.8 and `main` lists only the Lua 5.2 keywords (no
  `continue`). Nothing in the scanner, README or releases adds compound assignment, type annotations or `//` (a Lua 5.3
  operator, which 5.2 lacks). `Assets/CoreAIMods/Runtime/LuauDownlevel` stays required.

### Unreleased `main`: watch, do not build from it

- **Memory safety:** "release cancelled coroutines", "release retained runtime references", "reclaim deleted keys",
  "release abandoned coroutine captures". Long-lived mod states that cancel coroutines on budget trips are exactly
  where these would matter.
- **Coroutine cancellation changes:** "protect coroutine yield transfers from cancellation" and "propagate coroutine
  resume cancellation and simplify handoffs". `CoroutineCore` is rewritten. This is the path CoreAI's trip design
  depends on, and it must be re-verified when it ships.
- **Lua 5.2 alignment of the standard library and numerics:** "align finite printf and quoted numeric string
  semantics" and an optional Lua 5.2 numeric-formatting platform option. Expect `tostring` and `string.format` output
  differences in tests.
- **Fewer allocations during compilation:** "Reduce scanner lookup and compilation scratch allocations". This
  matters for LLM-generated chunks.
- **Faster numeric dispatch:** "Streamline numeric VM dispatch".
- **New features:** an embedded debugger with a DAP adapter, opt-in bound instance methods, and optional .NET 11
  support. netstandard2.1 is still a target.

**Recommendation.**

- Move to **0.5.8 now**, on a branch, as one reviewed change. Run these gates:
  - the portable `tools/portable/LuaTests`;
  - the Lua sandbox EditMode suites (guard trip and re-arm, coroutine resume, C-call depth, error-line contracts,
    LuauDownlevel);
  - a `tools/vmbench` re-run against the 08-31 and 11-11 curves;
  - `PERF_BASELINE` allocation rows;
  - the `WebGlLuaSelfTest` player run and one IL2CPP player run.
- Retire the `IsInHook` workaround only after a test shows the new `finally` covers both the throw path and the
  foreign-context path.
- Take **v0.5.9** when it is released, and treat its coroutine cancellation rewrite as a full re-verification of
  `LUA_SANDBOX_SECURITY.md` §"A budget trip is final".
- **Risks:** hook inheritance (#355) changes when the guard callback runs; parser and `coroutine.resume` error texts
  change; `table.insert` and `__len` behaviour changes are visible to existing mods; none of this is IL2CPP/WebGL-proven
  upstream.

## Microsoft.Extensions.AI and the .NET packages

- **Versions** ([NuGet](https://www.nuget.org/packages/Microsoft.Extensions.AI),
  [dotnet/extensions releases](https://github.com/dotnet/extensions/releases)):
  - 9.10.2 is the last 9.x. The 10.x line runs 10.0.0 (2025-11-11) → **10.11.0** (2026-10-14); **11.0.0** shipped
    2026-11-10 with .NET 11.
- **What a move requires.**
  - The netstandard2.0 group of 10.11.0 requires `System.Text.Json` >= 10.0.13. The same floor applies to
    `Microsoft.Extensions.Caching.Abstractions`, `DependencyInjection.Abstractions`, `Logging.Abstractions`,
    `System.Diagnostics.DiagnosticSource`, `System.Numerics.Tensors` and `System.Threading.Channels`. 11.0.0 requires
    11.0.0 of all of them.
  - Unity 6000.6.0f1 ships `Editor/Data/BCLExtensions/runtime/netstandard2.1/System.Text.Json.dll` file version 8.0.18
    (assembly 8.0.0.0), and the reference pack is the same. That is the copy user code compiles and runs against: the
    engine substitutes it for a project copy, as `MeaiVersionFloorEditModeTests` documents.
  - **A Unity project on netstandard2.0/2.1 therefore cannot move to 10.x or 11.x until Unity's BCL extensions carry
    STJ 10+.** When it does, the move is one atomic change:
    - re-vendor MEAI, MEAI.Abstractions, STJ, `System.Text.Encodings.Web`, `DiagnosticSource`, Caching, DI, Logging,
      `Primitives`, `Tensors`, `Bcl.Numerics`, `Channels`, `AsyncInterfaces` and `TimeProvider` on one major;
    - raise `MeaiFloor` and `EngineSystemTextJsonMajor`;
    - update `Assets/CoreAI/package.json` `nugetRefs`, `tools/portable/CoreAI.Core.csproj`,
      `CoreAINuGetBootstrapper` and `Assets/CoreAiUnity/link.xml`;
    - run an IL2CPP and WebGL stripping check.
  - Observation, meaning unverified: the 6000.6 editor also contains STJ 10.0.x builds under `il2cpp/build/deploy`
    and `PlaybackEngines/windowsstandalonesupport/il2cpp/unityaot`. They belong to the IL2CPP toolchain and AOT class
    libraries, not to the BCL extensions scripts compile against, so they do not lift the ceiling today.
  - API: the v10.0.0 release notes list only small AI changes (`AIFunctionFactory` naming, `DefaultValue`,
    `ConversationId`), and the AI changelog file at the v10.0.0 tag ends at 9.10.2. The 10.1–11.0 AI API changes
    were **not checked**; 11.0.0 marks `HybridCacheOptions.DisableCompression` obsolete.
- **.NET 8 and .NET 9 reached end of support on 2026-11-10**
  ([support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)). Every 8.x and 9.x
  package CoreAI is held on by the engine (STJ 8.0.6, AsyncInterfaces 8.0.0, Logging 8.0.3, Channels 8.0.0,
  TimeProvider 8.0.1) gets no further fixes; 9.0.23 is the last .NET 9 build. This is a standing risk to track, not
  something CoreAI can fix.
  - The November 2026 advisories ([dotnet/announcements#498](https://github.com/dotnet/announcements/issues/498))
    were **not checked** package by package. No new 8.x `System.Text.Json` package was issued (8.0.6 is still the
    newest).
- **Tensors and Bcl.Numerics 9.0.23** (2026-11-10,
  [9.0.23 release notes](https://github.com/dotnet/core/blob/main/release-notes/9.0/9.0.23/9.0.23.md)) are routine
  servicing builds; package-level changes were **not checked**.
  - Moving 9.0.22 → 9.0.23 repeats the identity step the 2026-10-26 plan already took (assembly 9.0.0.22 → 9.0.0.23
    while MEAI 9.10.2 references 9.0.0.10). Do it at the next release as one pair, with the same compile and
    stripping checks.

## UPM git packages

- **VContainer** 1.19.0 is the latest release
  ([releases](https://github.com/hadashiA/VContainer/releases)). `master` has four unreleased commits: dispose a
  `LifetimeScope` with `DestroyImmediate` outside Play Mode (#861), and a source-generator diagnostic fix (#866).
  Stay.
- **MessagePipe** 1.8.2 ([releases](https://github.com/Cysharp/MessagePipe/releases)), **UniTask** 2.5.11
  ([releases](https://github.com/Cysharp/UniTask/releases)) and **R3** 1.3.1
  ([releases](https://github.com/Cysharp/R3/releases)) are all latest. The commits since are CI, typo and README
  changes (R3 added a README workaround note for a missing `Microsoft.Bcl.AsyncInterfaces` in a future Unity). Stay.
- **LLMUnity** 3.0.3 (2026-03-08, [releases](https://github.com/undreamai/LLMUnity/releases)). The pin equals `main`
  HEAD and nothing newer exists. Stay.
- **MCP for Unity** 10.3.0 (2026-10-04, [release](https://github.com/CoplayDev/unity-mcp/releases/tag/v10.3.0)) is the
  latest stable. The `beta` branch is 138 commits ahead (10.3.1-beta.11, including a `read_console` log-type fix and
  Windows terminal UTF-8). Stay until 10.3.1 is stable; then update the client and the server together, as the
  10-26 plan describes.
- **NeoxiderTools** 10.19.0 → **12.2.0**
  ([changelog at v12.2.0](https://github.com/NeoXider/NeoxiderTools/blob/v12.2.0/Assets/Neoxider/CHANGELOG.md),
  [Migration12](https://github.com/NeoXider/NeoxiderTools/blob/v12.2.0/Assets/Neoxider/Docs/Migration12.md)).
  - CoreAI references only `Neo.Save`, `Neo.Progression` and `Neo.Tools.Random`, and only from `Assets/_exampleGame`.
  - **12.0.0** (2026-11-04) moved HtmlToUitk out of the package (CoreAI does not use it). It also triggers one full
    texture re-import on the first open, and changes some defaults.
  - Source notes for the modules CoreAI uses: `RandomExtensions.GetRandomEnumValue<T>()` throws for an empty enum;
    `FileSaveProvider.TryChangeSlot("")` returns false; `ChanceData.OnIdGenerated` is now a property;
    `ProgressionRewardDispatcher` gained an `IReadOnlyList` overload.
  - **12.2.0** (2026-11-11) changes only distribution: install with
    `https://github.com/NeoXider/NeoxiderTools.git#upm`, about 4.7 MB instead of about 104 MB.
  - Recommendation: update at the next release, pinning the `upm` commit of 12.2.0, and compile the example game.

## Mirror

- 96.0.1 (2025-02-27) → **96.11.4** (2026-11-07) ([releases](https://github.com/MirrorNetworking/Mirror/releases)).
  Changes that matter here:
  - **96.11.0 security:** "ReadHashSet allocation attack" and "Unbatcher overflow". Both are reachable from network
    input.
  - **WebGL:** `NetworkServer` no longer listens on WebGL (96.9.13); no error flood for unreliable entity-state
    messages (96.9.23); a wrong channel no longer breaks WebGL (96.10.1).
  - **Simple Web Transport:** per-message allocations removed (96.9.16); needs Unity 2022.3+ (96.9.14).
  - **Host-mode behaviour changes:** `[Command]`s run immediately on host (96.8.1) and fill the sender connection
    (96.8.4, 96.9.4); SyncVar hooks and SyncCollection actions are deferred until `OnObjectSpawnFinished` (96.9.9,
    96.9.10).
  - **Time:** `NetworkTime` offers scaled and unscaled time (96.11.0).
  - **Pooling:** unbatcher writers return to the pool on disconnect, and `NetworkIdentity._connectionToClient` is not
    serialized during a domain reload (96.11.4).
- Recommendation: **update every host's install now**, given the security fixes and the WebGL fixes, and write the
  expected version into the install docs.
  - Expect host-mode test differences from the 96.8–96.9 changes. The October review notes indicate CoreAI's Mirror
    layer was already read against 96.11.3.
  - Remember the editor-define rule: `MIRROR`/`EDGEGAP` must not be committed in `ProjectSettings.asset`.

## Unity registry packages

Source: `https://packages.unity.com/<name>`, read 2026-11-11.

- **Netcode for GameObjects** 2.13.3 → **2.14.0** (registry 2026-11-10, changelog dated 2026-11-09,
  [changelog](https://docs.unity3d.com/Packages/com.unity.netcode.gameobjects@2.14/changelog/CHANGELOG.html)).
  - The release is fixes only: NetworkAnimator late-join transitions; `FastBufferWriter`/`FastBufferReader`/`BitWriter`
    growth and offset bugs; `NetworkList` duplicates during sync; a `NetworkManager` NRE in derived `Awake`; a
    `NetworkTime.TickOffset` correction, which now yields tick -1 between minus one tick and zero.
  - Only `Assets/_exampleGame/SymbiosisMode` uses NGO. 3.0–3.2.1 require Unity 6000.7 betas.
  - Recommendation: next release.
- **Up to date:** Input System 1.20.1, Multiplayer Services 2.4.1, Multiplayer Tools 2.2.12, AI Navigation 2.0.16.
- **Built into the editor:** Test Framework 1.8.0 (Unity NUnit 2.1.0), URP 17.6.0, and Transport, which resolves to
  the built-in 6.6.0 in `packages-lock.json` (the registry's 2.7.5 is the older line). All stay with the engine.

## com.neoxider.markdownrenderer

- [NeoXider/NeoxiderMarkdownRenderer](https://github.com/NeoXider/NeoxiderMarkdownRenderer): `package.json` 1.2.1;
  tags v1.2.0 and v1.2.1, both 2026-10-28; no GitHub release objects.
- 1.2.1 resolves the package root of an embedded copy without trimming the path. 1.2.0 made it a standalone public
  repository with licence notes.
- CoreAI does not reference this package. Its chat's `MarkdownRenderer` is CoreAI's own class, and
  `Assets/NeoxiderTools/Docs` only holds a fork note.
