# Mod Sharing Format And Community Gallery

Status: format sections (1–3, 5) describe the **shipped implementation** as of 2026-07-10.
Section 4 (community gallery) is a **process proposal** — nothing in it exists yet.

Related product bet: the UGC loop ("shareable mod format + gallery + one-click import from the Hub")
is the modding-loop moat — tracked in [BACKLOG.md](../../Assets/CoreAiUnity/Docs/BACKLOG.md)
("Idea / positioning bets").

## 1. Why share mods

CoreAI's modding pillar is built around an AI (or a player) writing small Lua mods at
runtime: hooks, timers, world edits, logic overrides. A mod written in one session — often
authored by the in-game AI via the `manage_mods` tool — is just text plus a manifest, so it
can travel: between play sessions (the source store), between projects, and between players.
That is the UGC loop: content produced *inside* one game strengthens the ecosystem for
everyone else, and it is the one moat that grows through other people's work rather than
ours.

The runtime side of this loop already ships: every mod runtime can serialize a mod into a
single self-contained JSON bundle and load such a bundle back with safe capability masking.
What is missing is the social side — a public place to put bundles and a review process.
That is section 4.

## 2. The shareable mod format

### 2.1 Bundle shape

`ExportMod(id)` (both VMs: `LuaCsModRuntime` — the primary Lua-CSharp VM — and the legacy,
optional MoonSharp `LuaModRuntime`) produces one JSON object with exactly two keys:

```json
{ "manifest": { ... }, "source": "..." }
```

- `manifest` — the mod's `LuaModManifest` serialized with Json.NET default member names,
  i.e. **PascalCase** field names (`Id`, `Name`, `Capabilities`, ...). The two wrapper keys
  are explicitly lowercase (`manifest`, `source`).
- `source` — the complete Lua source of the mod as a single string (the `main.lua` entry;
  a bundle carries exactly one file, there is no multi-file or asset payload).

Implementation: `Assets/CoreAIMods/Runtime/LuaExecution/LuaCsModRuntime.cs`
(`ExportMod` / `ImportMod` / private `LuaModBundle`) and the identical pair in
`Assets/CoreAIMods/Runtime/LuaExecution/LuaModRuntime.cs`. The manifest contract lives in
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
| `Capabilities` | string | Requested `LuaCapabilities` flag set rendered as a string (e.g. `"Read, Gameplay, WorldEdit"`). **A request only** — never trusted on import (see §5). |
| `Active` | bool | Whether the mod auto-loads on rehydrate. |
| `Origin` | string | Host-local bundled-mod marker (`resources`, `streamingassets`, ...). Empty for user-authored mods. |
| `SeededVersion` / `SeededHash` | string | Host-local seed markers (bundled-mod update detection; the hash is FNV-1a 32-bit of the source). Meaningless on a foreign host. |
| `UpdateAvailable` | bool | Host-local "bundled update pending" flag. Meaningless on a foreign host. |
| `Entry` | string | Entry file name, defaults to `main.lua`. |

Missing manifest fields deserialize to their defaults; unknown keys are ignored.

Note an asymmetry worth knowing: when you export a **currently loaded** mod, the runtime
builds a minimal manifest (`Name` = id, capabilities = the mod's live tier, `Version` = the
revision count) rather than copying the stored one. The rich metadata still travels, because
it lives in the Lua source itself as the `@coreai` header (§2.3) and the receiving host
re-parses that header when it persists the import. **The header, not the manifest, is the
durable metadata carrier** — always fill it in.

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

`Version` is a free-form string with two conventions in play:

- When the host wires a version store (the Unity installer does), the runtime **auto-derives**
  the persisted/exported version as the mod's revision count — `"1"` for a freshly loaded
  mod, `"3"` after three distinct edits. Every successful load/reload of *changed* source
  appends a revision; a no-op reload does not.
