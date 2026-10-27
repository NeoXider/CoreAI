# Moddable Units gameplay binding seam

Status: implementation in progress, staging only. The goal is to forward explicit host gameplay bindings into persistent/rehydrated mods, replacement worlds and execute_lua through existing LuaCsModStackOptions.AdditionalGameplayBindings. No framework reference to demo types, global singleton, Find fallback, capability bypass or Editor-only primary path is authorized. Root owns scene provider wiring, live integration and executable acceptance.

Only scoped composition/demo/test/doc candidates and baseline metadata are writable under artifacts/moddable-units-20261026/staging. No live code, scene/prefab/meta, Git, Unity/MCP, builds/tests, model/provider call or extra agent is permitted. Preserve original RegisterCoreAiMods ABI and grant semantics; handle pre-Start rehydrate with lazy bindings and non-destructive Start.
