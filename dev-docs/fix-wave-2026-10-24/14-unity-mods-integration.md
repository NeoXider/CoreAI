# Unity Mods integration repair checkpoint

## Scope and invariants

The goal is to repair the Unity integration failures for startup namespaces, DI composition and acknowledged source persistence. The lease covers only:

- `Assets/CoreAIMods/Tests/EditMode/RbxApi/Acceptance/Mvp3WorldPackageFollowUpEditModeTests.cs`
- `Assets/CoreAIMods/Tests/EditMode/RbxApi/Acceptance/Mvp3WorldPackageEditModeTests.cs`
- `Assets/CoreAIMods/Tests/EditMode/RbxApi/Binding/RbxWorldHostDiWiringEditModeTests.cs`
- `Assets/CoreAIMods/Runtime/Infrastructure/RbxWorldPackageContracts.cs` (approved extension)
- `Assets/CoreAIMods/Runtime/Infrastructure/FileLuaModSourceStore.cs` (approved extension)
- This checkpoint.

Root subsequently approved the two production extensions and the exact compact staging format below. No Git mutation, build, test execution, Unity operation, asset metadata or additional agent belongs to this worker. Root owns executable acceptance. The shared `PLAN.md` remains root-owned; this report preserves the scoped milestone state. `LuaModStoreId.cs` remains unchanged.

Before planning: preserve collision-safe namespaces, verified legacy identity, corrupt-byte protection, exact restart restoration, observable cancellation and the distinction between best-effort writes and acknowledged local installation plus persistence arming. Authentication and session policy remain host responsibilities. Before delegation: no further delegation is authorized. Before accepting: read the real diff, retain all state assertions, and report unexecuted checks explicitly.

## Initial evidence, 2026-10-25

Read `AGENTS.md`, `Docs/ARCHITECTURE_RULES.md`, module 05 and 08 repair contracts, and `artifacts/fix-wave-20261025/unity-failures.json` from the actual `D:/Git/CoreAI` checkout.

- Follow-up acceptance failures use the obsolete `Startup/Stores/<raw-id>` path. `FileRbxWorldPackageStore` now resolves nonempty identities through the injective `LuaModStoreId.ApplyTo` contract. Tests must observe the actual startup directory or use that namespace API while retaining exact file, byte and restart assertions.
- Two follow-up Hub operations use a real `FileLuaModSourceStore` through `RbxWorldRuntimeSessionController.SourceStore`. The controller's `SessionSourceStore` and `ActiveLuaModSourceStore` wrappers implement only the legacy store interface, hiding the strict save/removal contracts. Hub correctly reports that facade as unconfirmed. This needs a production adapter integration, not a fake-success change to the fixture.
- Two DI startup namespace expectations are similarly stale. Five further DI failures occur during actual source replacement, before the intended assertion: `FileLuaModSourceStore.PrepareExactReplacementAsync` fails to write `manifest.json` below a default persistent-data root containing hashed store and mod identity components plus a GUID session component. Each observed path measures 270 characters, longer than the traditional Windows path limit. This is a suspected production path-length regression; no production edit is authorized yet.
- The base acceptance fixture already has its own `[SetUp]` synchronization-context detach and `[TearDown]` restore. Its recently changed manage-mods cancellation assertions use an awaited local helper that preserves cancellation and state snapshots. They do not introduce NUnit synchronous async assertions; the saved full-run guard failure names a different fixture.

## Validation state

Implementation and static source review only. No tests, builds or Unity runs were executed by this worker. Root must rerun the leased fixtures and source-store/deadlock guards after the coherent patch checkpoint.

## Namespace patch checkpoint

Replaced stale raw-identity directory assumptions with `LuaModStoreId.ApplyTo` or the actual file store's startup-directory observation in both affected fixtures. The shared startup-disk seam now resolves the hashed namespace for every restart/corrupt/missing-entry scenario. Exact filenames, stored bytes, previous-selection retention, source/tree restart restoration and injected-failure assertions remain intact. The namespace-isolation scenario also checks the second namespace's resolved path and distinct directory.

