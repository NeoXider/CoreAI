# Text hygiene candidates for the 7.55.0 demo commit

Prepared external candidate copies for all 48 paths named by the frozen whitespace log. Repository assets were read only. The sole repository write from this task is this English completion note, explicitly requested by the lead.

- Candidate root: `C:/AI/work/coreai-completion-20261027/text-hygiene-candidates/`.
- Manifest: `C:/AI/work/coreai-completion-20261027/text-hygiene-candidates/manifest.json`.
- Manifest SHA-256: `c43e23a6a61e9d8aacf1d9b6047852b12a53869aaec6e9af9b4a3757ae6b72f6`.
- Frozen inventory: 415 paths; all 48 candidate paths belong to it. Diagnostics: 2,215 across those 48 paths.
- Candidates: 35 Unity metadata files, four C# line-ending files and nine EOF-only files (seven Lua examples, one C# interface and one developer document).

## Changes and evidence

The metadata edits remove the trailing spaces on exactly 105 empty `userData`, `assetBundleName` and `assetBundleVariant` fields. Every GUID and every other line is preserved.

The four code-editing C# files contain duplicated CR bytes before LF; `CodeEditorTextUtility.cs` also has CR-only line breaks around `ParseTracebackLine`. Their candidates canonicalize runs of CR before LF to one LF and replace isolated CR with LF. Exactly three whitespace-only code lines also lose their spaces: normalized `CodeEditorTextUtility.cs:568` (12 spaces), `CodeLineSelection.cs:12` (8 spaces), and `PythonSyntaxHighlighter.cs:113` (12 spaces). These precise removals were authorized by the lead after inspection.

The byte scanner confirmed that every affected CR is outside character/string literals. There are no verbatim or raw string literals, no physical literal newlines and no `#line` directives in these four files. The scanned token sequence, complete literal sequence, Unicode bytes and string constants are identical before and after. Per-file baseline/candidate hashes and token/literal sequence hashes are recorded in the manifest.

No raw NUL replacement was needed. Both current worktree and exact staged export of `CodeEditorTextUtility.cs` contain zero NUL bytes; all four sentinel character literals already spell `\0`. The current worktree SHA-256 is `025eacbafb35674d298968ab5145f18f02e8a84c203c48245620f510e34f4c2d`. The exact staged-export SHA-256 is `4e2f62d113e4a5ecbf9853995a91ab645e823628a3cfdaa9907b94eecc4fea45`. Replacing each worktree CRLF with LF exactly reproduces the export; both produce the same candidate after the documented blank-line cleanup. The manifest records this mapping so a staged-versus-worktree baseline is not mistaken for a semantic source edit.

The nine EOF candidates remove only empty trailing line-ending bytes and keep one final LF. Lua headers are terminated long comments; all Lua strings and comments are closed before the removed suffix. The C# interface ends in closed code. The Markdown document ends in ordinary prose with closed fences. All bytes before the final newline suffix remain identical.

## Verification and limits

- All 48 candidates decode as strict UTF-8, contain only LF line endings, contain no NUL bytes and have zero trailing horizontal-whitespace lines or extra EOF blank lines.
- All 35 GUIDs are preserved; each metadata file has exactly the three permitted empty-field edits.
- C# token/literal equality passes for five files (312 literal tokens); Lua token/literal equality passes for seven files (76 literal tokens).
- Source bytes were re-read after preparation and all 48 current-worktree hashes remain unchanged.
- No Git commands, index changes, repository asset writes, Unity calls, test runs or process controls were performed by this worker.
- The existing native gate receipt belongs to the source snapshot before these equivalent formatting candidates. It must not be described as a run against promoted candidate hashes. The lead owns promotion after the active Unity runs, `git diff --check`, affected Core and actual Lua checks, file guards, and any targeted editor utility tests required by the final source delta.

## Acceptance handoff

Before copying any candidate, compare the target file with its `baseline_sha256` in `manifest.json`; reject a mismatch and investigate rather than overwrite concurrent work. Promote only the 48 listed relative paths. The lead owns staging, final review, commit and publication. No compiler/runtime claim is made by the lexical audit alone.

CHANGED: External candidate copies, per-file hash/proof manifest and this completion note.

VERIFY OUTPUT: 48/48 candidate byte checks passed; 35/35 GUID checks passed; five C# and seven Lua token/literal comparisons passed; 48/48 worktree baselines remained unchanged.

NOT DONE: Promotion, Git whitespace verification, compilation, native tests after promotion and Git publication.

CONCERNS: The raw staged export and worktree use different newline bytes; the explicit mapping and both hashes are included above. Future edits require a fresh baseline check.

QUESTIONS: None.
