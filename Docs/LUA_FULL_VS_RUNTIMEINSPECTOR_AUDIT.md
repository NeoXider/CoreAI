# CoreAI Full Lua vs RuntimeInspector Audit

## Question

Is CoreAI's Full-tier Lua reflection in `Assets/CoreAiUnity/Runtime/Source/Features/Lua/Infrastructure/CoreAiFullUnityLuaRuntimeBindings.cs` built on RuntimeInspector logic, or on its own `System.Reflection` path?

## Short Answer

CoreAI Full-tier Lua is standalone. It does not call `RuntimeInspector`, `RuntimeInspectorUtils`, `InspectorField`, or RuntimeInspector drawers. It directly uses `System.Reflection` (`BindingFlags`, `Type.GetMembers`, `FieldInfo.SetValue`, `PropertyInfo.SetValue`, `MethodInfo.Invoke`) plus small hand-written MoonSharp `DynValue` coercion.

RuntimeInspector also uses reflection, but it wraps it in its own inspector model: filtered serializable-member discovery, drawer selection per type, reference pickers, array/list resizing, nested-object editors, and UI-field-specific setters. The two systems currently duplicate the broad idea of reflection but do not share implementation.

## Evidence: Full Lua Is Standalone

- `CoreAiFullUnityLuaRuntimeBindings` imports `System.Reflection` directly and owns its caches for types, members, and settable members: `CoreAiFullUnityLuaRuntimeBindings.cs:2-6`, `:23-25`.
- It computes reflection flags itself: `MemberFlags()` returns instance/public plus optional non-public flags at `CoreAiFullUnityLuaRuntimeBindings.cs:51-55`.
- It registers Lua APIs directly, including `unity_list_members`, `unity_get_member`, `unity_set_member`, and `unity_call`: `CoreAiFullUnityLuaRuntimeBindings.cs:57-85`.
- It resolves fields/properties directly through `type.GetField()` / `type.GetProperty()`: `CoreAiFullUnityLuaRuntimeBindings.cs:696-727`.
- It writes values directly through `FieldInfo.SetValue` / `PropertyInfo.SetValue`: `CoreAiFullUnityLuaRuntimeBindings.cs:392-412`.
- It invokes methods through reflected parameter conversion and `MethodInfo.Invoke`: see method-call registration at `CoreAiFullUnityLuaRuntimeBindings.cs:415-452`.
- Full Lua is wired through CoreAI DI and capability gating, not RuntimeInspector: `WorldCommandsInstaller.cs:84-107` registers `CoreAiFullUnityLuaRuntimeBindings` and `AggregatingGameLuaRuntimeBindings.cs:88-90` exposes it only when `LuaCapabilities.Full` is granted.

## 1. Settable Member Enumeration

### Full Lua

Full Lua discovers settable members through `GetSettableMembers(Type)`:

- Uses `MemberFlags()` (`Instance | Public`, plus optional `NonPublic`) at `CoreAiFullUnityLuaRuntimeBindings.cs:51-55`.
- Calls `type.GetMembers(flags)` and filters each member with `IsSettableDiscoverableMember`: `CoreAiFullUnityLuaRuntimeBindings.cs:926-944`.
- Excludes `[Obsolete]` and `[HideInInspector]`: `CoreAiFullUnityLuaRuntimeBindings.cs:947-953`.
- Accepts any non-const, non-readonly field, without checking `[NonSerialized]`, `[SerializeField]`, Unity serializability, backing-field naming, or field type support: `CoreAiFullUnityLuaRuntimeBindings.cs:955-958`.
- Accepts any writable non-indexer property, without requiring a getter, serializable type, public getter, or non-override property: `CoreAiFullUnityLuaRuntimeBindings.cs:955-959`.
- Applies only CoreAI's host deny-list after discovery through `GetAllowedSettableMembers`: `CoreAiFullUnityLuaRuntimeBindings.cs:910-924`.

Result: Full Lua's list is "reflection writable" more than "Unity-inspector-editable". It can list many members RuntimeInspector intentionally hides, and it can list members that later fail at conversion or assignment.

### RuntimeInspector

RuntimeInspector discovers variables through a different pipeline:

