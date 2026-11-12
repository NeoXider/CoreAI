# Lua Modding core: the two ways to run Lua, changing a mod, the world API and scene queries

The CoreAI Lua reference for mods and one-off scripts. This core is what every task needs; before you
use a feature it names a section for, read that section, and read only the sections your task uses.

<!-- sections -->

## Execution models

- `execute_lua` tool: one-shot script. Runs once, returns Output/Error. Use for diagnostics
  and single actions. Nothing survives the call.
- `manage_mods` tool: persistent mods. `load` compiles and runs the source once (register your
  hooks there); handlers then run until `unload`. Mods auto-persist and reload on app restart.
  Actions: list, get_source, edit, load, reload, unload, export, import, forget, versions, revert,
  diagnostics, move, create_folder, rename_folder, move_folder, delete_folder. A hook that throws at runtime surfaces in `diagnostics`, not in the load call.
  A mod failing repeatedly is quarantined, never auto-unloaded: it stays in `list` with
  quarantined=true and its handlers/timers suspended; a successful `reload` or `edit` clears it.
- Changing an existing mod: read it with `get_source`. It comes in pages of whole lines; a page
  that is not the whole source has `next_from_line`: call again with `from_line` set to it
  (`line_count` limits a page). Then call `edit` with `old_text` copied exactly from `data`
  (whole lines, indentation included, enough of them to match once; `replace_all=true` changes
  every occurrence) and `new_text`. The mod reloads with the change; a refused or failed edit
  changes nothing and says what to send next. `reload` with the FULL source replaces the whole
  mod (never send it a fragment). Every load, reload and edit stores a revision: `versions` lists
  them, `revert` rolls back. `forget` = delete (unload + remove the persisted copy) - the panel's
  Forget button does the same.
- Mods live in folders. A mod's folder is the `category:` entry of its `@coreai` header, a `/`
  path (`category: Games/Arcade`); no category = top level. Prefer writing `category:` in the
  header you send. `load` with `folder` writes it for you (the game may also set a default
  folder for new AI mods). `move` (mod_id + folder) moves a loaded mod without restarting it;
  `create_folder`, `delete_folder` (empty folders only), `rename_folder` (folder + target = new
  name) and `move_folder` (folder + target = new parent; empty = top level) manage folders.
  `list` returns each mod's `folder` and a `folders` array; with `folder` only that branch.
  Folder names: letters, digits, spaces and _ - . ( ) & + ! ' , # (64 characters, 8 levels at
  most). A refused folder call starts with a code such as FOLDER_NOT_EMPTY, MOVE_INTO_ITSELF or
  INVALID_CHARACTER: fix that argument and retry. Moving changes only the `category:` line.
- In the `code` JSON string write newlines as standard \n escapes. Never double-escape (\\n
  reaches Lua as a literal backslash and the whole source collapses into one broken line).
- Sandbox: no io/os/load/debug (`require` loads `context: shared` modules: Rbx API, scripting).
  Instruction and time budgets apply; a handler burning >100 ms is reported as an error. Keep per-tick
  work small.
- Syntax: Luau is accepted and auto-downleveled to Lua 5.2 before compiling - `+=` (and the other
  compound assignments), `continue`, `` `str{}` `` string interpolation, if-then-else expressions,
  and type annotations/casts all work. Plain Lua 5.2 is unaffected.

## World API (WorldEdit tier - no Full mode needed)

World BUILDING (creating, moving, recoloring, destroying objects) is done Roblox-style:
`Instance.new('Part')`, `game`/`workspace`, `Vector3`/`CFrame`/`Color3`, and Part properties
(Position, Size, Color, Anchored, Parent = workspace). Call `read_skill('Rbx API')` for the
full reference before building anything visible - there is no other world-building surface.

## Scene queries (read-only, no Full mode needed)

- `coreai_world_find(pattern)` -> list of scene object names containing the pattern
  (case-insensitive substring match, at most 100 results).
- `coreai_world_pos(name)` -> `{x=,y=,z=}` table, or nil when the object does not exist.
- `coreai_world_exists(name)` -> bool.
  (availability depends on host wiring - if a query global is nil, fall back to unity_* under Full).