- The `version:` key in the `@coreai` header is author-declared (semver recommended) and is
  what the Hub's save path writes into the stored manifest.

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
    "Capabilities": "Read, Gameplay",
    "Active": true,
    "UpdateAvailable": false,
    "Entry": "main.lua"
  },
  "source": "--[[@coreai\nid: night_ambience\nname: Night Ambience\nversion: 1.0.0\ncapabilities: Read, Gameplay\ncategory: Ambience\nauthor: yourname\ntags: audio, day-night\ndescription: Dims the world and plays crickets while the in-game clock says night.\n]]\n\nhooks_every(5.0, function()\n    local hour = tonumber(store_get(\"clock_hour\")) or 12\n    if hour >= 21 or hour < 6 then\n        time_set_scale(0.9)\n        play_sound(\"crickets\", 0.4)\n    end\nend)\n"
}
```

### 2.6 What is intentionally NOT exported

- **Per-mod persistent state** — `store_set`/`store_get` values live in the separate
  `ILuaModStore` and never travel with the bundle. A shared mod starts with an empty store
  on the receiving host (write mods accordingly: `tonumber(store_get(k)) or default`).
- **Revision history** — the version store stays local; the importer starts a fresh history.
- **The granted capability tier** — only the *requested* set is carried, and it is re-masked
  on import (§5).
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
   list. On failure the status line explains why (empty clipboard, malformed JSON, missing
   `source`, missing `manifest.Id`, or a Lua load error).

Notes:

- The toolbar **Paste** button is different: it expects *raw Lua source* (not a JSON bundle)
  and opens it in the mod editor for review before saving. Use Paste when someone shares
  bare Lua; use Import for exported bundles.
- If a mod with the same id is already loaded, Import **reloads it in place** with the new
  source — convenient for updates, and a reason to review ids from strangers (§5).
- The AI can perform the same round trip through the `manage_mods` tool (`export` /
  `import` actions), so "share the mod you just wrote" works as a chat instruction.

## 4. Community gallery process (PROPOSAL)

> Everything in this section is a proposal for a process that does not exist yet. It needs a
> repository, an owner, and at least one reviewer before it is real.

### 4.1 Repository layout

A public GitHub repository, working name `coreai-mods`, one folder per mod:

```
coreai-mods/
  README.md                     # what this is, how to import, safety notice
  mods/
    yourname.night-ambience/
      mod.json                  # the exact export bundle, unmodified
      README.md                 # what it does, which game APIs/tiers it needs, changelog
      screenshot.png            # one image or short gif of the mod in action
  .github/
    PULL_REQUEST_TEMPLATE.md    # the submission checklist below
