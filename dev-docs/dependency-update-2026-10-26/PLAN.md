# Dependency update for Unity 6000.6.0f1

Status: six concrete UPM/managed/portable candidates ready, 2026-10-26. Package/version inputs are verified from current primary registries and upstream tags; ALL integration/compatibility outcomes remain UNVERIFIED until root resolves/imports/tests. No live files, lockfile, DLL/meta, Unity/MCP, CLI environment, model or connection were modified by this worker.

Candidate: artifacts/dependency-update-20261026/staging/Packages/manifest.json.
Live baseline SHA256: A40DFD5D316413C76ECD9BC114DF7A38056417F8E6E60DA43D71397E4E140609.
Candidate SHA256: 198D12C46560726CC9B95DD9D2115FB689F43FEE8F2FECE6317DFE8B8CA9D703.
Baseline inventory and machine-readable original requests are in artifacts/dependency-update-20261026/staging-manifest.json. Root must recheck before merging concurrent work.

## Goal and boundaries

Update existing libraries to the newest source-verified release/commit compatible with this installed Unity, without an engine upgrade or new optional dependencies. Preserve all seven CoreAI packages, current defines/features and human manifest intent, including auto-enabled built-in modules. The worker may stage the UPM/NuGet/portable project manifests only. Root owns package resolution, vendored managed restoration, binary/API checks and all runtime/build gates.

## UPM decisions

Git SHA values below are complete immutable commits; original full requested URLs remain in the live baseline/candidate and staging manifest. Registry facts were read from the linked official registry endpoints. All rows are UNVERIFIED at executable integration level.

