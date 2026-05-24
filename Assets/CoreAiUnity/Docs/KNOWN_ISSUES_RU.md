# Known issues

This file tracks accepted warning debt and project-level issues that are not runtime regressions.

## CS8632 nullable warnings

Symptom: Unity/C# compiler reports CS8632: nullable reference annotations are used outside a nullable context.

Cause: some files use nullable annotations while the assembly or file does not enable nullable context.

Impact: warning debt only; not a runtime regression.

Recommended follow-up:

- Choose a nullable policy per asmdef or file.
- Either enable nullable context or remove nullable annotations from affected files.
- Do not mix this cleanup into unrelated feature changes.

## PathTracing render pipeline warning

Symptom: Unity warning references UnityEngine.PathTracing.Core.WorldRenderPipelineResources or UniversalRenderPipelineGlobalSettings.

Cause: Unity, URP, or PathTracing package settings mismatch in project render pipeline global settings.

Impact: editor/package warning; does not affect CoreAI runtime directly.

Recommended follow-up:

- Check Unity and URP package versions.
- Check project render pipeline global settings.
- Remove or reassign obsolete render pipeline resource references if Unity created stale settings.

## Warning handling policy

- New compile errors block merges.
- New warnings must not be hidden inside accepted warning debt.
- If a warning is accepted debt, document the reason, owner, and follow-up plan here.
