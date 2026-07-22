# CoreAI.LuauDownlevel (Domain)

Standalone Luau → Lua 5.2 source preprocessor built to `Docs/ARCHITECTURE_RULES.md`. Rewrites the
Luau-only constructs a typical Roblox gameplay script uses into forms the bundled Lua-CSharp
(Lua 5.2) VM parses. darklua's rule set is the reference spec; plain Lua passes through untouched.

## Layer map

- **Domain (this assembly)** — `CoreAI.LuauDownlevel.asmdef`, `noEngineReferences: true`,
  `autoReferenced: false`, references: none. A pure text transform: no `UnityEngine`, no `Lua`
  VM. `LuauDownleveler.Process` is the only public entry point; it never throws (malformed input is
  returned verbatim with an Error diagnostic).
- **Consumers** — `CoreAI.Mods` references this assembly. It is wired into every runtime Lua compile
  path through `LuauSourceGate.ToLua52(source, chunkName)`, which runs `Process` before the Lua 5.2
  VM compiles the chunk: mod load/reload (`LuaCsModRuntime.BuildMod`), the one-shot `execute_lua`
  path (`LuaCsGameToolExecutor`), and the AI envelope path (`LuaCsAiEnvelopeProcessor`). Default-on
  with no flag — valid Lua 5.2 is byte-identical passthrough, so plain scripts are unaffected; a
  downlevel Error is surfaced as the load/exec failure (never a silent raw fallback). Tests reference
  it directly.

## Seams / structure

- `LuauLexer` — Luau-aware tokenizer (strings, long strings/comments, backtick interpolation, Luau
  number literals, the Luau operator set). Computes per-token rewrites for Luau-only number literals
  (`NumberRewrite`) and string escapes (`StringRewrite`).
- `LuauRewriteParser` — recursive-descent parser that records source **edits** instead of building a
  tree. Deletions re-emit the newlines they cover so line numbers survive.
- `LuauDownleveler` — orchestrates lex → cheap `NeedsDownlevel` trigger scan → parse → apply edits.
- `DownlevelResult` / `DownlevelDiagnostic` — the public, engine-free result surface.

## Invariants

- **Never throws**: every failure path returns the original source plus an Error diagnostic.
- **Byte-identical passthrough**: when nothing is rewritten (`Changed == false`) the original string
  instance is returned. The `NeedsDownlevel` trigger scan may false-positive (e.g. the generic-list
  heuristic on `a < b, c > d`); a false positive only costs a no-op parse that emits zero edits.
- **Line preservation**: rewrites keep the line count, except a multi-line `repeat`-`until` condition
  duplicated for `continue` (emits a Warning) and a `\z` escape that swallows a real newline.
- **Temp names**: generated temporaries use the `__luau_t<N>` prefix and assume user code never
  declares such an identifier.

## Recorded deviations / notes

- **Accepted darklua-style tradeoff**: if-expressions lower to an inline closure. Multi-return in a
  branch is truncated to one value (branches are parenthesized — `return (EXPR)`), matching Luau.
- **Unsupported**: a top-level `...` (varargs) inside an if-expression branch cannot be lowered into
  the generated closure; that construct is passed through unchanged with a Warning diagnostic.