- `RuntimeInspector.GetExposedVariablesForType()` calls `type.GetAllVariables()` and then wraps the result in `ExposedVariablesEnumerator`: `RuntimeInspector.cs:638-668`.
- Visibility is configurable separately for fields and properties (`None`, `SerializableOnly`, `All`), defaulting to `SerializableOnly`: `RuntimeInspector.cs:13`, `RuntimeInspector.cs:31-60`.
- `RuntimeInspectorUtils.GetAllVariables()` walks declared fields/properties per type, not one flat `GetMembers()` call: `RuntimeInspectorUtils.cs:592-624`.
- Properties must have getter and setter, must not be indexers, must be serializable, must not be `[Obsolete]`, `[NonSerialized]`, or `[HideInInspector]`, and override properties are skipped: `RuntimeInspectorUtils.cs:597-616`.
- Fields must not be const/readonly, must have serializable type, and must not be `[Obsolete]`, `[NonSerialized]`, or `[HideInInspector]`: `RuntimeInspectorUtils.cs:629-639`.
- RuntimeInspector's serializable-type filter covers Unity/common types, UnityEngine.Object references, arrays, lists, enum, primitives, and `[Serializable]` classes/structs: `RuntimeInspectorUtils.cs:26-48`, `RuntimeInspectorUtils.cs:741-779`.
- `ExposedVariablesEnumerator` then applies explicit hidden/exposed variable lists and the configured visibility policy: `ExposedVariablesEnumerator.cs:65-109`.

Result: RuntimeInspector enumerates "supported inspector variables", not merely writable reflection members. Its enumeration is safer and closer to Unity serialization/editor expectations.

## 2. Type Coercion

### Full Lua

Full Lua performs coercion in `ConvertArg(DynValue, Type)`:

- `nil` becomes default value for value types or `null` for reference types: `CoreAiFullUnityLuaRuntimeBindings.cs:795-800`.
- Handles `string`, `bool`, `int`, `float`, `double`: `CoreAiFullUnityLuaRuntimeBindings.cs:802-825`.
- Handles enums only from string values via case-insensitive `Enum.Parse`: `CoreAiFullUnityLuaRuntimeBindings.cs:827-830`.
- Handles `Vector2`, `Vector3`, `Vector4` from Lua tables with named `x/y/z/w` fields: `CoreAiFullUnityLuaRuntimeBindings.cs:832-857`.
- Handles `Color` from HTML string via `ColorUtility.TryParseHtmlString`, or Lua table `r/g/b/a`: `CoreAiFullUnityLuaRuntimeBindings.cs:859-880`.
- Handles `Quaternion` from Lua table as either `x/y/z/w` quaternion or `x/y/z` Euler angles: `CoreAiFullUnityLuaRuntimeBindings.cs:882-899`.
- Falls back to `Convert.ChangeType(value.ToObject(), targetType, CultureInfo.InvariantCulture)`: `CoreAiFullUnityLuaRuntimeBindings.cs:901`.
- Readback to Lua supports primitives, enum as string, `Vector2/3/4`, `Color`, and `Quaternion`; everything else becomes `value.ToString()`: `CoreAiFullUnityLuaRuntimeBindings.cs:748-770`.

Reference-type support is effectively limited. There is no ID-to-`UnityEngine.Object` resolver in `ConvertArg`; setting a `Material`, `Transform`, `GameObject`, `Texture`, custom component reference, array, list, `LayerMask`, `Rect`, `Bounds`, `Color32`, `Vector2Int`, `Vector3Int`, `RectInt`, `BoundsInt`, `AnimationCurve`, `Gradient`, or `[Serializable]` nested object is not supported except where `Convert.ChangeType` happens to work. For Unity object references, `Convert.ChangeType` is not a real resolver.

### RuntimeInspector

RuntimeInspector does not have one generic Lua-style coercion function. It selects a drawer for the member type and each drawer writes strongly typed values:

- Drawer selection is type-based: `CreateDrawerForType()` asks registered drawers whether they support the target type: `RuntimeInspector.cs:544-563`, `RuntimeInspector.cs:591-617`.
- The base `InspectorField.Value` setter calls the bound setter with the already-typed value: `InspectorField.cs:60-69`.
- Field/property binding calls `FieldInfo.SetValue` or `PropertyInfo.SetValue`, and value-type parents are written back after nested mutation: `InspectorField.cs:135-163`.
- Number handling has explicit handlers for signed/unsigned integer widths, `char`, `float`, `double`, and `decimal`: `NumberHandlers.cs:24-231`.
- Enum handling uses `Enum.GetNames()` / `Enum.GetValues()` and assigns the selected enum object directly: `EnumField.cs:57-89`, `EnumField.cs:109-112`.
- Color handling supports both `Color` and `Color32`: `ColorField.cs:30-57`.
- Object references use `Resources.FindObjectsOfTypeAll(BoundVariableType)` for picker candidates and assign `UnityEngine.Object` references directly: `ObjectReferenceField.cs:43-58`, `ObjectReferenceField.cs:77-95`.
- Drag/drop reference assignment can coerce between `Component` and `GameObject`, and can resolve a component type from a dropped component or GameObject: `RuntimeInspectorUtils.cs:343-377`.
- Arrays and `List<T>` can be resized, element drawers are bound, dropped references can append assignable objects, and new elements are templated/default-created: `ArrayField.cs:63-79`, `ArrayField.cs:118-204`, `ArrayField.cs:216-319`.

