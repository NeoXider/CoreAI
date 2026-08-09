# Your First Lua Mod in 5 Minutes

This is a hands-on quickstart for writing, loading, persisting, and sharing a Lua mod in CoreAI.
To create world objects — parts, folders, cameras, clicks — read [RBX_API.md](RBX_API.md): that
Roblox-style surface is what a mod builds with. For the mod runtime itself (every binding, the
capability tiers, transactions, Full mode) read [LUA_GAME_API.md](LUA_GAME_API.md). For the security
model read [LUA_SANDBOX_SECURITY.md](LUA_SANDBOX_SECURITY.md).

> Lua is an optional module. This guide applies only when your project defines
> `COREAI_LUA`; without it the runtime ships stub bindings instead. See
> [LUA_ACCESS_MODES.md](LUA_ACCESS_MODES.md).

## What a mod is

A **mod** is a long-lived Lua script with a stable id. Unlike a one-shot `execute_lua` snippet, a mod
stays loaded: it registers event hooks and timers once (when it loads) and then reacts to game events
for as long as it is active. CoreAI runs every loaded mod inside the same sandbox as the rest of Lua,
so a mod can only call the functions its **capability tier** grants it — nothing more.

A mod has two halves:

- its **source** (the Lua code), and
- a small **manifest** (`id`, `name`, `description`, `version`, `author`, `capabilities`, `active`,
  `entry`) that travels with the source so the mod can be persisted and shared.

## A minimal mod (copy-paste)

```lua
--[[@coreai
id: greeter
name: Greeter
description: Logs a line when a wave starts and counts how many it has seen.
]]
hooks_on("wave_started", function(evt, payload)
  local seen = tonumber(store_get("waves") or "0") + 1
  store_set("waves", tostring(seen))
  print("greeter: wave " .. payload .. " started (wave #" .. seen .. ")")
end)

hooks_every(5.0, function()
  print("greeter: still running, " .. (store_get("waves") or "0") .. " waves so far")
end)
```

That mod calls no capability-gated game bindings at all: `print` (routed to the mod's log/report
pipeline), `hooks_on`, `hooks_every`, and `store_*` are part of the mod-runtime surface and are
available to **every** loaded mod regardless of capability tier (even a mod loaded with no
capabilities at all) — see the table below.