| Existing dependency | Old requested / resolved | Candidate / latest verified | Decision and primary source |
|---|---|---|---|
| ai.undream.llm | unpinned Git / 2c30b44020c7899153bbca02ec0faae73916c6ec, 3.0.3 | pin same 2c30b44020c7899153bbca02ec0faae73916c6ec | Current main already equals installed code; pin provenance, no native-library/model change. Package requires 2022.3. [Source](https://github.com/undreamai/LLMUnity/blob/2c30b44020c7899153bbca02ec0faae73916c6ec/package.json) |
| com.coplaydev.unity-mcp | unpinned Git / c21bf496bca87d54e75bad048563c3adb1782081; cached 10.1.3-beta.4 | stable 10.3.0 / aa5fc638d623f56178d50329d9ad8c541a57fe66 | Update client to match current server 10.3.0; Unity minimum 2021.3; release explicitly fixes Unity 6000.6 InstanceID compatibility. [Release](https://github.com/CoplayDev/unity-mcp/releases/tag/v10.3.0), [package](https://github.com/CoplayDev/unity-mcp/blob/v10.3.0/MCPForUnity/package.json), [server](https://github.com/CoplayDev/unity-mcp/blob/v10.3.0/Server/pyproject.toml) |
| com.cysharp.messagepipe | 58516c36d4465a7b6396b7850a4ad7e03326998c / same, 1.8.2 | retain same 1.8.2 commit | Already latest stable. Keep integration on the same commit. [Release](https://github.com/Cysharp/MessagePipe/releases/tag/1.8.2) |
| com.cysharp.messagepipe.vcontainer | 58516c36d4465a7b6396b7850a4ad7e03326998c / same, 1.8.2 | retain same | Paired package; no split commit. [Tag ref](https://api.github.com/repos/Cysharp/MessagePipe/git/ref/tags/1.8.2) |
| com.cysharp.r3 | f6eed2dd4208dc4ae171c601e799e85f82aca25e / same, 1.3.1 | retain same | Latest stable UPM and NuGet R3 both 1.3.1. [Release](https://github.com/Cysharp/R3/releases/tag/1.3.1) |
| com.cysharp.unitask | 2e993ff18f28c931602a07292df0b0804eebef99 / same, 2.5.11 | retain same | Latest stable; portable shim NuGet pins still need alignment from 2.5.10. [Release](https://github.com/Cysharp/UniTask/releases/tag/2.5.11) |
| jp.hadashikick.vcontainer | 5401e5a7ebc4980a2b82141ffc26391a6547edd7 / same, 1.19.0 | retain same | Already latest stable Unity VContainer. [Release](https://github.com/hadashiA/VContainer/releases/tag/1.19.0) |
| com.neoxider.tools | unpinned Git / 34fd5146edc4c726c305046cbfac9ff8e9594d4b, cache 10.13.7 | current main 10.19.0 / ca1985564dfe7726834e2832d51c33e0e63e060c | Update/pin existing main channel. Unity minimum 6000.0; dependencies unchanged. Do not downgrade to the older published 10.1.0 tag. Module split/API consumers need compile/runtime verification. [Package](https://github.com/NeoXider/NeoxiderTools/blob/ca1985564dfe7726834e2832d51c33e0e63e060c/Assets/Neoxider/package.json), [changes](https://github.com/NeoXider/NeoxiderTools/blob/ca1985564dfe7726834e2832d51c33e0e63e060c/Assets/Neoxider/CHANGELOG.md) |
| com.unity.ai.navigation | 2.0.14 / same | 2.0.16 | Registry latest; minimum 6000.0. [Registry](https://packages.unity.com/com.unity.ai.navigation) |
| com.unity.collab-proxy | 2.13.6 / same | retain 2.13.6 | Already registry latest; minimum 6000.0. [Registry](https://packages.unity.com/com.unity.collab-proxy) |
| com.unity.ide.rider | 3.0.39 / same | 3.1.0 | Registry latest; minimum 6000.0. [Registry](https://packages.unity.com/com.unity.ide.rider) |
| com.unity.ide.visualstudio | 2.0.27 / same | 2.0.28 | Registry latest; minimum 2021.3. [Registry](https://packages.unity.com/com.unity.ide.visualstudio) |
| com.unity.inputsystem | 1.20.0 / same | 1.20.1 | Registry latest; minimum 6000.0. [Registry](https://packages.unity.com/com.unity.inputsystem) |
| com.unity.multiplayer.center.quickstart | 1.1.1 / same | 2.0.2 | Registry latest minimum 6000.6; requires center 2.0.0, satisfied by engine 2.0.1. [Registry](https://packages.unity.com/com.unity.multiplayer.center.quickstart) |
| com.unity.multiplayer.tools | 2.2.10 / same | 2.2.12 | Registry latest; minimum 6000.0. [Registry](https://packages.unity.com/com.unity.multiplayer.tools) |
| com.unity.netcode.gameobjects | 2.13.2 / same | 2.13.3 | Newest compatible 2.x patch. Latest 3.1.0 and 3.0.0 require Unity 6000.7.0b1, outside 6000.6.0f1. [Registry](https://packages.unity.com/com.unity.netcode.gameobjects) |
| com.unity.services.multiplayer | 2.3.1 / same | 2.4.1 | Registry latest minimum 6000.0. [Registry](https://packages.unity.com/com.unity.services.multiplayer) |
| com.unity.visualscripting | 1.9.12 / same | retain 1.9.12 | Already registry latest; minimum 6000.0. [Registry](https://packages.unity.com/com.unity.visualscripting) |
| com.unity.render-pipelines.universal | 17.6.0 / builtin | retain 17.6.0 | Installed 6000.6 editor BuiltInPackages manifest confirms 17.6.0. Public registry latest 10.10.1 is an older channel, not an upgrade. |
| com.unity.test-framework | 1.8.0 / builtin | retain 1.8.0 | Editor BuiltInPackages confirms 1.8.0; public registry latest 1.4.6 would downgrade. |
| com.unity.timeline | 6.6.0 / builtin | retain 6.6.0 | Editor BuiltInPackages confirms 6.6.0; do not replace with public registry 1.8.x. |
| com.unity.ugui | 2.6.0 / builtin | retain 2.6.0 | Editor BuiltInPackages confirms 2.6.0; do not install experimental registry 3.0.0-exp.4. |
| com.unity.dedicated-server | 3.0.0 / builtin | retain 3.0.0 | Editor BuiltInPackages confirms 3.0.0; public 2.0.2 would downgrade. |
| com.unity.multiplayer.playmode | 3.0.0 / builtin | retain 3.0.0 | Editor BuiltInPackages confirms 3.0.0; public 2.0.2 would downgrade. |
| com.unity.multiplayer.center | 2.0.1 / builtin | retain 2.0.1 | Editor BuiltInPackages confirms 2.0.1; public old prerelease listing is not authoritative for this engine. |
| com.unity.modules.* | existing auto-enabled 1.0.0 list | unchanged byte-for-byte entries/order | Engine-owned modules and human composition preserved. |

The seven internal package manifests remain 7.54.0 with their existing dependency graph/optional module boundaries; they are not third-party package upgrades.

## MCP controlled application gate

Both running uv environments were inspected from their actual mcpforunityserver .dist-info/METADATA files: stdio environment MJiPZ-xwvCoN7bCl and CoreAI HTTP 8040 environment LZlB61z8K8_agGRA are 10.3.0. PyPI current stable is also 10.3.0 and requires Python >=3.10. No global server/uv/Python upgrade is needed.

Root can apply this manifest only after stopping current QA/test jobs at a controlled gate. UPM resolve will reload editor assemblies and can disconnect MCP. Verify the imported Unity client version/SHA and reconnect to the same 10.3.0 server. The currently floating uvx spec permits prereleases; when root owns service configuration/reconnect, pin mcpforunityserver==10.3.0 or the same tagged commit rather than silently taking another prerelease. Do not alter other projects' stdio servers or global tool installations from this task.

## Managed dependency ABI gate

Installed Unity runtime and reference System.Text.Json DLLs are at BCLExtensions/runtime/netstandard2.1 and BCLExtensions/TargetingPacks/netstandard2.1/ref, both assembly 8.0.0.0 with identical SHA256 F80C939BE592939CA3BBBC9DDF61BB137CE201EC38D70005915CB70ACF2D4CC3. The previous short BCLExtensions/System.Text.Json.dll path no longer exists in this editor. System.Text.Encodings.Web and AsyncInterfaces are also assembly 8.0.0.0.

MEAI 9.10.2 is both the enforced repository floor and newest published 9.x. Latest 10.11.0 requires System.Text.Json >=10.0.13 on netstandard2.0 plus Extensions 10.x; it is not compatible with the verified Unity 8.0 ABI ceiling. Retain 9.10.2 and STJ 8.0.6 (already newest 8.x). Do not edit floor tests or remove features to make an incompatible upgrade compile.

Managed patch candidates and portable-project alignment are staged separately below. No DLL or meta was copied and no restore ran. Root must restore selected managed versions atomically with their manifests, check assembly identities and engine substitution, and then run the portable/Unity gates. A project file edit alone does not update Assets/Packages binaries.

## Final managed candidates and retained ceilings

Root approved staging the three changed-identity patches as an atomic UNVERIFIED binding group; identity differences alone are not asserted incompatible. Incoming DLL metadata was read from official NuGet package bytes in memory only: no binary was written, installed, executed or copied into the project.

| Dependency / location | Old requested/resolved | Fresh registry latest / compatible line | Staged decision and exact evidence |
|---|---|---|---|
| Microsoft.Bcl.AsyncInterfaces | 8.0.0 | 10.0.14 / latest 8.x 8.0.0 | Retain engine assembly-8 floor. [Registry](https://api.nuget.org/v3-flatcontainer/microsoft.bcl.asyncinterfaces/index.json) |
| Microsoft.Bcl.Numerics | 9.0.10 | 10.0.14 / 9.0.22 | Stage 9.0.22 in atomic group. Assembly 9.0.0.10 -> 9.0.0.22, netstandard2.1 reference only. [Package](https://api.nuget.org/v3-flatcontainer/microsoft.bcl.numerics/9.0.22/microsoft.bcl.numerics.nuspec) |
| Microsoft.Bcl.TimeProvider | 8.0.0 | 10.0.14 / 8.0.1 | Stage 8.0.1 in Assets/packages.config and both existing portable references. Assembly 8.0.0.0 -> 8.0.0.1. Incoming references netstandard 2.0.0.0, AsyncInterfaces 8.0.0.0, Tasks.Extensions 4.2.0.1. [Package](https://api.nuget.org/v3-flatcontainer/microsoft.bcl.timeprovider/8.0.1/microsoft.bcl.timeprovider.nuspec) |
| Microsoft.Extensions.AI | 9.10.2 | 10.11.0 / latest 9.x 9.10.2 | Retain exact enforced floor; 10.11.0 netstandard requires STJ >=10.0.13 and Extensions 10.x, incompatible with verified engine STJ 8 ABI. [Index](https://api.nuget.org/v3-flatcontainer/microsoft.extensions.ai/index.json), [10.11 requirements](https://api.nuget.org/v3-flatcontainer/microsoft.extensions.ai/10.11.0/microsoft.extensions.ai.nuspec) |
| Microsoft.Extensions.AI.Abstractions | 9.10.2 | 10.11.0 / latest 9.x 9.10.2 | Retain paired exact floor, internal manifests unchanged. [Index](https://api.nuget.org/v3-flatcontainer/microsoft.extensions.ai.abstractions/index.json) |
| Microsoft.Extensions.Caching.Abstractions | 8.0.0 | 10.0.14 / latest 8.x 8.0.0 | Retain latest existing ABI line; no unsupported major transition. [Index](https://api.nuget.org/v3-flatcontainer/microsoft.extensions.caching.abstractions/index.json) |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.2 | 10.0.14 / latest 8.x 8.0.2 | Retain; Unity runtime identity 8.0.0.0 verified. [Index](https://api.nuget.org/v3-flatcontainer/microsoft.extensions.dependencyinjection.abstractions/index.json) |
| Microsoft.Extensions.Logging.Abstractions | 8.0.3 | 10.0.14 / latest 8.x 8.0.3 | Retain latest existing line and MEAI's assembly-8 dependency. [Index](https://api.nuget.org/v3-flatcontainer/microsoft.extensions.logging.abstractions/index.json) |
| Microsoft.Extensions.Primitives | 8.0.0 | 10.0.14 / latest 8.x 8.0.0 | Retain latest existing line. [Index](https://api.nuget.org/v3-flatcontainer/microsoft.extensions.primitives/index.json) |
| R3 | 1.3.1 | 1.3.1 | Retain; matches UPM adapter 1.3.1/tag SHA. Current DLL references TimeProvider 8.0.0.0, so atomic group's 8.0.0.1 binding needs root verification. [Index](https://api.nuget.org/v3-flatcontainer/r3/index.json) |
| System.ComponentModel.Annotations | 5.0.0 | 5.0.0 | Retain already latest stable. [Index](https://api.nuget.org/v3-flatcontainer/system.componentmodel.annotations/index.json) |
| System.Diagnostics.DiagnosticSource | 8.0.1 | 10.0.14 / latest 8.x 8.0.1 | Retain engine assembly-8 line; actual editor 8.0.0.0 verified. [Index](https://api.nuget.org/v3-flatcontainer/system.diagnostics.diagnosticsource/index.json) |
| System.Numerics.Tensors | 9.0.10 | 10.0.14 / 9.0.22 | Stage 9.0.22 atomically with Numerics 9.0.22. Assembly 9.0.0.10 -> 9.0.0.22. [Package](https://api.nuget.org/v3-flatcontainer/system.numerics.tensors/9.0.22/system.numerics.tensors.nuspec) |
| System.Text.Encodings.Web | 8.0.0 | 10.0.14 / latest 8.x 8.0.0 | Retain engine assembly 8.0.0.0. [Index](https://api.nuget.org/v3-flatcontainer/system.text.encodings.web/index.json) |
| System.Text.Json | 8.0.6 | 10.0.14 / latest 8.x 8.0.6 | Retain newest engine-compatible line and portable explicit pin. [Index](https://api.nuget.org/v3-flatcontainer/system.text.json/index.json) |
| System.Threading.Channels | 8.0.0 | 10.0.14 / latest 8.x 8.0.0 | Retain latest existing line. [Index](https://api.nuget.org/v3-flatcontainer/system.threading.channels/index.json) |
| VContainer (NuGet metadata) | 1.0.2 | 1.0.2 | Retain metadata untouched. This is nazgul's Godot/net7 package, NOT hadashiA's Unity VContainer; contains no active DLL in current Assets/Packages. Do not upgrade Unity DI by changing this ID. [Index](https://api.nuget.org/v3-flatcontainer/vcontainer/index.json) |
| VContainerCore (NuGet metadata) | 1.0.4 | 1.0.4 | Retain metadata-only unrelated Godot/net7 package; no active DLL. Its existing Godot/PdEventBus requirements are not new Unity dependencies. [Index](https://api.nuget.org/v3-flatcontainer/vcontainercore/index.json) |
| UniTask, portable shims | 2.5.10 | 2.5.11 | Stage all three existing portable references to 2.5.11, matching already-current UPM commit. [Index](https://api.nuget.org/v3-flatcontainer/unitask/index.json) |
| Newtonsoft.Json, portable Core | 13.0.3 | 13.0.4 | Stage portable patch only. Incoming netstandard2.0 identity remains 13.0.0.0. Unity registry package already latest 3.2.2 ships DLL file 13.0.2, identity 13.0.0.0; real Unity compile against that actual DLL still required. Do not add another Unity Newtonsoft DLL. [Package](https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.4/newtonsoft.json.nuspec), [Unity package](https://packages.unity.com/com.unity.nuget.newtonsoft-json) |
| NUnit | 3.14.0 | 5.0.0 / latest 3.x 3.14.0 | Retain Unity-compatible NUnit-3 Assert API; major migration would require source/test changes outside this dependency batch. [Index](https://api.nuget.org/v3-flatcontainer/nunit/index.json) |
| Microsoft.NET.Test.Sdk | 17.11.1 | 18.11.0 | Retain by root decision: avoid unrelated test-platform major churn; no runtime library requires it. Not represented as latest. [Index](https://api.nuget.org/v3-flatcontainer/microsoft.net.test.sdk/index.json) |
| NUnit3TestAdapter | 4.6.0 | 6.3.1 | Retain by root decision; no adapter/MTP migration in this runtime-library batch. [Index](https://api.nuget.org/v3-flatcontainer/nunit3testadapter/index.json) |
| coverlet.collector | 6.0.2 | 10.1.0 | Retain by root decision; coverage-tool major update not needed for package/library updates. Not represented as latest. [Index](https://api.nuget.org/v3-flatcontainer/coverlet.collector/index.json) |

Incoming Tensors 9.0.0.22 references: netstandard 2.0.0.0, System.Memory 4.0.1.2, System.Numerics.Vectors 4.1.3.0, Microsoft.Bcl.Numerics 9.0.0.22 and System.Runtime.CompilerServices.Unsafe 4.0.4.1. Its NuGet netstandard dependency remains System.Memory >=4.5.5. Existing MEAI 9.10.2 DLL references Tensors 9.0.0.10 and STJ/Logging/AsyncInterfaces 8.0.0.0. These exact identity deltas justify an atomic restore/binding test, not an unsupported assertion that the patches are incompatible.

## Actual binary/duplicate inventory

Read-only assembly metadata found exactly one project non-analyzer DLL for each of MEAI, MEAI.Abstractions, R3, STJ, TimeProvider, Bcl.Numerics and Tensors, all under Assets/Packages with the versions above. No competing VContainer DLL is present under Assets: Unity VContainer is the source UPM assembly. The VContainer/VContainerCore NuGet directories contain metadata/readme/license/icon only, with no Unity-loadable assembly. Preserve them in this scoped candidate; do not silently add Godot dependencies or remove human configuration.

Unity's own STJ 8.0.0.0 and the project's STJ 8.0.0.0 have the expected same identity; engine substitution is why the portable explicit STJ pin and MEAI floor remain. This is not evidence of a conflicting major duplicate. Unity Newtonsoft comes from the existing UPM package; the candidate does not vendor a second copy.

Observed manual project tools include NuGetForUnity assembly/file 4.5.0.0, IngameDebugConsole README 1.8.8, DOTween assembly/file 1.0.0.0 and source-vendored Mirror. They have no selected UPM/NuGet update route in the granted file lease. They are inventoried, not claimed latest or silently replaced. Manual asset/plugin replacement requires its own concrete source/binary plan; no library was deleted for a green compile.

## Root atomic application and acceptance

1. Review/recheck all six live baseline hashes before merging candidates; preserve current Gateway/CoreEditor harness edits. The UPM candidate is independently ready for the controlled resolve/MCP reconnect gate; managed changes form a separate gate.
2. Apply UPM manifest and let the actual Unity 6000.6 Package Manager resolve/write packages-lock.json. Confirm all requested SHA/version outcomes, preserve engine modules, and verify NeoxiderTools module consumers before runtime claims. No worker lockfile edit is supplied.
3. For the managed atomic group, restore through existing NuGetForUnity using candidate Assets/packages.config. Replace stale package versions as one reviewed operation; ensure only one active assembly per name and preserve Unity-created metadata. Do not copy DLL/metas between Unity versions or edit the MEAI/STJ floor guards.
4. Apply the matching three UniTask and two TimeProvider portable references plus the portable Newtonsoft patch. Build Core netstandard2.1 and all affected portable/Source/Lua harnesses, then run focused contract/floor/regression suites with actual result accounting. The manifest-only candidate has not restored binaries.
5. Verify actual Unity compile/import, both LLM/Lua define configurations, Editor tests and relevant Play Mode/player/WebGL/IL2CPP/runtime behavior. If a patch is demonstrably incompatible, retain that proven ceiling and report the actual failure rather than calling it latest-compatible from version numbers alone.
6. Update/reconnect only the project UnityMCP client to stable 10.3.0/SHA after active jobs finish, confirm server/client/tool behavior, and retain the running 10.3.0 server unless a concrete runtime problem requires more. No global tool or model upgrade is part of this work.

## Final candidate inventory and verification boundary

- Packages/manifest.json
- Assets/packages.config
- tools/portable/CoreAI.Core.csproj
- tools/portable/LuaTier/CoreAI.Source/CoreAI.Source.csproj
- tools/portable/LuaTier/CoreAI.Mods/CoreAI.Mods.csproj
- tools/portable/LuaTests/CoreAI.Portable.LuaTests.csproj

All six are mirrored under artifacts/dependency-update-20261026/staging/. Full original/candidate/current hashes and atomic-group metadata are in artifacts/dependency-update-20261026/staging-manifest.json.

CHANGED: six staged candidates, this plan and baseline manifest only. VERIFY OUTPUT: authoritative GitHub release/tag/commit files, official Unity/NuGet/PyPI registries, actual installed assembly and live-process package metadata, source pins and JSON/XML consistency inspection. No build, test, UPM/NuGet restore/install, Unity/MCP call, provider request, Git mutation, lockfile edit, DLL/meta copy or live write was performed. NOT DONE: root resolve/restore/reconnect and every executable compatibility gate; all candidates remain UNVERIFIED. CONCERNS: exact managed identity deltas and version substitution need actual binding proof; manual vendor assets are not claimed upgraded. QUESTIONS: none; root approved the separately unverified managed atomic group and retained test-tool floors.

Final live-state note: Root independently applied the staged UPM manifest during this work. The final live Packages/manifest.json hash equals candidate 198D12C46560726CC9B95DD9D2115FB689F43FEE8F2FECE6317DFE8B8CA9D703; its original baseline remains recorded. The five managed/portable live originals still match their baselines. This confirms bytes only, not UPM resolve/import/runtime success, and is not a worker live write.
