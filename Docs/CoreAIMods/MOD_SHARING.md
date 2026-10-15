# Mod Sharing Format

## 1. Why share mods

CoreAI's modding pillar is built around an AI (or a player) writing small Lua mods at
runtime: hooks, timers, world edits, logic overrides. A mod written in one session — often
authored by the in-game AI via the `manage_mods` tool — is just text plus a manifest, so it
can travel: between play sessions (the source store), between projects, and between players.
That is the UGC loop: content produced *inside* one game strengthens the ecosystem for
everyone else — the shared-mod format is what lets that content travel.

The runtime can serialize a mod into a single self-contained JSON bundle and load such a bundle
back with safe capability masking. There is no public gallery for bundles: they travel as text
(§3).

## 2. The shareable mod format

### 2.1 Bundle shape

`ExportMod(caller, id)` (on `ILuaModRuntime`, implemented by `LuaCsModRuntime`) produces, for a
loaded or a stored mod, one JSON object with exactly two keys:

```json
{ "manifest": { ... }, "source": "..." }
```

- `manifest` — the mod's `LuaModManifest` serialized with Json.NET default member names,
  i.e. **PascalCase** field names (`Id`, `Name`, `Capabilities`, ...). The two wrapper keys
  are explicitly lowercase (`manifest`, `source`).
- `source` — the complete Lua source of the mod as a single string (the `main.lua` entry;
  a bundle carries exactly one file, there is no multi-file or asset payload).

Implementation: `Assets/CoreAIMods/Runtime/LuaExecution/LuaCsModRuntime.cs`
(`ExportMod` / `ImportMod` / private `LuaModBundle`). The manifest contract lives in
`Assets/CoreAIMods/Runtime/LuaExecution/LuaModManifest.cs`.

### 2.2 Manifest fields

| Field | Type | Meaning |
|---|---|---|
| `Id` | string | Stable mod identifier; also the storage key. **Required on import** — a bundle with a blank/missing id is rejected. |
| `Name` | string | Display name. |
| `Description` | string | Free-text description. |
| `Version` | string | Free-form version string (see §2.4). |
| `Category` | string | `/`-separated category path used for tree grouping in the Hub. |
| `Tags` | string | Comma-separated tags for search/filtering. |
| `Author` | string | Attribution. |
| `OwnerActorId` | string | The actor that owns the mod on the exporting host; empty = host. Ignored on import: the importing actor becomes the owner. |
| `Capabilities` | string | Requested `LuaCapabilities` flag set rendered as a string (e.g. `"Read, Gameplay, WorldEdit"`). **A request only** — never trusted on import (see §4). |
| `Active` | bool | Whether the mod auto-loads on rehydrate. |
| `Origin` | string | Host-local bundled-mod marker (e.g. `resources`). Empty for user-authored mods. |
| `SeededVersion` / `SeededHash` | string | Host-local seed markers (bundled-mod update detection; the hash is FNV-1a 32-bit of the source). Meaningless on a foreign host. |
| `UpdateAvailable` | bool | Host-local "bundled update pending" flag. Meaningless on a foreign host. |
| `Entry` | string | Entry file name, defaults to `main.lua`. |
| `LoadOrder`, `SuspendedAfterBudgetTrips` | long, bool | Host-local; written only when set, so they appear only in the export of a mod that is stored but not loaded ([mod-system.md](mod-system.md) §2). |

Missing manifest fields deserialize to their defaults; unknown keys are ignored.

What export writes depends on the mod's state. For a **loaded** mod the runtime builds the
manifest from the source's `@coreai` header (name, description, category, tags, author, and
`version`, or the revision count when the header has none), with the mod's live tier and
`Active = true`; the seed markers stay empty. For a mod that is only **stored**, it copies the
stored manifest. On import the runtime reads only `Id` and `Capabilities` from the manifest and
rebuilds the rest from the header (§2.3) when it persists the mod. **The header, not the
manifest, is the durable metadata carrier** — always fill it in.

### 2.3 The `@coreai` header

Metadata is declared at the top of the Lua source in a block comment (parsed by
`Assets/CoreAIMods/Runtime/LuaExecution/LuaModHeader.cs`; a `-- @coreai key: value`
line-comment form also exists):

```lua
--[[@coreai
id: night_ambience
name: Night Ambience
version: 1.0.0
capabilities: Read, Gameplay
category: Ambience
author: yourname
tags: audio, day-night
description: Dims the world and plays crickets while the in-game clock says night.
]]
```

