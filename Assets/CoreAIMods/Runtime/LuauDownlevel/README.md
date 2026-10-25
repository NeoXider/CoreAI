# CoreAI.LuauDownlevel (Domain)

Standalone Luau → Lua 5.2 source preprocessor built to `Docs/ARCHITECTURE_RULES.md`. Rewrites the
Luau-only constructs a typical Roblox gameplay script uses into forms the bundled Lua-CSharp
(Lua 5.2) VM parses. darklua's rule set is the reference spec for the supported subset. Source that
requires no edits retains the original string instance; generic-for loops are normalized in plain Lua too.

## Layer map

- **Domain (this assembly)** — `CoreAI.LuauDownlevel.asmdef`, `noEngineReferences: true`,
  `autoReferenced: false`, references: none. A pure text transform: no `UnityEngine`, no `Lua`
  VM. `LuauDownleveler.Process` is the only public entry point; it never throws (malformed input is
  returned verbatim with an Error diagnostic).
- **Consumers** — `CoreAI.Mods` references this assembly. It is wired into every runtime Lua compile
  path through `LuauSourceGate.ToLua52(source, chunkName)`, which runs `Process` before the Lua 5.2
  VM compiles the chunk: mod load/reload (`LuaCsModRuntime.BuildMod`), the one-shot `execute_lua`
  path (`LuaCsGameToolExecutor`), and the AI envelope path (`LuaCsAiEnvelopeProcessor`). Default-on
  with no flag. Generic-for loops use the configured iterator adapter even when already valid Lua 5.2; a
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
- `LuauDownlevelOptions` — immutable preprocessing limits and the optional trusted iterator seam.

## Configuration

`Process(source)` and `Process(source, chunkName)` use bounded defaults. Hosts can call
`Process(source, chunkName, options)` with `LuauDownlevelOptions`; null options select the same defaults.

| Option | Default | Contract |
|---|---:|---|
| `MaxInputCharacters` | 1,048,576 | UTF-16 characters, checked before lexing, including plain Lua. |
| `MaxOutputCharacters` | 4,194,304 | UTF-16 characters in the input, intermediate edited/rendered text and final output. |
| `MaxEdits` | 262,144 | Total recorded edit operations, including operations later rolled back. |
| `MaxWorkUnits` | 16,777,216 | Charged source/token characters, parser advances, trigger scans and edit/render processing. |
| `TrustedIteratorResolverName` | null | Optional simple ASCII Lua identifier of a host-provided iterator resolver, outside the reserved `__luau_t` prefix. |

Limits must be positive. Invalid options and exhausted limits return the original input string with
an Error; no partially rewritten output is emitted. Output sizes use checked-wide accounting before
render allocation. Work units are a deterministic preprocessing admission limit, separate from VM
instruction/time guards; they are not a wall-clock or exact managed-heap measurement.

The trusted resolver is captured in a reserved temporary at chunk entry and receives each generic-for
iterator expression list once. It must preserve ordinary iterator/state/control triples (and other
multiple arguments), normalize one direct table or supported userdata through its actual `__iter`
metamethod when present, invoke that metamethod once and return its results, or supply normal table /
callable iteration otherwise. A VM adapter owns that lookup and execution under the host's normal
step/time/capability guards. It must not return a protected metatable or widen general metatable access.
The host must install the named callable before execution; user locals with the same name do not
replace the captured function. This Domain assembly never depends on a VM implementation.

## Invariants

- **Never throws**: every failure path returns the original source plus an Error diagnostic.
- **Bounded preprocessing**: input, intermediate/final output, edit count and charged work are limited
  per call, before token-text/render allocation or edit insertion can exceed their configured bound.
- **Bounded recursion**: the lexer and the parser recurse once per nesting level, so both stop at
  `LuauDownleveler.MaxNestingDepth` (200, the bundled Lua 5.2 parser's own syntax-level limit, counted
  the same way: statements and subexpressions, plus function-type arrows and backtick strings nested
  inside interpolations). Deeper source gets an Error diagnostic. Without the bound, about 2,000 nested
  parentheses (or 1,000 nested if-expressions) would overflow a 1 MB .NET stack, which kills the
  process before any catch runs.
- **Byte-identical passthrough**: when nothing is rewritten (`Changed == false`) the original string
  instance is returned. The `NeedsDownlevel` trigger scan may false-positive (e.g. the generic-list
  heuristic on `a < b, c > d`); a false positive only costs a no-op parse that emits zero edits.
- **Line preservation**: rewrites keep the line count except a `\z` escape that swallows real
  newlines (emits a Warning). Diagnostics always use positions in the original source.
- **Temp names**: generated temporaries use the `__luau_t<N>` prefix and assume user code never
  declares such an identifier.
- **Primitive hygiene**: generated floor-division and interpolation calls capture `math.floor` and
  `tostring` at chunk entry, before author locals or assignments can shadow those primitives.
- **Repeat continuation**: the original until condition remains at its original lexical site and
  executes once per completed/continued iteration. Continue jumps out of nested blocks to a reserved
  label. A suffix containing later direct body locals is scoped before that label; valid Luau forbids
  an until condition from referring to a local skipped by continue. Those invalid programs are outside
  the supported contract; the transform is not a full Luau semantic validator.

## Recorded deviations / notes

- **Accepted darklua-style tradeoff**: if-expressions lower to an inline closure. Multi-return in a
  branch is truncated to one value (branches are parenthesized — `return (EXPR)`), matching Luau.
- **Unsupported**: a top-level `...` (varargs) inside an if-expression branch cannot be lowered into
  the generated closure; that construct is passed through unchanged with a Warning diagnostic.
- **Standalone protected metatables**: without `TrustedIteratorResolverName`, generalized iteration
  uses public `getmetatable`. It supports direct ordinary tables and unprotected `__iter` metatables,
  but cannot faithfully resolve protected metatables (including table-valued protection/decoys).
  Hosts accepting those values must provide a trusted resolver. Documentation of this standalone
  limitation does not imply protected-metatable parity for the fallback adapter.
- **Unicode escapes**: quoted and backtick literals accept 1–16 hexadecimal digits with a value in
  `0..0x10FFFF`, using the existing Luau-style UTF-8 byte encoding, including surrogate code units.
  Values that would overflow or exceed that range, empty/malformed escapes and missing braces are
  refused in original source coordinates. This is explicit input hardening; upstream Luau versions
  that wrap an oversized accumulator do not define this module's accepted overflow behavior.
- Raw multiline backtick text remains unsupported; escaped newline/whitespace handling does not
  claim full Luau string or language coverage.