At this initial namespace checkpoint no source-persistence fake was modified: the two affected Hub cases use production source-store facades. Production source remained unchanged pending root ownership coordination. The base acceptance fixture required no synchronization-context change; its existing local detach/restore covers the remaining NUnit async assertion.

## Approved production extension checkpoint

The earlier read-only handoff is superseded for the two approved production files. The root approved forwarding strict source ports and compacting only internal world-session versions; ordinary canonical hex identities and public method signatures remain unchanged.

- `SessionSourceStore` and `ActiveLuaModSourceStore` now forward `SaveStrict` and `DeleteStrict` to the current underlying strict ports. Unsupported legacy backends throw before invoking any source mutation. Session change notifications still run after an attempted strict operation, including a partial local failure; exceptions and cancellation propagate without conversion to success.
- New internal versions use `.world-sessions/<26 lowercase Base32 characters>/id-<52 lowercase Base32 characters>`. The first component encodes every bit of the generated GUID; the second encodes every bit of the SHA-256 digest. The unpadded alphabet is `abcdefghijklmnopqrstuvwxyz234567`. No digest is truncated, and every identity path is case-insensitively distinct for the supported collision cases. The observed 270-character default path becomes 252 characters; its later `.tmp` path is 256.
- Each new version owns a fixed `.source-layout` file containing exactly `sha256-base32-v1`. Preparation creates it with `FileMode.CreateNew`, never overwrites a collision, and only cleans an incoming version after this operation created that marker. Existing incoming paths refuse before retention cleanup. Validation reads at most the fixed marker length and refuses missing required, unsupported, malformed, oversized or directory-shaped markers.
- Marker interpretation is reserved to explicitly prepared compact stores and recognized `.world-sessions/<version>` roots. Recognized names are legacy 32-digit GUID hex or complete 26-character Base32 GUID names with valid padding bits. Public reopen detects the compact codec without caller hints. Marker-free legacy versions retain hex names; ordinary roots do not interpret the reserved marker name, preserving a legacy mod called `.source-layout`.
- All incoming mod manifests/sources are snapshotted and validated before filesystem changes. On Windows, preparation preflights every actual manifest, source, temporary and marker path before cleanup, migration or creation. Paths longer than this backend's 259-character limit throw an observable root/length diagnostic. Source admission/writes also preflight their temporary paths before migration or writing; existing source reads check their actual final paths without requiring an unused temporary path.
- Retention keeps the existing active/incoming version fences and skips directories outside recognized owned version-name shapes. Persisted startup packages and ordinary canonical source/store namespaces retain their previous formats. No test root or identity hash was shortened to hide the default-root failure.

Authored regression coverage, execution pending root:

- Legacy source backends cannot acknowledge strict save/removal or receive fallback mutation calls.
- The retained DI facade forwards strict writes/removals to the new backend after a session swap, preserving exact I/O failures and cancellation and leaving refused data unchanged. Its deterministic transactional fake now explicitly implements both strict ports.
- Compact preparation, activation, completion, public reopen, atomic updates/removal, capture and startup restoration preserve case-distinct identities and the actual legacy sanitizer collision pair (`a/b`, `a_b_3a8e75c1`). An independent standard-library SHA-256/Base32 vector for `a` pins all 256 digest bits. Canonical hex sources remain intact across the restart simulation.
- Legacy hex-stage reopen, missing/invalid/oversized marker rejection, directory-marker collision preservation, and ordinary legacy `.source-layout` mod migration remain covered.
- A Windows configured root whose final manifest fits but whose required `.tmp` path is 260 characters refuses before any persistence callback, version cleanup, directory creation or source mutation; all existing source/orphan paths and bytes remain exact. Unrelated version directories also survive normal retention during restore.

## Initial source-review handoff

