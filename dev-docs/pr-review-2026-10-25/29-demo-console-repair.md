# FullAccess demo console startup repair

Status: root compatibility repair reviewed; isolated real-component regression authored. Compilation and execution are unrun by this worker and remain root-owned.

## Goal and scope

The goal is to let the shipped console input construct and initialize with the installed TMP implementation while preserving its existing command handling. The FullAccess scene smoke must remain meaningful. No CoreAI runtime dependency on the vendor console, broad private-reflection replacement or fabricated platform proof is required.

This worker may write only `Assets/CoreAiUnity/Tests/PlayMode/FastNoLlm/FullAccessDemoScenePlayModeTests.cs` and this report for the console task. `CommandInputField.cs`, installed package sources, scenes, prefabs and assembly definitions are read-only. Root owns the runtime implementation, tests, manual scene checks and Git.

Reuse decision: retain the actual FullAccess fixture and use the real installed `CommandInputField`, `DebugLogManager` and `TMP_InputField`. Resolve vendor types at runtime because FastNoLlm already references TMP but deliberately has no vendor assembly reference.

## Source facts and root runtime review

- Root reported a prior FullAccess EditMode/PlayMode scene smoke failing with a constructor `NullReferenceException`. This worker did not rerun or independently certify that receipt.
- The old `CommandInputField` instance-field initializer dereferenced the result of `GetField("m_IsCompositionActive")` to bind its getter. This occurs during component construction, before `Awake`; the previous stub's wording about binding in `Awake` was inaccurate.
- Read the exact installed `Library/PackageCache/com.unity.ugui@23caec89ae27/package.json` and `Runtime/TMP/TMP_InputField.cs`. The local package version is `2.6.0`. That TMP source has no `m_IsCompositionActive` field; it still defines `m_IsTextComponentUpdateRequired`, which the console's remaining reflected setter uses.
- Installed TMP handles composition through its current `KeyPressed`, caret/composition length and selected-update paths. This local source read is compatibility context, not an IME/platform execution result.
- Root's actual diff stores the optional legacy `FieldInfo` without dereferencing it during construction. The old OSX suppression branch checks for a nonnull field before reading it, allowing the modern path when absent.
- The Return/KeypadEnter/Tab mapping and console `Append` override remain unchanged; ordinary keys still pass to `KeyPressed`. No broad keyboard behavior change is inferred from startup success.

## CHANGED

- Added `CommandInputField_ConstructsAndInitializesWithInstalledTmp` to the existing FullAccess PlayMode fixture. It locates actual vendor types across loaded assemblies, adds a real input component to an inactive GameObject and requires construction to succeed.
- The fixture supplies a real inactive manager and a real suggestions container, invokes the actual `Initialize` method, verifies that initialization hides suggestions and installs the character-validation callback, then enables the input, changes its text, invokes ordinary character validation and advances one frame.
- Keeping the manager inactive isolates the input from unrelated full-console scene wiring, singleton startup and log subscriptions. Suggestions are disabled through the installed manager's actual field so ordinary text changes do not need the full suggestions UI. All created GameObjects are owned by one root destroyed in `finally`.
- Vendor reflection is confined to this existing test file. No core or test assembly reference was added, and no source-string assertion, log suppression, failure skip or command execution was introduced.
- Retained `FullAccessDemo_LoadsWithFullLuaAndAnEmptyWorld` with the same scene load and behavior assertions. Existing optional scene absence behavior is unchanged. Two existing comment prefixes were aligned with the repository's WHY convention.

## VERIFY OUTPUT

Static only. Read the complete current vendor diff, affected input/manager lifecycle methods, installed TMP selected-update/construction paths and the complete fixture diff. A read-only body comparison against the current Git baseline, ignoring comments/indentation, reported:

```text
Preserved existing FullAccess scene behavior assertions
Explicit C# locals: no var declarations in either leased fixture
Present isolated console regression: CommandInputField_ConstructsAndInitializesWithInstalledTmp
Source review only; no fixture compilation or execution performed
```

Also read `CoreAI.Tests.PlayMode.FastNoLlm.asmdef`: it already references `Unity.TextMeshPro` and omits `IngameDebugConsole.Runtime`. Runtime reflection preserves that compile-time boundary. Local package source inspection confirmed the removed legacy flag and retained text-update field; it did not execute either component.

## NOT DONE

- No compilation, PlayMode/EditMode fixture, Unity import/MCP, provider, network or extra agent was run by this worker. Root must run `FullAccessDemoScenePlayModeTests`, inspect unexpected logs and repeat the real demo smoke.
- Root's planned manual PlayMode pass over all 17 scenes and agent/mod flows is not performed or certified here.
- Real keyboard routing, Return/Tab command submission, focus/autocomplete, composition/IME, macOS/iOS, Mono/IL2CPP, WebGL, device, visual and performance behavior remain unrun. A programmatic text/validation callback is not hardware-input proof.
- No runtime, package-cache, assembly-definition, `.meta`, asset, scene, prefab, settings, shared tracker, changelog or Git state was mutated by this worker.

## CONCERNS

- The isolated regression requires the installed vendor types present in this repository; missing types fail explicitly. It does not silently convert an unavailable or broken constructor into a pass.
- It covers construction, initialization and one ordinary update with minimal UI wiring. The preserved scene test remains necessary for the full manager/prefab composition. Root execution must establish both paths are clean.
- The remaining private TMP text-update setter still depends on a field confirmed in this installed package. Compatibility with an arbitrary future TMP version is not claimed.

## QUESTIONS

None. Root owns execution and the remaining manual/platform gates.
