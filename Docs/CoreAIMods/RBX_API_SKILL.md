# Rbx API skill

`Rbx API` is an on-demand agent skill: a compact, model-facing reference for the
Roblox-compatible (Rbx) Lua surface that the in-game Programmer agent can pull into context when
it needs to write Roblox-style scripts (`Instance.new`, `game`/`workspace`, `Vector3`/`CFrame`/
`Color3`, Part properties, attributes, tags).

## What a skill is

Skills keep the Programmer system prompt small. The prompt only *names* the skill; the full
reference is loaded lazily. The agent calls the meta-tool:

```
read_skill('Rbx API')
```

and receives the whole reference back as text. The classic Lua surface is a sibling skill,
`read_skill('Lua Modding')`; the Rbx skill covers only the Roblox-style API and assumes the
classic one for timers/persistence/events.

## How it is wired

Same pattern as the Lua Modding skill:

- **Canonical text** — `Assets/CoreAiUnity/Resources/AgentSkills/RbxApi.txt`. Editor/Resources
  hosts load this override.
- **Built-in fallback** — `BuiltInRbxApiSkillText` in
  `Assets/CoreAI/Runtime/Core/Features/AgentPrompts/BuiltInRbxApiSkillText.cs`. Code-only hosts
  (no Resources) fall back to this constant. The two are byte-identical, pinned by an EditMode
  test (`LuaModdingSkillEditModeTests`).
- **Registration** — `CoreAiModsInstaller.RegisterCoreAiMods` adds the skill to the built-in
  Programmer role (`AddSkillForRole`), loading the Resources override if present and the built-in
  constant otherwise. `read_skill` resolves the skill by its name, `"Rbx API"`.

The Rbx Lua bindings themselves live in `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRoblox*`
(namespaces `CoreAI.Mods.Rbx.*`, assemblies `CoreAI.RbxApi.*`) and are enabled through
`LuaCsModStackOptions.RobloxApi`, which the same installer wires by default.

## What is NOT implemented yet

The skill documents only the surface that actually runs today. The following raise a loud
`NOT_IMPLEMENTED` error and are intentionally excluded from the reference: the `task` scheduler
(`task.wait`/`spawn`/`defer`/`delay`/`cancel`), signal connections
(`instance.ChildAdded:Connect`/`:Once`/`:Wait`), yielding `WaitForChild`, `Model:PivotTo`/
`GetPivot`, `Instance.fromExisting`, the BasePart `Shape`/`Material`/`Orientation`/`Rotation`
properties, and every enum outside `Material`/`PartType`/`NormalId`/`Axis`/`RotationOrder`.
Luau-only syntax (`+=`, `continue`, backtick string interpolation, type annotations) is also not
accepted — scripts must be plain Lua 5.2.

For the full picture of what has landed and what is planned, see
[`ROBLOX_API_ROADMAP.md`](ROBLOX_API_ROADMAP.md).
