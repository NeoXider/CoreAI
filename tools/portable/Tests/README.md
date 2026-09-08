# Portable Core regression tests

Run the existing engine-free CoreAI tests against the real `netstandard2.1` Core library using .NET 8 or newer. No Unity assemblies, editor symbols, simulated Unity APIs, API key, or Unity license are needed.

From the repository root:

```powershell
dotnet test tools/portable/Tests/CoreAI.Portable.Tests.csproj -c Release --collect:"XPlat Code Coverage" --settings tools/portable/Tests/coverage.runsettings --results-directory artifacts/portable-tests
```

The project explicitly links the reusable fixtures from `Assets/CoreAI/Tests/EditMode` and `Assets/CoreAiUnity/Tests/EditMode`; it does not copy or weaken their assertions. The setup locates the repository root for source-contract tests. When adding an engine-free regression fixture, add its source path to the project so both Unity and portable CI run it.

Coverage is written to `artifacts/portable-tests/<run-id>/coverage.cobertura.xml`. The denominator is the compiled **CoreAI.Core assembly only**, with `COREAI_LLM` enabled and without `UNITY_EDITOR` or `UNITY_WEBGL`. It includes orchestration, actor scopes, memory contracts, tool policy, skills, HTTP/SSE parsing, retry, and timeout behavior. This measures exercised lines and branches, not a guarantee of correctness.

Unity host adapters, UI, Mods/Lua, MCP, editor-only HTTP test hooks, and WebGL/IL2CPP execution are outside this gate. Their fixtures stay in the real Unity suite. Browser/device builds still require separate validation. The portable CI gate fails on test failures and uploads coverage; it does not claim a coverage target it has not established.