Recognized keys: `id`, `name`, `version`, `capabilities`, `category`, `author`,
`description`, `tags`, `active`. Unspecified `capabilities` defaults to `All`
(= every standard tier, **never** `Full`).

### 2.4 Versioning semantics

`Version` is a free-form string:

- The `version:` key in the `@coreai` header is author-declared (semver recommended). When the
  header has one, the runtime writes it into the persisted and exported manifest.
- When the header has none and the host wires a version store (the Unity installer does), the
  runtime uses the mod's revision count instead — `"1"` for a freshly loaded mod, `"3"` after
  three distinct edits. Every successful load/reload of *changed* source appends a revision; a
  no-op reload does not.

For sharing, treat the header `version:` as the authoritative, human-meaningful version and
bump it per published change; the numeric revision count is a per-host edit counter, not a
release number.

### 2.5 Example export bundle

```json
{
  "manifest": {
    "Id": "night_ambience",
    "Name": "Night Ambience",
    "Description": "Dims the world and plays crickets while the in-game clock says night.",
    "Version": "1.0.0",
    "Category": "Ambience",
    "Tags": "audio, day-night",
    "Origin": "",
    "SeededVersion": "",
    "SeededHash": "",
    "Author": "yourname",
    "OwnerActorId": "",
    "Capabilities": "Read, Gameplay",
    "Active": true,
    "UpdateAvailable": false,
    "Entry": "main.lua"
  },
  "source": "--[[@coreai\nid: night_ambience\nname: Night Ambience\nversion: 1.0.0\ncapabilities: Read, Gameplay\ncategory: Ambience\nauthor: yourname\ntags: audio, day-night\ndescription: Dims the world and plays crickets while the in-game clock says night.\n]]\n\nhooks_every(5.0, function()\n    local hour = tonumber(store_get(\"clock_hour\")) or 12\n    if hour >= 21 or hour < 6 then\n        time_set_scale(0.9)\n    end\nend)\n"
}
```

### 2.6 What is intentionally NOT exported

- **Per-mod persistent state** — `store_set`/`store_get` values live in the separate
  `ILuaModStore` and never travel with the bundle. A shared mod starts with an empty store
  on the receiving host (write mods accordingly: `tonumber(store_get(k)) or default`).
- **Revision history** — the version store stays local; the importer starts a fresh history.
- **The granted capability tier** — only the *requested* set is carried, and it is re-masked
  on import (§4).
- **Assets** — no prefabs, audio clips, textures, or extra files. A bundle is Lua + metadata
  only; anything the mod references must already exist in the receiving game.
- **Host configuration** — error budgets, allowed scenes, rate limits, `allowFull` are all
  decided by the receiving host, never by the bundle.

## 3. How to share a mod today

Both directions go through the system clipboard via the Hub's Mods page
(`Assets/CoreAIMods/Runtime/HubIntegration/HubModsPage.cs`).

### Export

1. Open the CoreAI Hub in game and switch to the **Mods** tab.
2. Find the mod row (search box filters by name/category/tags) and click **Export**.
3. The full JSON bundle is copied to the clipboard (`GUIUtility.systemCopyBuffer`); the
   status line confirms `Copied '<id>' export bundle to clipboard.`
4. Paste it anywhere text goes: a GitHub gist, a repo file, a Discord message, a pastebin.
   Recommended file name: `<id>.coreai-mod.json` (any name works — import reads the
   clipboard, not files).

### Import

1. Copy the entire bundle JSON to the clipboard.
2. Hub → **Mods** tab → toolbar **Import** button.
3. On success the mod loads immediately, is persisted to the local store, and appears in the
   list. On failure the status line says so: an empty clipboard has its own message, and a
   refused bundle (malformed JSON, missing `source`, missing `manifest.Id`, a Lua load error)
   reads `Import failed: the clipboard does not contain a valid mod bundle.`; the runtime log
   names the reason.

Notes:

- The toolbar **Paste** button is different: it expects *raw Lua source* (not a JSON bundle)
  and opens it in the mod editor for review before saving. Use Paste when someone shares
  bare Lua; use Import for exported bundles.
- If a mod with the same id is already loaded, Import **reloads it in place** with the new
  source and keeps that mod's current capability tier — convenient for updates, and a reason
  to review ids from strangers (§4).
- The AI can perform the same round trip through the `manage_mods` tool (`export` /
  `import` actions), so "share the mod you just wrote" works as a chat instruction.

## 4. Safety when importing third-party mods