- `git diff --check --` over all three leased fixtures: exit 0, no diagnostics.
- Reviewed the actual two newly changed fixture diffs. No assertion was removed; the composed-restart path comparison now checks the complete expected resolved path.
- Parsed the saved Unity XML independently: 24 failed cases in `Mvp3WorldPackageFollowUpEditModeTests` and 7 in `RbxWorldHostDiWiringEditModeTests`. The saved deadlock-guard failure names `AuditLogWriterResilienceEditModeTests`, not the base Mods fixture. The complete run log was searched for the affected case and exception path; the structured failure messages provide the path evidence.
- `Mvp3WorldPackageEditModeTests.cs` is unchanged by this worker. Its preexisting cancellation diff retains runtime/source/revision/manual-slot snapshots, awaits `AssertManageModsBackupCancellationAsync`, and catches only `OperationCanceledException` with the injected-message assertion.
- Pending source integrations were sent to root before requesting any production edit: strict save/removal forwarding through both controller wrappers, and real default-root Windows path handling. Changing the DI tests to a shorter source root would conceal the latter regression and was not done.

Root acceptance filters: `CoreAI.Tests.EditMode.RbxApi.Acceptance.Mvp3WorldPackageFollowUpEditModeTests`; `CoreAI.Tests.EditMode.RbxApi.Binding.RbxWorldHostDiWiringEditModeTests`; `CoreAI.Tests.EditMode.RbxApi.Acceptance.Mvp3WorldPackageEditModeTests`; `CoreAI.Tests.EditMode.SynchronousAsyncAssertionDeadlockGuardEditModeTests`. No execution pass is claimed by this handoff.

SHA-256 checkpoint:

| File | SHA-256 |
|---|---|
| `Mvp3WorldPackageFollowUpEditModeTests.cs` | `6c4b4fd16a701d1be8f0ed445cacb8d7896c56442c2d150aaa88ce96e8cdce82` |
| `RbxWorldHostDiWiringEditModeTests.cs` | `5be59716627a04d2823a4dc1b9cf5416d5c6f87f45260273b500626911b43e45` |

## Final source handoff after approved extension

Before accepting: reviewed both production diffs, the new fixture changes, every compact-codec call site, marker/reopen dispatch, strict failure propagation and cleanup ownership. No further source edit is pending in this lease. Root owns executable acceptance and integration review.

- Scoped `git diff --check`: exit 0. Git emitted only the existing file-source CRLF-to-LF normalization notice.
- Added lines across the four changed source/fixture paths contain zero implicit `var` declarations, zero Cyrillic lines and zero unapproved line-comment prefixes.
- No NUnit synchronous async assertion was added. The base acceptance fixture is unchanged by this worker; its SHA-256 still matches the original cancellation handoff.
- No build, portable test, Unity run, Git mutation, asset metadata edit or additional agent was used. Static checks are not execution receipts.

Root acceptance filters: `Mvp3WorldPackageFollowUpEditModeTests`, `RbxWorldHostDiWiringEditModeTests`, `Mvp3WorldPackageEditModeTests`, `FileLuaModSourceStoreEditModeTests`, and `SynchronousAsyncAssertionDeadlockGuardEditModeTests`. The real default-root DI cases must pass without injecting a shorter source directory. All authored restart, strict receipt, collision, cleanup and Windows temporary-path regressions remain unexecuted until root runs those filters.

Final SHA-256 manifest, superseding the earlier namespace-only checkpoint:

| File | SHA-256 |
|---|---|
| `Runtime/Infrastructure/FileLuaModSourceStore.cs` | `d00cc12cf9a745f4618786ed16b20e788e6c833f1bad276ac31beaf203b37f4d` |
| `Runtime/Infrastructure/RbxWorldPackageContracts.cs` | `06e8d63e0018c71c47819308b71aed2f83170c67afdefa4da9464dd22227551f` |
| `Tests/EditMode/RbxApi/Acceptance/Mvp3WorldPackageFollowUpEditModeTests.cs` | `dc001131525570f7a5781ce85a10d69734b917755e18bf094323c47c0098fed8` |
| `Tests/EditMode/RbxApi/Binding/RbxWorldHostDiWiringEditModeTests.cs` | `916ec232b13ba73eca0a513085160aa5337cbf0b58d8a4c67019f83c29d5dae6` |
| `Tests/EditMode/RbxApi/Acceptance/Mvp3WorldPackageEditModeTests.cs` (unchanged here) | `9fbe00e9a9067b3e39b31edf0d17eff88102849e5a38ab5f192371b4b7009954` |
