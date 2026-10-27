# Luau downlevel repair receipt (PR-08 findings)

Status: implemented; root verification pending.

Scope: PR-08-001 and PR-08-002 only. Changed files: Assets/CoreAIMods/Runtime/LuauDownlevel/DownlevelResult.cs, Assets/CoreAIMods/Runtime/LuauDownlevel/LuauRewriteParser.cs, Assets/CoreAIMods/Tests/EditMode/LuauDownlevelerEditModeTests.cs, Assets/CoreAIMods/Tests/EditMode/LuauDownlevelerExecutionEditModeTests.cs, this document.

## Decisions
- PR-08-001: reject configured trusted iterator resolver name exactly `_ENV` as a reserved lexical name. Added `_ENV` to invalid-name regression. Returns ordinary Error + original source; no throw.
- PR-08-002: EnsureFloor no longer hoists `math.floor` at chunk entry. It captures the chunk lexical `_ENV` once into a fresh generated local, then defines a local lazy getter returning `capturedEnv.math.floor`. Emitted operations use `getter()(lhs / rhs)`, so the lookup happens at operation evaluation and the function is obtained before operand effects. Unused operations never evaluate the getter. Local math shadowing remains ignored because capture occurs before author locals.

## Changed files and receipts
- DownlevelResult.cs: `_ENV` added to reserved resolver-name cases.
- LuauRewriteParser.cs: EnsureFloor and all floor-division emission sites updated.
- LuauDownlevelerEditModeTests.cs: `_ENV` added to `InvalidTrustedResolverName_IsRefusedWithoutThrowing`.
- LuauDownlevelerExecutionEditModeTests.cs: added unused-operation, missing-math operation error, and evaluation-order regression tests.

## Verification
- Static checks only performed by engineer. Root should run:
  - dotnet build CoreAI.Tests.csproj (or relevant)
  - Unity/EditMode filters: `LuauDownlevelerEditModeTests`, `LuauDownlevelerExecutionEditModeTests`
  - portable Lua filter `FullyQualifiedName~LuauDownleveler`
- No execution claim made.