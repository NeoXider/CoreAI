# MVP2 G10 measurement harness

The harness resolves `IAiOrchestrationService`, `ILuaModRuntime`, and `LuaCsModStack` from the shipped
`RegisterCorePortable` and `RegisterCoreAiMods` composition. It never constructs the orchestrator.
It runs the 20-actor workload once for staggered chat and once for synchronized bursts.

Run the manifest-duration scripted mode (about three minutes plus build/drain time):

```powershell
dotnet test tools/G10Harness.Tests/G10Harness.Tests.csproj -- `
  --logger "trx;LogFileName=g10-tests.trx" `
  --results-directory Temp/G10/TestResults

dotnet run --project tools/G10Harness/G10Harness.csproj -- `
  --config tools/G10Harness/g10.stub.json `
  --output Temp/G10/g10-stub.json `
  --discovered-tests <observed total> `
  --skipped-tests <observed skipped> `
  --discovery-source Temp/G10/TestResults/g10-tests.trx `
  --quiet
```

Discovery values must come from the named test evidence; the CLI never invents them. The fixed loop
count is 280. Production guard samples calibrate its maximum callback shape at **580 guarded
instructions** (`576` for timer callbacks and `580` for event callbacks), below the manifest's
measured total capacity of 589 instructions per 4 ms frame. A run fails calibration if any observed
resume exceeds 589 or the per-resume samples do not reconcile with the production aggregate counter.

Event isolation is observed independently of `IRbxRuntimeObservabilitySink`: each emit carries a unique
payload and `bench_actor.lua` writes it to the external mod store. The report requires exactly one
matching subscriber write and zero writes by the other 19 actors for every emit.

`--quick` shortens warm-up and measurement and is only a harness smoke check. Its report sets
`manifestWorkload` to `false` and cannot pass G10.

For LM Studio, copy `g10.real-provider.example.json`, then supply the actual endpoint, model id,
context cap, output cap, backend concurrency, orchestrator concurrency, and timeout. Empty or null
values fail configuration; the harness does not substitute provider facts. Do not put secrets in a
tracked config file.

The scripted result reports model/caps/backend concurrency/provider-produced responses as
`not_measured` with `null` values. Only a real-provider run can produce those fields and the summative
G10 result, so a successful stub report retains gate status `not_measured` with no failures.