```

Conventions:

- Folder name = `<author>.<mod-id-kebab>`; the `Id` inside `mod.json` should be namespaced
  the same way (e.g. `yourname.night_ambience`) so two authors' mods cannot collide or
  silently replace each other on import.
- `mod.json` is the untouched output of Hub Export — reviewers diff it against the source
  embedded in it; no hand-edited divergence between manifest and header.
- One mod per PR; updates bump `version:` in the header and describe changes in the mod's
  README changelog.

### 4.2 Submission checklist (PR template)

- [ ] `mod.json` is a verbatim Hub Export bundle (valid JSON, `manifest` + `source` keys).
- [ ] `@coreai` header is complete: `id`, `name`, `version`, `capabilities`, `author`,
      `description` (and `category`/`tags` where sensible).
- [ ] `Id` is namespaced with your author name and matches the folder name.
- [ ] `capabilities:` requests the **minimum** tiers the mod needs — and never `Full`.
- [ ] README states which game bindings the mod calls (e.g. `coreai_world_*`, `time_*`) so
      hosts know whether their game exposes them.
- [ ] Mod was tested via Hub Import on a clean project/profile (fresh store, no leftover
      `store_*` state) and loads without errors.
- [ ] Screenshot or gif included.
- [ ] No obfuscated, minified, or generated-then-unreadable Lua; the source is reviewable.

### 4.3 Review checklist (maintainer)

- [ ] Import the bundle in a sandboxed project with the default grant (no `allowFull`);
      confirm it loads, runs, and unloads cleanly.
- [ ] Read every line of `source`. Reject anything the reviewer cannot follow.
- [ ] Capabilities audit: header request matches what the code actually calls; flag any
      `unity_*` usage (requires `Full` — dev-only, should not appear in gallery mods).
- [ ] Hostility audit per §5: unbounded loops/recursion, string/table amplification, timer
      floods (`hooks_every` with tiny intervals), store spam, misleading `report` output.
- [ ] Id/namespace check: no collision with an existing gallery mod, no impersonation of
      bundled mod ids (`sample_*`).
- [ ] Metadata sanity: description matches behavior; screenshot plausibly from this mod.
- [ ] License/attribution: the PR author has the right to publish the code.

Two approvals to merge while the process is young; automation (JSON schema check, header/
manifest consistency, Lua parse smoke test in CI) can replace one of them later.

## 5. Safety when importing third-party mods

Importing a mod means running someone else's code. The sandbox makes that survivable; the
tier system makes it granular. Full details:
[LUA_SANDBOX_SECURITY.md](../../Assets/CoreAI/Docs/LUA_SANDBOX_SECURITY.md) and
[LUA_ACCESS_MODES.md](../../Assets/CoreAI/Docs/LUA_ACCESS_MODES.md).

### Capability tiers — request vs. grant

A bundle's `Capabilities` is only a **request**. On import it is intersected with the host's
grant, and `Full` is stripped unless the host explicitly passes `allowFull: true` (the Hub
service is constructed with this ceiling; see `HubModServiceBase` /
`LuaCsModRuntime.ImportMod`). A shared mod can therefore never escalate itself.

| Tier | What it reaches | Import stance |
|---|---|---|
| `Read` | World queries, logging — no side effects | Safe |
| `Gameplay` | Time scale, sounds, UI text, read-only input | Safe (cosmetic annoyance at worst) |
| `WorldEdit` | Spawn/move/destroy, scenes, batch world commands | **The sensible default ceiling for imported mods** — visible, revertible game-world effects |
| `LogicOverride` | Redefines logic slots (formulas, loot tables) | Review what it overrides before granting |
| `Full` | Reflection over GameObjects/components (`unity_*`) | **Dev-only. Never grant to untrusted imports.** Not part of `All`; opt-in per host |

Functions outside the granted tiers are *physically absent* from the mod's globals — a
`WorldEdit` mod does not have `unity_*` to call.

### What the sandbox removes outright

Regardless of tier, mod Lua has **no network, filesystem, process, or OS access** — there is
nothing to steal and nowhere to send it. Per the sandbox security doc, the environment
removes `io`, `os`, `debug`, `package`, `require`, `loadfile`, `dofile`, and all arbitrary
CLR/Unity reflection entry points; hosts only ever add narrow, validated C# bindings. Runaway
code is bounded by instruction budgets, wall-clock timeouts, a per-execution total-allocation
budget (default 64 MB), `string.rep`/`table.concat` output caps, coroutine lifetime budgets,
and a mod error budget with auto-unload.

### What to actually look for when reviewing untrusted Lua

Since exfiltration and system damage are off the table, review for in-game abuse:

- Griefing via granted tiers: mass `coreai_world_*` destruction, scene loads, time-scale
  or audio spam.
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
version history (`manage_mods` `versions`/`revert`, or `TryRevertMod` — a non-destructive
revert that reloads an older revision as the new current one), or **Delete** to remove the
package entirely. A failed revert/reload leaves the running mod untouched.

## 6. Related documents

- [LUA_SANDBOX_SECURITY.md](../../Assets/CoreAI/Docs/LUA_SANDBOX_SECURITY.md) — the security boundary: removed APIs, budgets, attack vectors to test.
- [LUA_ACCESS_MODES.md](../../Assets/CoreAI/Docs/LUA_ACCESS_MODES.md) — capability tiers, Full mode, `manage_mods` tool surface.
- [LUA_GAME_API.md § Persistence & Sharing](../../Assets/CoreAI/Docs/LUA_GAME_API.md) — source store, export/import API contract, revision history and rollback.
- [FIRST_MOD.md](../../Assets/CoreAI/Docs/FIRST_MOD.md) — writing your first mod.
- [mod-system.md](mod-system.md) / [mod-authoring.md](mod-authoring.md) — mod runtime internals and authoring notes.
- [BACKLOG.md](../../Assets/CoreAiUnity/Docs/BACKLOG.md) — "Idea / positioning bets", where the UGC-loop bet lives.
