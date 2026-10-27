# Hub repair receipt (PR-09 findings)

Status: implemented; root verification pending.

Scope: PR-09-001, PR-09-002, PR-09-003. Changed files: Assets/CoreAIHub/Editor/CoreAiHubSetupMenu.cs, Assets/CoreAIHub/Runtime/CoreAiHubWindow.cs, Assets/CoreAIHub/Runtime/HubSettingsPage.cs, Assets/CoreAiUnity/Tests/EditMode/HubContractRegressionTests.cs, Assets/CoreAiUnity/Tests/EditMode/CoreAiHubWindowEscapeEditModeTests.cs, Assets/CoreAiUnity/Tests/EditMode/HubSettingsRegressionEditModeTests.cs, this document.

## Decisions
- PR-09-001: generated Hub prefab now also adds the optional WorldStateHubBinder when its assembly is present, mirroring the existing optional Mods binder. The binder registers WorldStateHubPage only when a world manager resolves.
- PR-09-002: HandleEscapeRequested returns whether Escape was actually handled; OnRootKeyDown consumes the event only in that case.
- PR-09-003: endpointGroup's ChangeEvent<bool> handler ignores Foldout presentation changes while still invalidating on real boolean descriptor inputs.

## Verification
- Root should run dotnet build for affected csproj and Unity EditMode filters HubContractRegressionTests, CoreAiHubWindowEscapeEditModeTests, HubSettingsRegressionEditModeTests.
- No execution claim made.
