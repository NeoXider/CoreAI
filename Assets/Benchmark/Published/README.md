# Published benchmark castles

Curated G6 castle assets live in `<date>/<target-label>/`. Each target folder contains
its prefabs and a `Materials/` palette; runtime-generated texture assets live below
`Materials/Textures/`. Different target labels retain separate folders even when they
use the same model.

`Assets/Benchmark/` outside `Published/` is ignored local scratch space. Publish a
finished run with `GameCreationBenchmarkPrefabPublisher.PublishCastlePrefabs(runId,
dateFolder)` or the documented benchmark CLI. Publication moves assets with their
`.meta` files to preserve GUIDs, updates report links, and retains a recoverable move
journal in the run state. Repeating the same publication completes any interrupted
moves or reports that it is already complete.

Keep this README and both folder `.meta` files tracked so a fresh clone always has a
valid published asset root. Commit chosen published assets and all their `.meta`
files together after reviewing them.
