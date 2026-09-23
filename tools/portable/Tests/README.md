# Portable Core regression tests

Run the existing engine-free CoreAI tests against the real `netstandard2.1` Core library and the engine-free Mods assemblies (`CoreAI.RbxApi.Datatypes`, `CoreAI.RbxApi.Instances`, `CoreAI.LuauDownlevel`) using .NET 8 or newer. No Unity assemblies, editor symbols, simulated Unity APIs, API key, or Unity license are needed.

From the repository root:

```powershell
dotnet test tools/portable/Tests/CoreAI.Portable.Tests.csproj -c Release --collect:"XPlat Code Coverage" --settings tools/portable/Tests/coverage.runsettings --results-directory artifacts/portable-tests
```

The project explicitly links the reusable fixtures from `Assets/CoreAI/Tests/EditMode`, `Assets/CoreAiUnity/Tests/EditMode` and `Assets/CoreAIMods/Tests/EditMode`; it does not copy or weaken their assertions. The setup locates the repository root for source-contract tests. When adding an engine-free regression fixture, add its source path to the project so both Unity and portable CI run it.

A Mods fixture qualifies when it compiles against NUnit, `CoreAI.Core` and the three engine-free Mods assemblies alone. That covers the Rbx datatypes, the instance tree and world ACL, the mod scheduler, replication and networking (`RbxApi/Datatypes`, `RbxApi/Instances`, `RbxApi/Acceptance/RungZeroAclEditModeTests.cs`, `RbxApi/Scheduling`, `RbxApi/Replication`, `RbxApi/Networking`), the Luau downleveler, and the Rbx shader keyword guard (it only reads files). A fixture that touches `UnityEngine` (including `Application.dataPath`), `Lua.dll`, the `CoreAI.Mods` assembly, the Lua bindings or VContainer stays Unity-only.

The test assembly is named `CoreAI.Mods.Tests` because `CoreAI.Core` and `CoreAI.RbxApi.Instances` both already grant that name `InternalsVisibleTo`, and the scheduler and replication fixtures use internal seams. Reusing an existing friend name keeps the runtime friend lists exactly as Unity ships them.

Coverage is written to `artifacts/portable-tests/<run-id>/coverage.cobertura.xml`. The denominator is the compiled **CoreAI.Core assembly only** (the Mods assemblies are tested here but not counted), with `COREAI_LLM` enabled and without `UNITY_EDITOR` or `UNITY_WEBGL`. It includes orchestration, actor scopes, memory contracts, tool policy, skills, HTTP/SSE parsing, retry, and timeout behavior. This measures exercised lines and branches, not a guarantee of correctness.

Unity host adapters, UI, the Lua VM and its Rbx bindings, the Unity world backing (`CoreAI.RbxApi.Unity`), MCP, editor-only HTTP test hooks, and WebGL/IL2CPP execution are outside this gate. Their fixtures stay in the real Unity suite. Browser/device builds still require separate validation. The portable CI gate fails on test failures and uploads coverage; it does not claim a coverage target it has not established.