RuntimeInspector's "coercion" is mostly UI/editor-value construction rather than text/table parsing. It is more complete for Unity object references, arrays/lists, nested serializable objects, and Unity built-in serializable structs.

## 3. RuntimeInspector Handles That Full Lua Does Not

Concrete gaps in Full Lua compared with RuntimeInspector:

- Unity serialization filtering: RuntimeInspector excludes non-serializable member types, `[NonSerialized]`, hidden members, and override duplicate properties (`RuntimeInspectorUtils.cs:597-616`, `:629-639`, `:741-779`). Full Lua only excludes obsolete/hidden and mutability (`CoreAiFullUnityLuaRuntimeBindings.cs:947-959`).
- Configurable exposure policy: RuntimeInspector supports `None`, `SerializableOnly`, and `All` for fields/properties (`RuntimeInspector.cs:13`, `:31-60`) plus explicit hidden/exposed variable sets (`ExposedVariablesEnumerator.cs:65-109`). Full Lua has public vs optional non-public plus a deny-list policy (`CoreAiFullUnityLuaRuntimeBindings.cs:51-55`, `IFullLuaAccessBlacklistPolicy.cs:9-15`).
- Unity object references: RuntimeInspector can pick and assign object references from `Resources.FindObjectsOfTypeAll` (`ObjectReferenceField.cs:43-58`) and convert dragged GameObject/Component references (`RuntimeInspectorUtils.cs:343-377`). Full Lua does not convert instance IDs or names into `UnityEngine.Object` values in `ConvertArg`.
- Component/GameObject reference cross-assignment: RuntimeInspector can turn a `GameObject` into a requested `Component`, or a `Component` into a `GameObject` (`RuntimeInspectorUtils.cs:355-366`). Full Lua lacks this.
- Arrays and lists: RuntimeInspector edits `T[]` and `List<T>`, resizes them, creates default/template elements, and binds each element (`ArrayField.cs:63-79`, `:216-319`). Full Lua has no table-to-array/list path.
- Nested serializable objects: RuntimeInspector can instantiate `[Serializable]` classes/structs and `ScriptableObject` instances for nested editing (`RuntimeInspectorUtils.cs:809-823`). Full Lua has no nested-object table serializer.
- More Unity structs: RuntimeInspector's supported serializable set includes `Rect`, `Bounds`, `LayerMask`, `Color32`, `Matrix4x4`, `AnimationCurve`, `Gradient`, `RectOffset`, `GUIStyle`, int-vector/rect/bounds variants, arrays, and lists (`RuntimeInspectorUtils.cs:26-48`). Full Lua explicitly handles only `Vector2`, `Vector3`, `Vector4`, `Color`, and `Quaternion`.
- Numeric width coverage: RuntimeInspector has handlers for `uint`, `long`, `ulong`, `byte`, `sbyte`, `short`, `ushort`, `char`, and `decimal` (`NumberHandlers.cs:24-231`). Full Lua explicitly handles only `int`, `float`, and `double`, with other numeric types falling through `Convert.ChangeType` (`CoreAiFullUnityLuaRuntimeBindings.cs:812-825`, `:901`).
- GameObject authoring affordances: RuntimeInspector exposes GameObject active/name/tag/layer and component drawers, add/remove component UI, and component filtering (`GameObjectField.cs:12-25`, `:96-115`, `:122-136`, `:143-238`). Full Lua can list components and set active/transform basics, but does not expose RuntimeInspector's component add/remove/type picker workflow.

## 4. WebGL/IL2CPP Reflection-Stripping Risk

Current project state:

