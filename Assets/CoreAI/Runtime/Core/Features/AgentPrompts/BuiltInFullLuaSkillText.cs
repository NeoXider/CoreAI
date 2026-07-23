namespace CoreAI.Ai
{
    /// <summary>
    /// Built-in "Full Lua" skill: the model-facing reference for the Full-tier reflection surface
    /// (<c>unity_*</c> scene APIs), loaded on demand via <c>read_skill</c>. Mirrors
    /// <see cref="BuiltInLuaModdingSkillText"/>: the Programmer prompt only names the skill as a
    /// rarely-needed backup; this text carries the whole reference. An optional
    /// <c>Resources/AgentSkills/FullLua</c> TextAsset overrides <see cref="Instructions"/>.
    /// </summary>
    public static class BuiltInFullLuaSkillText
    {
        /// <summary>Catalog name (what the model passes to <c>read_skill</c>).</summary>
        public const string SkillName = "Full Lua";

        /// <summary>One-line catalog description shown before the model decides to read the skill.</summary>
        public const string SkillDescription =
            "Full-tier reflection over arbitrary scene objects and components: unity_list_objects/" +
            "unity_find_*/unity_describe_object, transform setters, unity_get_member/unity_set_member/" +
            "unity_call. A rarely-needed backup for tasks the Rbx surface and Lua mechanics cannot " +
            "cover; requires Full mode to be enabled. Read only when a task truly needs raw reflection.";

        /// <summary>Full instructions returned by <c>read_skill("Full Lua")</c>.</summary>
        public const string Instructions = @"# CoreAI Full Lua Reference (Full-tier reflection)

Full mode exposes raw reflection over every GameObject and component in the scene. It is a
BACKUP surface: prefer the Rbx API (read_skill('Rbx API')) for building and the standard mod
mechanics (hooks/timers/store/input) for behaviour. Use Full only when a task truly needs to
inspect or mutate arbitrary existing scene objects. These APIs are available only when the host
enabled Full mode; otherwise every unity_* call is absent.

## Workflow

First run a small diagnostic execute_lua script: inspect the scene, return a compact string or
JSON through return/report, then read Success/Output/Error BEFORE changing anything or loading a
persistent mod. Do not hard-code one recipe for visual requests: inspect objects/components
first, then use the smallest real API that matches the scene.

## Discovery

- `unity_list_objects(max)` -> root objects with ids.
- `unity_find_all(pattern, max)` -> objects whose name matches the pattern.
- `unity_find_by_tag(tag, max)`; `unity_find_by_component(type, max)`.
- `unity_describe_object(id)` -> name, tag, active state, components.
- `unity_get_children(id)`; `unity_list_components(id)`.

## Transforms

- `unity_get_transform(id)` -> position/rotation/scale.
- `unity_set_position(id, x, y, z)`; `unity_set_rotation_euler(id, x, y, z)`;
  `unity_set_scale(id, x, y, z)`.
- `unity_parent(childId, parentIdOr0, worldPositionStays)` — parentId 0 unparents.

## Members & methods

- `unity_get_member(id, component, member)` / `unity_set_member(id, component, member, value)`.
- `unity_call(id, component, method, args)` — invoke a component method with an args table.

## Example diagnostic

```lua
local objs = unity_find_all('Enemy', 10)
local names = {}
for i, o in ipairs(objs) do names[i] = o.name .. ':' .. o.id end
report(table.concat(names, ','))
```
Read the Output, pick the exact ids, and only then apply the smallest mutation
(unity_set_position / unity_set_member) that fulfils the request.";
    }
}