Importing a mod means running someone else's code. The sandbox makes that survivable; the
tier system makes it granular. Full details:
[LUA_SANDBOX_SECURITY.md](../../Assets/CoreAI/Docs/LUA_SANDBOX_SECURITY.md) and
[LUA_ACCESS_MODES.md](../../Assets/CoreAI/Docs/LUA_ACCESS_MODES.md).

### Capability tiers — request vs. grant

A bundle's `Capabilities` is only a **request**. On import it is intersected with the host's
grant, and `Full` is stripped unless the host explicitly passes `allowFull: true` (the Hub
service is constructed with this ceiling; see `HubModServiceBase` /
`ILuaModRuntime.ImportMod`). A shared mod can therefore never escalate itself.

| Tier | What it reaches | Import stance |
|---|---|---|
| `Read` | World queries, logging — no side effects | Safe |
| `Gameplay` | Time scale, read-only input | Safe (cosmetic annoyance at worst) |
| `WorldEdit` | Creating, changing and destroying Rbx instances (`Instance.new`, properties, `:Destroy()`); the classic `coreai_world_*` build calls only on hosts that opt in | **The sensible default ceiling for imported mods** — visible, revertible game-world effects |
| `LogicOverride` | Redefines logic slots (formulas, loot tables) | Review what it overrides before granting |
| `Full` | Reflection over GameObjects/components (`unity_*`) | **Dev-only. Never grant to untrusted imports.** Not part of `All`; opt-in per host |

Functions outside the granted tiers are *physically absent* from the mod's globals — a
`WorldEdit` mod does not have `unity_*` to call.

### What the sandbox removes outright

Regardless of tier, mod Lua has **no filesystem, process, or OS access**, and no network access
unless the host installs an `HttpService` request policy and transport
(`LuaCsModStackOptions.RbxHttpPolicy` / `RbxHttpTransport`; the default is deny-all). Per the sandbox
security doc, the environment removes `io`, `debug`, `package`, `require`, `load`, `loadfile`,
`dofile`, the stock `os` library (the Rbx API provides only `os.time`/`os.clock`), and all arbitrary
CLR/Unity reflection entry points; hosts only ever add narrow, validated C# bindings. Runaway
code is bounded by per-resume instruction, wall-clock and memory budgets (live-heap growth,
256 MB by default), `string.rep`/`table.concat` output caps, and a mod error budget with
quarantine (a repeatedly failing mod stays loaded but stops dispatching until it is reloaded).

### What to actually look for when reviewing untrusted Lua

Since exfiltration and system damage are off the table, review for in-game abuse:

- Griefing via granted tiers: mass `Instance` creation or `:Destroy()` calls, time-scale spam, or
  (on hosts that opt into the classic build bindings) `coreai_world_*` destruction and scene loads.
- Resource pressure: tight `hooks_every` intervals, allocation-heavy loops (bounded but
  still a frame-time tax), unbounded `store_set` growth.
- Deception: a `description` that does not match the code, `report` output that imitates
  system messages, or an `Id` that shadows a mod you already trust (import reloads
  same-id mods in place).
- A `capabilities:` request broader than the code needs — not dangerous by itself (masking
  applies), but a signal of carelessness or probing.

### The revert safety net

Every load/reload of changed source records a revision. If an imported mod (or an update to
one) misbehaves: disable it with the row toggle (unload, kept dormant), roll back via the
version history (`manage_mods` `versions`/`revert`, or `TryRevertMod(caller, id, revisionIndex, out
restored)` — a non-destructive revert that reloads an older revision as the new current one), or
**Delete** to remove the
package entirely. A failed revert/reload leaves the running mod untouched.

## 5. Related documents

- [LUA_SANDBOX_SECURITY.md](../../Assets/CoreAI/Docs/LUA_SANDBOX_SECURITY.md) — the security boundary: removed APIs, budgets, attack vectors to test.
- [LUA_ACCESS_MODES.md](../../Assets/CoreAI/Docs/LUA_ACCESS_MODES.md) — capability tiers, Full mode, `manage_mods` tool surface.
- [LUA_GAME_API.md § Persistence & Sharing](../../Assets/CoreAI/Docs/LUA_GAME_API.md) — source store, export/import API contract, revision history and rollback.
- [FIRST_MOD.md](../../Assets/CoreAI/Docs/FIRST_MOD.md) — writing your first mod.
- [mod-system.md](mod-system.md) / [mod-authoring.md](mod-authoring.md) — mod runtime internals and authoring notes.