- Full Lua is disabled for WebGL player builds in `CoreAILifetimeScope`: `UNITY_WEBGL && !UNITY_EDITOR` forces `effectiveFullLuaAccess = false`: `CoreAILifetimeScope.cs:108-118`.
- Root `Assets/link.xml` explicitly preserves Lua binding types that run on WebGL and explicitly does not preserve `CoreAiFullUnityLuaRuntimeBindings` because Full is disabled on WebGL: `Assets/link.xml:21-32`.
- The package-local `Assets/CoreAiUnity/link.xml` only preserves `MessagePipeAiCommandSink`: `Assets/CoreAiUnity/link.xml:1-7`.

Risk assessment:

- If Full Lua remains disabled on WebGL, no additional `link.xml` is needed for `CoreAiFullUnityLuaRuntimeBindings`. The current root `Assets/link.xml` is aligned with that policy.
- If Full Lua is ever enabled on WebGL/IL2CPP, `link.xml` for the Full binding type alone is not enough. The binding can be preserved, but the target game/component members it reflects over may still be stripped because Full Lua discovers arbitrary types/members by name at runtime (`ResolveType` scans assemblies at `CoreAiFullUnityLuaRuntimeBindings.cs:663-693`; `ResolveMember` uses reflected names at `:696-727`).
- A safe IL2CPP Full-Lua design would need either an explicit allow-list of component/member types with generated preserve entries, `[UnityEngine.Scripting.Preserve]` annotations on intended reflected members, or a generated `link.xml` per project/game surface. A blanket `preserve="all"` on whole gameplay assemblies would reduce failures but is too broad for build size and security.
- RuntimeInspector has the same general reflection-stripping class of risk when reflecting arbitrary runtime types, but its member list is constrained to serializable/supported variables and its picker/editor stack is UI-driven. It does not automatically solve IL2CPP stripping for CoreAI Full Lua unless CoreAI also adopts an allow-listed, generated-preserve model.

## 5. Recommendation

Do not port RuntimeInspector wholesale into Full Lua. The current standalone reflection path is enough for the current documented Full-tier use case: admin/debug scene inspection, simple transform edits, primitive/member edits, and method calls on non-WebGL builds. It is small, capability-gated, already integrated with CoreAI deny-list policy, and intentionally absent from WebGL.

Do create a small shared CoreAI introspection/coercion feature if CoreAI wants Full Lua to become a reliable authoring surface rather than a debug escape hatch.

Recommended shape:

1. Add a CoreAI-owned introspection service, not a dependency on RuntimeInspector UI classes.
   - Example responsibility: enumerate writable members using RuntimeInspector-inspired filters; convert structured values into target types; resolve Unity object references by instance id/path/name with explicit policy; serialize values back to Lua-friendly objects.
   - Keep this under CoreAI runtime code so Lua, tools, and future admin UI can share it without depending on RuntimeInspector's uGUI drawer layer.

2. Reuse RuntimeInspector ideas, not its UI implementation.
   - Port/adapt its serializable-type filter from `RuntimeInspectorUtils.cs:26-48` and `:741-779`.
   - Port/adapt its stricter field/property filtering from `RuntimeInspectorUtils.cs:597-639`.
   - Port/adapt its object-reference compatibility rules from `RuntimeInspectorUtils.cs:343-377`.
   - Port/adapt array/list resize and default-element semantics only if Lua needs table-to-array/list writes (`ArrayField.cs:216-319`).

3. Keep Full Lua's current direct reflection for methods and simple fields until a shared service exists.
   - Method invocation is outside RuntimeInspector's ordinary field-drawer editing path; Full Lua's `unity_call` is already specific to Lua and can stay local.

4. Treat WebGL Full Lua as unsupported unless there is an explicit allow-listed preserve pipeline.
   - Existing code and `Assets/link.xml` intentionally disable/omit Full on WebGL (`CoreAILifetimeScope.cs:108-118`, `Assets/link.xml:21-32`).
   - If product requirements change, generate a per-project reflected surface manifest and matching `link.xml`; do not simply add `CoreAiFullUnityLuaRuntimeBindings` to `link.xml` and assume arbitrary component reflection will survive IL2CPP.

Bottom line: current standalone reflection is acceptable for controlled non-WebGL debug Full Lua. For production-grade Lua editing of Unity object state, CoreAI should build a shared introspection/coercion layer inspired by RuntimeInspector's filters and type handlers, then have Lua wrap that service. This avoids coupling Lua to RuntimeInspector UI code while closing the concrete gaps around serializable filtering, Unity object references, arrays/lists, nested objects, and IL2CPP preserve planning.

