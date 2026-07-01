# Optional modules: MoonSharp (Lua) & LLMUnity

CoreAI has two **optional** packages. Each is gated by a version-define that Unity sets
automatically when the package is present, so installing/removing the package is all it
takes to turn the feature on or off at compile time.

| Module | Package | Auto-define (when installed) | Extra manual switch |
|---|---|---|---|
| Lua (MoonSharp) | `org.moonsharp.moonsharp` | `COREAI_HAS_MOONSHARP` | `COREAI_NO_LUA` (soft-disable, keeps package) |
| Local LLM (LLMUnity) | `ai.undream.llm` | `COREAI_HAS_LLMUNITY` | — |

Runtime code guards on `#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA` (Lua) and
`#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL` (local LLM). Demo and core assemblies never use
these packages' types outside those guards.

### About the asmdef references

`CoreAI.Core.asmdef` and `CoreAI.Source.asmdef` do list `MoonSharp.Interpreter` (and
`CoreAI.Source.asmdef` also lists `undream.llmunity.Runtime`) in their `references`. Those
references are **name-based and optional in effect**: when the package is absent Unity simply
cannot resolve the referenced assembly and drops it, and because every use of those types is
behind the `#if` guards above, the assemblies still compile. This is verified in CI for
MoonSharp — see below.

> **Not overclaiming:** the "MoonSharp can be absent" path is proven by CI (the `no-lua` job
> removes `org.moonsharp.moonsharp` from `Packages/manifest.json` entirely and the suite stays
> green). For LLMUnity, CI currently only exercises `COREAI_NO_LLM` (which compiles out the LLM
> *layer* via source guards) — it does **not** remove the `ai.undream.llm` package. So removal
> of the LLMUnity package is expected to work by the same guard mechanism but is **not** covered
> by an automated job today.

## Editor tool

**`CoreAI ▸ Setup ▸ Modules`** (`CoreAIModuleManager`):

- **MoonSharp (Lua) ▸ Enable + Update to latest** — installs the package if missing and
  re-resolves it to the latest commit of its branch (`Client.Add` with the canonical git URL),
  and clears `COREAI_NO_LUA`.
- **MoonSharp (Lua) ▸ Disable Lua (keep package)** — adds `COREAI_NO_LUA`; Lua compiles out
  but the package stays installed. Reversible from the same menu.
- **MoonSharp (Lua) ▸ Remove package** — removes `org.moonsharp.moonsharp` entirely
  (`COREAI_HAS_MOONSHARP` unsets).
- **LLMUnity ▸ Enable + Update to latest** / **Remove package** — same for `ai.undream.llm`.
- **Report module status** — shows the installed version/source of each package, whether
  `COREAI_NO_LUA` is set, and the effective enabled/disabled state.

"Enable + Update to latest" uses `UnityEditor.PackageManager.Client.Add(gitUrl)`, which both
installs a missing package and bumps an installed one to the branch tip. Package operations
resolve asynchronously; the tool polls UPM on the editor loop and recompiles when done.

> To **add all** CoreAI git dependencies at once (VContainer, MessagePipe, UniTask, MoonSharp,
> LLMUnity) use **`CoreAI ▸ Setup ▸ Install Git Dependencies`** (`CoreAIDependencyInstaller`),
> which adds any missing manifest entries without changing pinned versions.

## CI parity

The `no-lua` CI matrix (`.github/workflows/ci.yml`) reproduces the disabled state by removing
`org.moonsharp.moonsharp` from `Packages/manifest.json` **and** defining `COREAI_NO_LUA`, then
runs the full EditMode/PlayMode suite to keep the MoonSharp-absent build green.

## Notes

- These are **git** packages, so "latest" means the tip of the referenced branch, not a
  semver release. Pin a tag/commit in `Packages/manifest.json` if you need reproducibility.
- Removing a package triggers a domain reload; the feature's types disappear and any scene
  components that used them log a one-time "module unavailable" warning and disable themselves.