`hooks_on("tick", fn)` is a convenience alias for `hooks_every(0.05, fn)` (a per-frame-ish callback at
20 Hz) — useful for reading `input_*` state (`Gameplay` tier) every tick instead of writing your own
timer. When a mod needs to react to another mod rather than poll it, `events_emit`/`hooks_on` and
`mods_export`/`mods_call` cover two different needs: events are fire-and-forget broadcasts, exports
let a mod read or call another mod's state directly by id — see
[LUA_GAME_API.md § Cross-mod Exports](LUA_GAME_API.md#cross-mod-exports).

## Capability tiers (short version)

Each mod is loaded at a capability tier; if a tier is not granted, those functions are physically
absent from the mod's globals.

| Tier | What it unlocks |
|---|---|
| `Read` | world queries (`coreai_world_exists/pos/find/list_prefabs/raycast`), version stores (`coreai_lua_*`/`coreai_data_*`) |
| `Gameplay` | `time_*` (time scale, etc.), `input_*` (keyboard/mouse, read-only) |
| `WorldEdit` | `Instance.new` and the rest of the [Rbx API](RBX_API.md) — how a mod creates world objects. The classic `coreai_world_spawn`/`_change`/`_destroy` build functions are **disabled in the default production composition** (a stub errors and points at the Rbx API); they exist only on hosts that deliberately opt in |
| `LogicOverride` | `logic_define/reset/list` |
| `Full` | reflection (`unity_*`) — **opt-in, off by default** |

`LuaCapabilities.All` is every tier **except** `Full`. Full is granted only when the host explicitly
enables it (see the security note at the end).

The mod APIs — `hooks_on`/`hooks_every`, `store_set`/`store_get`, `print`/`report`, `events_emit`,
`mods_export`/`mods_get`/`mods_call`/`mods_list_exports`, `mod_id` — aren't in this table:
they belong to the mod runtime itself, not to a capability-gated game binding group, so any loaded
mod has them regardless of tier.

## Three ways to load a mod

### (a) Via the agent — `manage_mods load`

Ask the Programmer agent in chat to create a mod, or call the tool directly. The `manage_mods` tool
accepts these actions: `list`, `get_source`, `load`, `reload`, `unload`, `export`, `import`, `forget`,
`versions`, `revert`, `diagnostics`.

```json
{ "action": "load", "mod_id": "greeter", "code": "--[[@coreai\nid: greeter\n]]\nhooks_on(\"wave_started\", function(evt, payload) print(payload) end)" }
```

A mod loaded this way is **auto-persisted**: its source and manifest are written to the source store,
so it **survives a restart** (see "How persistence works"). The agent can never raise the capability
tier — the host fixes the tier when it registers the tool.

### (b) In C# — `LuaCsModRuntime.LoadMod`

```csharp
modRuntime.LoadMod(
    "greeter",
    luaSource,
    LuaCapabilities.Read | LuaCapabilities.LogicOverride);
// Drive game -> mod events:
modRuntime.EmitEvent("wave_started", "3");
// Observe mod -> game events:
modRuntime.ModEventEmitted += (mod, evt, payload) => { /* ... */ };
```

`LoadMod` auto-persists by default (the runtime is constructed with `autoPersistMods: true`). Pass a
restricted capability set; a mod can never widen the host tier.

### (c) Assign a `.lua` TextAsset to a demo controller

`.lua` files are first-class `TextAsset`s in CoreAI — the `LuaScriptedImporter` imports any `*.lua`
asset so you can author a mod with a real `.lua` extension (editor recognition, drag-and-drop) instead
of the old `.lua.txt` workaround. `myLuaAsset.text` returns the source.

Author `greeter.lua` under `Assets/`, then drag it onto a demo controller's `TextAsset` field; the
controller passes `asset.text` to `LoadMod` on start.

## How persistence works

When a host wires a **source store** (`ILuaModSourceStore`), every successful `LoadMod` / `ReloadMod`
auto-saves the mod's source plus its manifest, and `UnloadMod` marks the stored package dormant
(`Active = false`) without deleting the source. A file-backed store (`FileLuaModSourceStore`) lays the
package out as:

```
<persistentDataPath>/CoreAI/Mods/<id>/
    manifest.json   -- id, name, description, version, author, capabilities, active, entry
    main.lua        -- the mod source (entry-point file)
```

On startup the host calls `LuaCsModRuntime.RehydrateFromStore(hostGrant)`: every stored package whose
manifest is `Active` is re-loaded automatically, with its requested capabilities masked down to the
host grant (and stripped of `Full` unless the host explicitly allows it). So a mod you loaded once via
chat is back the next time you press Play — no manual reload.

If no source store is wired the runtime falls back to `NullLuaModSourceStore`: everything still works,
but mods live only in memory (exactly the pre-persistence behavior). `RehydrateFromStore` then
returns `0`.

Note: this is separate from per-mod **`store_set`/`store_get`** key/value data, which is persisted by
`FileLuaModStore` (`persistentDataPath/CoreAI/LuaMods`). The source store persists the *mod itself*;
the mod store persists the *mod's runtime variables*.

## Sharing a mod between players

Two ways:

1. **Export / import a bundle.** `manage_mods export` (or `LuaCsModRuntime.ExportMod(id)`) returns a
   single JSON bundle of the shape `{"manifest":{...},"source":"..."}`. Send that string to another
   player; they run `manage_mods import` with it (or `LuaCsModRuntime.ImportMod(bundleJson, hostGrant)`)
   and the mod loads on their machine. Use `manage_mods forget` (`LuaCsModRuntime.ForgetMod(id)`) to
   permanently delete a stored package.

   ```json
   { "action": "export", "mod_id": "greeter" }
   ```
   ```json
   { "action": "import", "bundle": "{\"manifest\":{\"id\":\"greeter\",...},\"source\":\"...\"}" }
   ```

2. **Copy the mod folder.** Because the file-backed store is just
   `persistentDataPath/CoreAI/Mods/<id>/{manifest.json,main.lua}`, you can copy that folder to another
   player's matching path; it rehydrates on their next start.

On import or rehydrate the requested capabilities are always intersected with the host grant, so a
shared mod can never auto-acquire a tier the receiving host does not allow.

## A Full-mode mod (and why Full is off by default)

Most mods never need `Full`. When you genuinely need reflection (`unity_*`) — for diagnostics or to
reach a component CoreAI does not expose through `coreai_world_*` — a Full mod looks like this:

```lua
--[[@coreai
id: light_dimmer
name: Light Dimmer
description: Halves the directional light intensity once on load (Full mode).
]]
local id = unity_find("Directional Light")
if id then
  local intensity = unity_get_member(id, "Light", "intensity")
  unity_set_member(id, "Light", "intensity", intensity * 0.5)
  print("light_dimmer: dimmed the sun")
end
```

**Security note — Full is OFF by default for persisted and shared mods.** A persisted, rehydrated,
imported, or copied mod can never silently escalate to `Full`: on rehydrate and import the requested
capabilities are masked to the host grant and then stripped of `Full` unless the host explicitly
passes `allowFull: true`. Full reflection is only granted when the host turns on **Enable Full Lua
Access** (or passes `caps | LuaCapabilities.Full` to `LoadMod` in the same session). This keeps a
shared bundle from arriving with reflection powers the receiving game never intended to grant.

## Next steps

- [LUA_GAME_API.md](LUA_GAME_API.md) — full binding reference, transactions, events, Full mode.
- [LUA_BEST_PRACTICES.md](LUA_BEST_PRACTICES.md) — do's and don'ts.
- [LUA_ACCESS_MODES.md](LUA_ACCESS_MODES.md) — capability tiers in depth.
- [LUA_SANDBOX_SECURITY.md](LUA_SANDBOX_SECURITY.md) — the sandbox boundary and host checklist.
