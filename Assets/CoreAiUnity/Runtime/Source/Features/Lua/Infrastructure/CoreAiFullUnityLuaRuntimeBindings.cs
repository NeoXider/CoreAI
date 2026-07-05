#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Sandbox;
using MoonSharp.Interpreter;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// Full-tier Lua bindings: reflection access to live <see cref="GameObject"/>s and components.
    /// Registered only when <see cref="LuaCapabilities.Full"/> is granted. By default, only public
    /// members are exposed; non-public member access is opt-in.
    /// </summary>
    public sealed class CoreAiFullUnityLuaRuntimeBindings : IGameLuaRuntimeBindings
    {
        private static readonly ConcurrentDictionary<string, Type> TypeCache = new(StringComparer.Ordinal);

        private static readonly ConcurrentDictionary<(Type type, string member, bool nonPublic), MemberInfo>
            MemberCache = new();

        private static readonly ConcurrentDictionary<(Type type, bool nonPublic), List<MemberInfo>>
            SettableMemberCache = new();

        private readonly IGameLogger _logger;
        private readonly bool _allowNonPublic;
        private readonly IFullLuaAccessBlacklistPolicy _blacklistPolicy;

        public CoreAiFullUnityLuaRuntimeBindings(
            IGameLogger logger = null,
            bool allowNonPublicMembers = false,
            IFullLuaAccessBlacklistPolicy blacklistPolicy = null)
        {
            _logger = logger;
            _allowNonPublic = allowNonPublicMembers;
            _blacklistPolicy = blacklistPolicy ?? AllowAllFullLuaAccessBlacklistPolicy.Instance;

            // Full tier with no deny-list is fail-open: reflection can read/write/call any public member of
            // any component. That is the documented admin/debug behaviour, but it must not be silent — a
            // host that enabled Full and forgot to supply a policy should see why everything is reachable.
            if (blacklistPolicy == null)
            {
                _logger?.LogWarning(GameLogFeature.MessagePipe,
                    "[FullLua] No IFullLuaAccessBlacklistPolicy supplied; Full-tier reflection is unrestricted " +
                    "(allow-all). Provide a deny-list policy to restrict which components/members scripts may reach.");
            }
        }

        private BindingFlags MemberFlags()
        {
            return BindingFlags.Instance | BindingFlags.Public |
                   (_allowNonPublic ? BindingFlags.NonPublic : BindingFlags.Default);
        }

        public void RegisterGameplayApis(LuaApiRegistry registry)
        {
            registry.Register("unity_find", new Func<string, int>(FindByName));
            registry.Register("unity_id", new Func<string, int>(FindByName));
            registry.RegisterCallback("unity_list_objects",
                (ctx, args) => ToLuaValue(ctx.GetScript(), ListObjects(ReadOptionalMax(args, 0))));
            registry.RegisterCallback("unity_find_all", (ctx, args) => ToLuaValue(ctx.GetScript(),
                FindAll(ReadString(args, 0, "unity_find_all"), ReadOptionalMax(args, 1))));
            registry.RegisterCallback("unity_find_by_tag", (ctx, args) => ToLuaValue(ctx.GetScript(),
                FindByTag(ReadString(args, 0, "unity_find_by_tag"), ReadOptionalMax(args, 1))));
            registry.RegisterCallback("unity_find_by_component", (ctx, args) => ToLuaValue(ctx.GetScript(),
                FindByComponent(ReadString(args, 0, "unity_find_by_component"), ReadOptionalMax(args, 1))));
            registry.Register("unity_describe_object", new Func<int, object>(DescribeObject));
            registry.Register("unity_set_active", new Func<int, bool, bool>(SetActive));
            registry.Register("unity_get_position", new Func<int, Table>(GetPosition));
            registry.Register("unity_set_position", new Func<int, double, double, double, bool>(SetPosition));
            registry.Register("unity_get_transform", new Func<int, object>(GetTransform));
            registry.Register("unity_set_rotation_euler",
                new Func<int, double, double, double, bool>(SetRotationEuler));
            registry.Register("unity_set_scale", new Func<int, double, double, double, bool>(SetScale));
            registry.Register("unity_parent", new Func<int, int, bool, bool>(SetParent));
            registry.Register("unity_get_children", new Func<int, List<object>>(GetChildren));
            registry.Register("unity_list_components", new Func<int, List<string>>(ListComponents));
            registry.RegisterCallback("unity_list_members", (ctx, args) => ToLuaValue(ctx.GetScript(),
                ListMembers((int)args.AsType(0, "unity_list_members", DataType.Number, false).Number,
                    ReadString(args, 1, "unity_list_members"))));
            registry.Register("unity_get_member", new Func<int, string, string, DynValue>(GetMember));
            registry.Register("unity_set_member", new Func<int, string, string, DynValue, bool>(SetMember));
            registry.Register("unity_call", new Func<int, string, string, DynValue[], DynValue>(CallMethod));
            registry.Register("unity_add_component", new Func<int, string, bool>(AddComponent));
            registry.Register("unity_destroy", new Func<int, bool>(DestroyObject));
        }

        private static int ReadOptionalMax(CallbackArguments args, int index)
        {
            if (args == null || args.Count <= index || args[index].IsNil() || args[index].Type == DataType.Void)
            {
                return 100;
            }

            return (int)args.AsType(index, "max", DataType.Number, false).Number;
        }

        private static string ReadString(CallbackArguments args, int index, string apiName)
        {
            return args.AsType(index, apiName, DataType.String, false).String;
        }

        private static DynValue ToLuaValue(Script script, object value)
        {
            if (value == null)
            {
                return DynValue.Nil;
            }

            switch (value)
            {
                case DynValue dyn:
                    return dyn;
                case bool b:
                    return DynValue.NewBoolean(b);
                case string s:
                    return DynValue.NewString(s);
                case int i:
                    return DynValue.NewNumber(i);
                case long l:
                    return DynValue.NewNumber(l);
                case float f:
                    return DynValue.NewNumber(f);
                case double d:
                    return DynValue.NewNumber(d);
                case IDictionary<string, object> dict:
                {
                    Table table = new(script);
                    foreach (KeyValuePair<string, object> kv in dict)
                    {
                        table[kv.Key] = ToLuaValue(script, kv.Value);
                    }

                    return DynValue.NewTable(table);
                }
                case IEnumerable<string> strings:
                {
                    Table table = new(script);
                    int index = 1;
                    foreach (string item in strings)
                    {
                        table[index++] = DynValue.NewString(item);
                    }

                    return DynValue.NewTable(table);
                }
                case IEnumerable<object> list:
                {
                    Table table = new(script);
                    int index = 1;
                    foreach (object item in list)
                    {
                        table[index++] = ToLuaValue(script, item);
                    }

                    return DynValue.NewTable(table);
                }
                default:
                    return DynValue.NewString(value.ToString());
            }
        }

        private static int FindByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return 0;
            }

            GameObject go = GameObject.Find(name.Trim());
            return GetObjectId(go);
        }

        private List<object> ListObjects(int max)
        {
            List<object> results = new();
            CollectSceneObjects("", ClampMax(max), results, MatchAny);
            return results;
        }

        private List<object> FindAll(string pattern, int max)
        {
            string search = (pattern ?? "").Trim();
            List<object> results = new();
            CollectSceneObjects(search, ClampMax(max), results, MatchNameOrPath);
            return results;
        }

        private List<object> FindByTag(string tag, int max)
        {
            string targetTag = (tag ?? "").Trim();
            List<object> results = new();
            if (string.IsNullOrEmpty(targetTag))
            {
                return results;
            }

            CollectSceneObjects(targetTag, ClampMax(max), results,
                (go, search) => string.Equals(go.tag, search, StringComparison.Ordinal));
            return results;
        }

        private List<object> FindByComponent(string componentType, int max)
        {
            Type type = ResolveType((componentType ?? "").Trim());
            List<object> results = new();
            if (type == null ||
                !typeof(Component).IsAssignableFrom(type) ||
                !IsTypeAllowed(type))
            {
                return results;
            }

            CollectSceneObjects(componentType, ClampMax(max), results,
                (go, _) => go.GetComponent(type) != null);
            return results;
        }

        private object DescribeObject(int instanceId)
        {
            GameObject go = Resolve(instanceId);
            return go == null ? null : BuildObjectSummary(go, true, true);
        }

        private static bool SetActive(int instanceId, bool active)
        {
            GameObject go = Resolve(instanceId);
            if (go == null)
            {
                return false;
            }

            go.SetActive(active);
            return true;
        }

        /// <summary>
        /// Full-tier: add a component of the named type to an object (e.g. "Rigidbody", "BoxCollider", or a
        /// fully-qualified custom type). Honors the host blacklist policy. Returns false when the object or
        /// type cannot be resolved. Symmetric with the curated <c>coreai_component_add</c>, but reflection-based.
        /// </summary>
        private bool AddComponent(int instanceId, string componentType)
        {
            GameObject go = Resolve(instanceId);
            if (go == null || string.IsNullOrWhiteSpace(componentType))
            {
                return false;
            }

            Type type = ResolveType(componentType.Trim());
            if (type == null || !typeof(Component).IsAssignableFrom(type))
            {
                throw new ScriptRuntimeException(
                    $"unity_add_component: '{componentType}' is not a resolvable Component type.");
            }

            if (!IsTypeAllowed(type))
            {
                throw new ScriptRuntimeException(
                    $"Full Lua access to type '{type.Name}' is denied by host policy.");
            }

            go.AddComponent(type);
            return true;
        }

        /// <summary>Full-tier: destroy a scene object by id. Returns false when the object is not found.</summary>
        private static bool DestroyObject(int instanceId)
        {
            GameObject go = Resolve(instanceId);
            if (go == null)
            {
                return false;
            }

            // Destroy() is illegal outside Play mode (it throws in edit mode and would corrupt assets), so
            // route to DestroyImmediate when not playing. At runtime, Destroy() is the correct deferred path.
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(go);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(go);
            }

            return true;
        }

        private static Table GetPosition(int instanceId)
        {
            GameObject go = Resolve(instanceId);
            if (go == null)
            {
                throw new ScriptRuntimeException($"unity_get_position: object id {instanceId} not found.");
            }

            Vector3 p = go.transform.position;
            Table t = new(null);
            t["x"] = p.x;
            t["y"] = p.y;
            t["z"] = p.z;
            return t;
        }

        private static bool SetPosition(int instanceId, double x, double y, double z)
        {
            GameObject go = Resolve(instanceId);
            if (go == null)
            {
                return false;
            }

            go.transform.position = new Vector3((float)x, (float)y, (float)z);
            return true;
        }

        private static object GetTransform(int instanceId)
        {
            GameObject go = Resolve(instanceId);
            return go == null ? null : BuildTransformSummary(go.transform);
        }

        private static bool SetRotationEuler(int instanceId, double x, double y, double z)
        {
            GameObject go = Resolve(instanceId);
            if (go == null)
            {
                return false;
            }

            go.transform.rotation = Quaternion.Euler((float)x, (float)y, (float)z);
            return true;
        }

        private static bool SetScale(int instanceId, double x, double y, double z)
        {
            GameObject go = Resolve(instanceId);
            if (go == null)
            {
                return false;
            }

            go.transform.localScale = new Vector3((float)x, (float)y, (float)z);
            return true;
        }

        private static bool SetParent(int childInstanceId, int parentInstanceId, bool worldPositionStays)
        {
            GameObject child = Resolve(childInstanceId);
            if (child == null)
            {
                return false;
            }

            GameObject parent = Resolve(parentInstanceId);
            child.transform.SetParent(parent != null ? parent.transform : null, worldPositionStays);
            return true;
        }

        private List<object> GetChildren(int instanceId)
        {
            List<object> children = new();
            GameObject go = Resolve(instanceId);
            if (go == null)
            {
                return children;
            }

            Transform t = go.transform;
            for (int i = 0; i < t.childCount; i++)
            {
                children.Add(BuildObjectSummary(t.GetChild(i).gameObject, true,
                    false));
            }

            return children;
        }

        private List<string> ListComponents(int instanceId)
        {
            GameObject go = Resolve(instanceId);
            List<string> names = new();
            if (go == null)
            {
                return names;
            }

            Component[] components = go.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                Component c = components[i];
                if (c != null && IsTypeAllowed(c.GetType()))
                {
                    names.Add(c.GetType().Name);
                }
            }

            return names;
        }

        private List<object> ListMembers(int instanceId, string componentType)
        {
            object target = ResolveComponent(instanceId, componentType);
            if (target == null)
            {
                throw new ScriptRuntimeException(
                    $"unity_list_members: component '{componentType}' on id {instanceId} not found.");
            }

            List<object> results = new();
            List<MemberInfo> members = GetAllowedSettableMembers(target.GetType());
            for (int i = 0; i < members.Count; i++)
            {
                MemberInfo member = members[i];
                results.Add(new Dictionary<string, object>
                {
                    { "name", member.Name },
                    { "type", FriendlyTypeName(GetMemberValueType(member)) }
                });
            }

            return results;
        }

        private DynValue GetMember(int instanceId, string componentType, string memberName)
        {
            object target = ResolveComponent(instanceId, componentType);
            if (target == null)
            {
                throw new ScriptRuntimeException(
                    $"unity_get_member: component '{componentType}' on id {instanceId} not found.");
            }

            MemberInfo member = ResolveMember(target.GetType(), memberName);
            object value = member switch
            {
                FieldInfo fi => fi.GetValue(target),
                PropertyInfo pi => pi.GetValue(target),
                _ => throw new ScriptRuntimeException($"unity_get_member: '{memberName}' is not readable.")
            };

            return ToDyn(value);
        }

        private bool SetMember(int instanceId, string componentType, string memberName, DynValue value)
        {
            object target = ResolveComponent(instanceId, componentType);
            if (target == null)
            {
                return false;
            }

            MemberInfo member = ResolveMember(target.GetType(), memberName);
            object converted = FromDyn(value, member);
            switch (member)
            {
                case FieldInfo fi:
                    fi.SetValue(target, converted);
                    return true;
                case PropertyInfo pi when pi.CanWrite:
                    pi.SetValue(target, converted);
                    return true;
                default:
                    throw new ScriptRuntimeException($"unity_set_member: '{memberName}' is not writable.");
            }
        }

        private DynValue CallMethod(int instanceId, string componentType, string methodName, DynValue[] args)
        {
            object target = ResolveComponent(instanceId, componentType);
            if (target == null)
            {
                throw new ScriptRuntimeException(
                    $"unity_call: component '{componentType}' on id {instanceId} not found.");
            }

            Type type = target.GetType();
            MethodInfo method;
            try
            {
                method = type.GetMethod(methodName, MemberFlags());
            }
            catch (AmbiguousMatchException)
            {
                throw new ScriptRuntimeException(
                    $"unity_call: method '{methodName}' is ambiguous on {type.Name} (overloaded); call is not supported.");
            }

            if (method == null)
            {
                throw new ScriptRuntimeException($"unity_call: method '{methodName}' not found on {type.Name}.");
            }

            EnsureMemberAllowed(method);

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != (args?.Length ?? 0))
            {
                throw new ScriptRuntimeException(
                    $"unity_call: '{methodName}' expects {parameters.Length} args, got {args?.Length ?? 0}.");
            }

            object[] invokeArgs = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                invokeArgs[i] = ConvertArg(args[i], parameters[i].ParameterType);
            }

            object result = method.Invoke(target, invokeArgs);
            return ToDyn(result);
        }

        private static GameObject Resolve(int instanceId)
        {
            if (instanceId == 0)
            {
                return null;
            }

            // Resolve by the SAME id scheme GetObjectId() handed out (instance id on older Unity,
            // entity-id value on 6000.3+). Scanning loaded objects keeps the round-trip consistent across
            // Unity versions and works in both Edit and Play mode, unlike Resources.InstanceIDToObject,
            // which uses the legacy scheme and returns null in Edit mode. Full tier is admin/debug, not a
            // hot path, so the linear scan is acceptable.
            GameObject[] loaded = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int i = 0; i < loaded.Length; i++)
            {
                if (GetObjectId(loaded[i]) == instanceId)
                {
                    return loaded[i];
                }
            }

            return null;
        }

        /// <summary>
        /// Resolves any loaded <see cref="UnityEngine.Object"/> (Material, Texture, Transform, a Component,
        /// GameObject, …) by the same id scheme <see cref="GetObjectId"/> hands out, so Lua can assign a
        /// reference member to another object. Returns null when no loaded object of an assignable type
        /// matches the id (the caller's SetValue then nulls/throws as usual).
        /// </summary>
        private static UnityEngine.Object ResolveUnityObject(int objectId, Type wanted)
        {
            if (objectId == 0)
            {
                return null;
            }

            UnityEngine.Object[] loaded = Resources.FindObjectsOfTypeAll(wanted);
            for (int i = 0; i < loaded.Length; i++)
            {
                if (GetObjectId(loaded[i]) == objectId)
                {
                    return loaded[i];
                }
            }

            return null;
        }

        private static Vector3 ReadVector3(Table t)
        {
            if (t == null)
            {
                return Vector3.zero;
            }

            return new Vector3(
                (float)t.Get("x").CastToNumber(),
                (float)t.Get("y").CastToNumber(),
                (float)t.Get("z").CastToNumber());
        }

        private delegate bool ObjectMatch(GameObject go, string search);

        private void CollectSceneObjects(
            string search,
            int max,
            List<object> results,
            ObjectMatch match)
        {
            if (results == null || max <= 0)
            {
                return;
            }

            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                return;
            }

            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length && results.Count < max; i++)
            {
                CollectObjectRecursive(roots[i], search, max, results, match);
            }
        }

        private void CollectObjectRecursive(
            GameObject go,
            string search,
            int max,
            List<object> results,
            ObjectMatch match)
        {
            if (go == null || results.Count >= max)
            {
                return;
            }

            if (match == null || match(go, search))
            {
                results.Add(BuildObjectSummary(go, false, false));
            }

            Transform t = go.transform;
            for (int i = 0; i < t.childCount && results.Count < max; i++)
            {
                CollectObjectRecursive(t.GetChild(i).gameObject, search, max, results, match);
            }
        }

        private static bool MatchAny(GameObject go, string search)
        {
            return true;
        }

        private static bool MatchNameOrPath(GameObject go, string search)
        {
            if (string.IsNullOrEmpty(search))
            {
                return true;
            }

            return go.name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   GetPath(go).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int ClampMax(int max)
        {
            return Math.Max(1, Math.Min(max <= 0 ? 100 : max, 500));
        }

        private Dictionary<string, object> BuildObjectSummary(
            GameObject go,
            bool includeTransform,
            bool includeComponents)
        {
            Dictionary<string, object> summary = new()
            {
                { "id", GetObjectId(go) },
                { "name", go.name },
                { "path", GetPath(go) },
                { "tag", go.tag },
                { "layer", LayerMask.LayerToName(go.layer) },
                { "layer_index", go.layer },
                { "active", go.activeSelf },
                { "active_in_hierarchy", go.activeInHierarchy },
                { "parent_id", go.transform.parent != null ? GetObjectId(go.transform.parent.gameObject) : 0 },
                { "parent", go.transform.parent != null ? go.transform.parent.gameObject.name : "" },
                { "child_count", go.transform.childCount }
            };

            if (includeTransform)
            {
                summary["transform"] = BuildTransformSummary(go.transform);
            }

            if (includeComponents)
            {
                summary["components"] = ListComponents(GetObjectId(go));
            }

            return summary;
        }

        private static int GetObjectId(UnityEngine.Object obj)
        {
            if (obj == null)
            {
                return 0;
            }

            // Unity 6.5 made BOTH the EntityId->int implicit cast AND Object.GetInstanceID() obsolete ERRORS
            // (CS0619). EntityId.GetHashCode() yields a stable, session-unique int for the in-session object
            // lookup these ids feed (collisions are negligible at scene-object counts) without any obsolete API.
            return obj.GetEntityId().GetHashCode();
        }

        private static Dictionary<string, object> BuildTransformSummary(Transform transform)
        {
            Vector3 p = transform.position;
            Vector3 r = transform.eulerAngles;
            Vector3 s = transform.localScale;
            return new Dictionary<string, object>
            {
                { "position", Vector(p) },
                { "rotation", Vector(r) },
                { "scale", Vector(s) }
            };
        }

        private static Dictionary<string, object> Vector(Vector3 v)
        {
            return new Dictionary<string, object>
            {
                { "x", (double)v.x },
                { "y", (double)v.y },
                { "z", (double)v.z }
            };
        }

        private static string GetPath(GameObject go)
        {
            if (go == null)
            {
                return "";
            }

            Stack<string> names = new();
            Transform current = go.transform;
            while (current != null)
            {
                names.Push(current.gameObject.name);
                current = current.parent;
            }

            return string.Join("/", names);
        }

        private object ResolveComponent(int instanceId, string componentTypeName)
        {
            GameObject go = Resolve(instanceId);
            if (go == null || string.IsNullOrWhiteSpace(componentTypeName))
            {
                return null;
            }

            Type type = ResolveType(componentTypeName.Trim());
            if (type == null)
            {
                return null;
            }

            if (!IsTypeAllowed(type))
            {
                throw new ScriptRuntimeException($"Full Lua access to type '{type.Name}' is denied by host policy.");
            }

            return go.GetComponent(type);
        }

        private static Type ResolveType(string name)
        {
            if (TypeCache.TryGetValue(name, out Type cached))
            {
                return cached;
            }

            Type t = Type.GetType(name);
            if (t == null)
            {
                t = Type.GetType("UnityEngine." + name + ", UnityEngine.CoreModule");
            }

            if (t == null)
            {
                // Scan loaded assemblies for the fully-qualified name AND the bare name with a UnityEngine
                // prefix, so "Rigidbody" (in PhysicsModule, not CoreModule), "Light", "Camera", etc. resolve
                // from a short name without the caller knowing which UnityEngine.*Module assembly hosts them.
                string unityQualified = "UnityEngine." + name;
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    t = asm.GetType(name) ?? asm.GetType(unityQualified);
                    if (t != null)
                    {
                        break;
                    }
                }
            }

            if (t != null)
            {
                TypeCache[name] = t;
            }

            return t;
        }

        private MemberInfo ResolveMember(Type type, string memberName)
        {
            (Type, string, bool) key = (type, memberName, _allowNonPublic);
            if (MemberCache.TryGetValue(key, out MemberInfo cached))
            {
                EnsureMemberAllowed(cached);
                return cached;
            }

            BindingFlags flags = MemberFlags();
            MemberInfo member;
            try
            {
                member = type.GetField(memberName, flags) as MemberInfo ??
                         type.GetProperty(memberName, flags);
            }
            catch (AmbiguousMatchException)
            {
                throw new ScriptRuntimeException(
                    $"unity_call: member '{memberName}' is ambiguous on {type.Name} (overloaded); call is not supported.");
            }

            if (member == null)
            {
                throw new ScriptRuntimeException(
                    $"Member '{memberName}' not found on {type.Name}. Settable members: {BuildSettableMemberHint(type)}");
            }

            EnsureMemberAllowed(member);
            MemberCache[key] = member;
            return member;
        }

        private bool IsTypeAllowed(Type type)
        {
            if (type == null)
            {
                return false;
            }

            // Fail-CLOSED hard floor: certain namespaces/types are NEVER reachable from Full Lua, even with
            // the allow-all default policy. Without this, Full tier + no deny-list lets a script reflect into
            // System.IO/Reflection/Diagnostics/etc and escape the sandbox (arbitrary process spawn, file I/O,
            // assembly loading = RCE). This runs BEFORE the permissive allow-list path below.
            if (IsHardDeniedType(type))
            {
                return false;
            }

            return _blacklistPolicy?.IsTypeAllowed(type) ?? true;
        }

        private void EnsureMemberAllowed(MemberInfo member)
        {
            if (member == null)
            {
                return;
            }

            // Fail-CLOSED hard floor (mirrors IsTypeAllowed): a member declared on a dangerous type — or whose
            // member TYPE is dangerous (method return, property type, field type) — is never callable/readable/
            // writable from Full Lua regardless of the host deny-list policy.
            Type memberType = member switch
            {
                MethodInfo mi => mi.ReturnType,
                PropertyInfo pi => pi.PropertyType,
                FieldInfo fi => fi.FieldType,
                _ => null
            };
            if (IsHardDeniedType(member.DeclaringType) || IsHardDeniedType(memberType))
            {
                throw new ScriptRuntimeException(
                    $"Full Lua access to member '{member.DeclaringType?.Name}.{member.Name}' is denied " +
                    "(dangerous type; blocked by the Full Lua hard deny-list).");
            }

            if (!(_blacklistPolicy?.IsMemberAllowed(member) ?? true))
            {
                throw new ScriptRuntimeException(
                    $"Full Lua access to member '{member.DeclaringType?.Name}.{member.Name}' is denied by host policy.");
            }
        }

        /// <summary>
        /// Namespace prefixes that are NEVER reachable from Full-tier Lua, even when the host supplies no
        /// deny-list (allow-all). These gate the .NET capabilities that would let a script break out of the
        /// game sandbox: file I/O, reflection escapes, process/diagnostics, threading, networking, assembly
        /// loading, environment access, and activation. <c>UnityEngine.*</c> and ordinary game/component
        /// types are intentionally NOT here, so Full Lua stays useful for gameplay scripting.
        /// </summary>
        private static readonly string[] HardDeniedNamespacePrefixes =
        {
            "System.IO",
            "System.Reflection",
            "System.Diagnostics",
            "System.Threading",
            "System.Runtime",
            "System.Net",
            "System.Security",
            "System.CodeDom",
            "Microsoft.CSharp",
            "Mono.",
            "MoonSharp"
        };

        /// <summary>
        /// Fully-qualified dangerous types that must be blocked even though their namespace (e.g. plain
        /// <c>System</c>) is otherwise allowed: the reflection/activation/environment/process escapes.
        /// </summary>
        private static readonly HashSet<string> HardDeniedFullTypeNames = new(StringComparer.Ordinal)
        {
            "System.Type",
            "System.Activator",
            "System.AppDomain",
            "System.Environment",
            "System.GC",
            "System.Runtime.InteropServices.Marshal",
            "System.AppContext",
            "System.OperatingSystem"
        };

        /// <summary>
        /// True when <paramref name="type"/> (or, for generics, any of its type arguments) is one of the
        /// hard-denied dangerous .NET types/namespaces that must stay out of Full Lua's reach.
        /// </summary>
        private static bool IsHardDeniedType(Type type)
        {
            if (type == null)
            {
                return false;
            }

            if (type.IsByRef || type.IsArray || type.IsPointer)
            {
                Type element = type.GetElementType();
                if (element != null && element != type)
                {
                    return IsHardDeniedType(element);
                }
            }

            string fullName = type.FullName;
            if (!string.IsNullOrEmpty(fullName) && HardDeniedFullTypeNames.Contains(fullName))
            {
                return true;
            }

            string ns = type.Namespace;
            if (!string.IsNullOrEmpty(ns))
            {
                for (int i = 0; i < HardDeniedNamespacePrefixes.Length; i++)
                {
                    string prefix = HardDeniedNamespacePrefixes[i];
                    if (ns.Equals(prefix, StringComparison.Ordinal) ||
                        ns.StartsWith(prefix + ".", StringComparison.Ordinal) ||
                        (ns.StartsWith(prefix, StringComparison.Ordinal) &&
                         prefix.EndsWith(".", StringComparison.Ordinal)))
                    {
                        return true;
                    }
                }
            }

            // Reflection-typed instances often surface as the abstract bases (Type, MemberInfo, Assembly,
            // MethodBase, …) whose Namespace is System.Reflection (caught above) or System (Type, caught by
            // name above). Also block any type that itself derives from these reflection roots.
            if (typeof(Type).IsAssignableFrom(type) ||
                typeof(MemberInfo).IsAssignableFrom(type) ||
                typeof(Assembly).IsAssignableFrom(type) ||
                typeof(Module).IsAssignableFrom(type) ||
                typeof(Delegate).IsAssignableFrom(type))
            {
                return true;
            }

            if (type.IsGenericType)
            {
                foreach (Type arg in type.GetGenericArguments())
                {
                    if (arg != type && IsHardDeniedType(arg))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static DynValue ToDyn(object value)
        {
            if (value == null)
            {
                return DynValue.Nil;
            }

            return value switch
            {
                bool b => DynValue.NewBoolean(b),
                string s => DynValue.NewString(s),
                float f => DynValue.NewNumber(f),
                double d => DynValue.NewNumber(d),
                int i => DynValue.NewNumber(i),
                long l => DynValue.NewNumber(l),
                Enum e => DynValue.NewString(e.ToString()),
                Vector2 v => NewNumberTable(("x", v.x), ("y", v.y)),
                Vector3 v => NewNumberTable(("x", v.x), ("y", v.y), ("z", v.z)),
                Vector4 v => NewNumberTable(("x", v.x), ("y", v.y), ("z", v.z), ("w", v.w)),
                Color c => NewNumberTable(("r", c.r), ("g", c.g), ("b", c.b), ("a", c.a)),
                Color32 c => NewNumberTable(("r", c.r), ("g", c.g), ("b", c.b), ("a", c.a)),
                Quaternion q => NewNumberTable(("x", q.x), ("y", q.y), ("z", q.z), ("w", q.w)),
                Rect r => NewNumberTable(("x", r.x), ("y", r.y), ("width", r.width), ("height", r.height)),
                _ => DynValue.NewString(value.ToString())
            };
        }

        private static DynValue NewNumberTable(params (string key, float value)[] values)
        {
            Table table = new(null);
            for (int i = 0; i < values.Length; i++)
            {
                table[values[i].key] = values[i].value;
            }

            return DynValue.NewTable(table);
        }

        private static object FromDyn(DynValue value, MemberInfo member)
        {
            Type targetType = member switch
            {
                FieldInfo fi => fi.FieldType,
                PropertyInfo pi => pi.PropertyType,
                _ => typeof(object)
            };
            return ConvertArg(value, targetType);
        }

        private static object ConvertArg(DynValue value, Type targetType)
        {
            if (value.IsNil())
            {
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
            }

            if (targetType == typeof(string))
            {
                return value.CastToString();
            }

            if (targetType == typeof(bool))
            {
                return value.CastToBool();
            }

            if (targetType == typeof(int))
            {
                return (int)value.CastToNumber();
            }

            if (targetType == typeof(float))
            {
                return (float)value.CastToNumber();
            }

            if (targetType == typeof(double))
            {
                return value.CastToNumber();
            }

            // Remaining numeric widths: route every other primitive numeric type through CastToNumber so a
            // Lua number can set a long/short/byte/uint/etc member instead of falling through to ChangeType.
            if (targetType == typeof(long))
            {
                return (long)value.CastToNumber();
            }

            if (targetType == typeof(uint))
            {
                return (uint)value.CastToNumber();
            }

            if (targetType == typeof(ulong))
            {
                return (ulong)value.CastToNumber();
            }

            if (targetType == typeof(short))
            {
                return (short)value.CastToNumber();
            }

            if (targetType == typeof(ushort))
            {
                return (ushort)value.CastToNumber();
            }

            if (targetType == typeof(byte))
            {
                return (byte)value.CastToNumber();
            }

            if (targetType == typeof(sbyte))
            {
                return (sbyte)value.CastToNumber();
            }

            if (targetType.IsEnum && value.Type == DataType.String)
            {
                return Enum.Parse(targetType, value.String, true);
            }

            // Enum from a Lua number (the underlying integral value).
            if (targetType.IsEnum && value.Type == DataType.Number)
            {
                return Enum.ToObject(targetType, (long)value.CastToNumber());
            }

            if (targetType == typeof(Vector3) && value.Type == DataType.Table)
            {
                Table t = value.Table;
                return new Vector3(
                    (float)t.Get("x").CastToNumber(),
                    (float)t.Get("y").CastToNumber(),
                    (float)t.Get("z").CastToNumber());
            }

            if (targetType == typeof(Vector2) && value.Type == DataType.Table)
            {
                Table t = value.Table;
                return new Vector2(
                    (float)t.Get("x").CastToNumber(),
                    (float)t.Get("y").CastToNumber());
            }

            if (targetType == typeof(Vector4) && value.Type == DataType.Table)
            {
                Table t = value.Table;
                return new Vector4(
                    (float)t.Get("x").CastToNumber(),
                    (float)t.Get("y").CastToNumber(),
                    (float)t.Get("z").CastToNumber(),
                    (float)t.Get("w").CastToNumber());
            }

            if (targetType == typeof(Color))
            {
                if (value.Type == DataType.String)
                {
                    if (ColorUtility.TryParseHtmlString(value.String, out Color color))
                    {
                        return color;
                    }

                    throw new ScriptRuntimeException($"Could not parse Color from '{value.String}'.");
                }

                if (value.Type == DataType.Table)
                {
                    Table t = value.Table;
                    return new Color(
                        (float)t.Get("r").CastToNumber(),
                        (float)t.Get("g").CastToNumber(),
                        (float)t.Get("b").CastToNumber(),
                        ReadOptionalTableNumber(t, "a", 1f));
                }
            }

            if (targetType == typeof(Quaternion) && value.Type == DataType.Table)
            {
                Table t = value.Table;
                DynValue w = t.Get("w");
                if (!w.IsNil())
                {
                    return new Quaternion(
                        (float)t.Get("x").CastToNumber(),
                        (float)t.Get("y").CastToNumber(),
                        (float)t.Get("z").CastToNumber(),
                        (float)w.CastToNumber());
                }

                return Quaternion.Euler(
                    (float)t.Get("x").CastToNumber(),
                    (float)t.Get("y").CastToNumber(),
                    (float)t.Get("z").CastToNumber());
            }

            if (targetType == typeof(Rect) && value.Type == DataType.Table)
            {
                Table t = value.Table;
                return new Rect(
                    (float)t.Get("x").CastToNumber(),
                    (float)t.Get("y").CastToNumber(),
                    (float)t.Get("width").CastToNumber(),
                    (float)t.Get("height").CastToNumber());
            }

            if (targetType == typeof(Bounds) && value.Type == DataType.Table)
            {
                Table t = value.Table;
                Vector3 center = ReadVector3(t.Get("center").Table);
                Vector3 size = ReadVector3(t.Get("size").Table);
                return new Bounds(center, size);
            }

            if (targetType == typeof(Color32))
            {
                if (value.Type == DataType.String &&
                    ColorUtility.TryParseHtmlString(value.String, out Color parsed))
                {
                    return (Color32)parsed;
                }

                if (value.Type == DataType.Table)
                {
                    Table t = value.Table;
                    return new Color32(
                        (byte)t.Get("r").CastToNumber(),
                        (byte)t.Get("g").CastToNumber(),
                        (byte)t.Get("b").CastToNumber(),
                        (byte)ReadOptionalTableNumber(t, "a", 255f));
                }
            }

            // Unity object reference (Material, Texture, Transform, GameObject, a Component, …) by the same
            // instance id GetObjectId() hands out, so a mod can WIRE references, not just set value types.
            if (typeof(UnityEngine.Object).IsAssignableFrom(targetType) && value.Type == DataType.Number)
            {
                return ResolveUnityObject((int)value.CastToNumber(), targetType);
            }

            return Convert.ChangeType(value.ToObject(), targetType, CultureInfo.InvariantCulture);
        }

        private static float ReadOptionalTableNumber(Table table, string key, float defaultValue)
        {
            DynValue value = table.Get(key);
            return value.IsNil() ? defaultValue : (float)value.CastToNumber();
        }

        private List<MemberInfo> GetAllowedSettableMembers(Type type)
        {
            List<MemberInfo> candidates = GetSettableMembers(type);
            List<MemberInfo> allowed = new(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                MemberInfo member = candidates[i];
                if (_blacklistPolicy?.IsMemberAllowed(member) ?? true)
                {
                    allowed.Add(member);
                }
            }

            return allowed;
        }

        private List<MemberInfo> GetSettableMembers(Type type)
        {
            BindingFlags flags = MemberFlags();
            (Type, bool) key = (type, _allowNonPublic);
            return SettableMemberCache.GetOrAdd(key, _ =>
            {
                List<MemberInfo> members = new();
                MemberInfo[] allMembers = type.GetMembers(flags);
                for (int i = 0; i < allMembers.Length; i++)
                {
                    MemberInfo member = allMembers[i];
                    if (IsSettableDiscoverableMember(member))
                    {
                        members.Add(member);
                    }
                }

                return members;
            });
        }

        private static bool IsSettableDiscoverableMember(MemberInfo member)
        {
            if (Attribute.IsDefined(member, typeof(ObsoleteAttribute), true) ||
                Attribute.IsDefined(member, typeof(HideInInspector), true))
            {
                return false;
            }

            return member switch
            {
                FieldInfo fi => !fi.IsLiteral && !fi.IsInitOnly,
                PropertyInfo pi => pi.CanWrite && pi.GetIndexParameters().Length == 0,
                _ => false
            };
        }

        private string BuildSettableMemberHint(Type type)
        {
            List<MemberInfo> members = GetAllowedSettableMembers(type);
            if (members.Count == 0)
            {
                return "none";
            }

            int count = Math.Min(members.Count, 12);
            string[] names = new string[count];
            for (int i = 0; i < count; i++)
            {
                names[i] = members[i].Name;
            }

            return string.Join(", ", names);
        }

        private static Type GetMemberValueType(MemberInfo member)
        {
            return member switch
            {
                FieldInfo fi => fi.FieldType,
                PropertyInfo pi => pi.PropertyType,
                _ => typeof(object)
            };
        }

        private static string FriendlyTypeName(Type type)
        {
            if (type == typeof(float) || type == typeof(double))
            {
                return "float";
            }

            if (type == typeof(int) || type == typeof(long) || type == typeof(short) ||
                type == typeof(byte) || type == typeof(uint) || type == typeof(ulong) ||
                type == typeof(ushort) || type == typeof(sbyte))
            {
                return "int";
            }

            if (type == typeof(bool))
            {
                return "bool";
            }

            if (type == typeof(string))
            {
                return "string";
            }

            if (type == typeof(Vector2))
            {
                return "Vector2";
            }

            if (type == typeof(Vector3))
            {
                return "Vector3";
            }

            if (type == typeof(Vector4))
            {
                return "Vector4";
            }

            if (type == typeof(Quaternion))
            {
                return "Quaternion";
            }

            if (type == typeof(Color))
            {
                return "Color";
            }

            if (type.IsEnum)
            {
                return "enum:" + type.Name;
            }

            return type.Name;
        }
    }
}
#endif