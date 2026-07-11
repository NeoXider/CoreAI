using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Lua;
using CoreAI.Sandbox.LuaCs;
using Lua;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// Lua-CSharp counterpart of <c>CoreAiFullUnityLuaRuntimeBindings</c>.
    /// </summary>
    public sealed class LuaCsFullUnityRuntimeBindings
    {
        private static readonly ConcurrentDictionary<string, Type> TypeCache = new(StringComparer.Ordinal);

        private static readonly ConcurrentDictionary<(Type type, string member, bool nonPublic), MemberInfo>
            MemberCache = new();

        private static readonly ConcurrentDictionary<(Type type, bool nonPublic), List<MemberInfo>>
            SettableMemberCache = new();

        // instanceId -> GameObject, memoized across unity_* calls; a full Resources.FindObjectsOfTypeAll scan
        // is only re-run when a lookup misses (and even then, at most once per MissedIdRescanIntervalSeconds
        // for a given id) so an N-object-per-tick mod costs ~1 scan instead of N.
        private static readonly ConcurrentDictionary<int, GameObject> GameObjectResolveCache = new();
        private static readonly ConcurrentDictionary<int, float> GameObjectMissedScanTime = new();

        private static readonly ConcurrentDictionary<Type, ConcurrentDictionary<int, UnityEngine.Object>>
            UnityObjectResolveCacheByType = new();

        private static readonly ConcurrentDictionary<(Type Type, int ObjectId), float>
            UnityObjectMissedScanTime = new();

        private const float MissedIdRescanIntervalSeconds = 1f;

        private readonly IGameLogger _logger;
        private readonly bool _allowNonPublic;
        private readonly IFullLuaAccessBlacklistPolicy _blacklistPolicy;

        public LuaCsFullUnityRuntimeBindings(
            IGameLogger logger = null,
            bool allowNonPublicMembers = false,
            IFullLuaAccessBlacklistPolicy blacklistPolicy = null)
        {
            _logger = logger;
            _allowNonPublic = allowNonPublicMembers;
            _blacklistPolicy = blacklistPolicy ?? AllowAllFullLuaAccessBlacklistPolicy.Instance;

            if (blacklistPolicy == null)
            {
                _logger?.LogWarning(GameLogFeature.MessagePipe,
                    "[FullLua] No IFullLuaAccessBlacklistPolicy supplied; Full-tier reflection is unrestricted " +
                    "(allow-all). Provide a deny-list policy to restrict which components/members scripts may reach.");
            }
        }

        public void Register(LuaCsApiRegistry registry, LuaCapabilities capabilities)
        {
            if ((capabilities & LuaCapabilities.Full) == 0)
            {
                return;
            }

            RegisterGameplayApis(registry);
        }

        public void RegisterGameplayApis(LuaCsApiRegistry registry)
        {
            registry.Register("unity_find", new Func<string, int>(FindByName));
            registry.Register("unity_id", new Func<string, int>(FindByName));
            registry.RegisterCallback("unity_list_objects", (ctx, ct) =>
                new ValueTask<int>(ctx.Return(ToLuaValue(ListObjects(ReadOptionalMax(ctx, 0))))));
            registry.RegisterCallback("unity_find_all", (ctx, ct) =>
                new ValueTask<int>(ctx.Return(ToLuaValue(FindAll(
                    ReadString(ctx, 0, "unity_find_all"),
                    ReadOptionalMax(ctx, 1))))));
            registry.RegisterCallback("unity_find_by_tag", (ctx, ct) =>
                new ValueTask<int>(ctx.Return(ToLuaValue(FindByTag(
                    ReadString(ctx, 0, "unity_find_by_tag"),
                    ReadOptionalMax(ctx, 1))))));
            registry.RegisterCallback("unity_find_by_component", (ctx, ct) =>
                new ValueTask<int>(ctx.Return(ToLuaValue(FindByComponent(
                    ReadString(ctx, 0, "unity_find_by_component"),
                    ReadOptionalMax(ctx, 1))))));
            registry.Register("unity_describe_object", new Func<int, LuaValue>(instanceId =>
                ToLuaValue(DescribeObject(instanceId))));
            registry.Register("unity_set_active", new Func<int, bool, bool>(SetActive));
            registry.Register("unity_get_position", new Func<int, LuaTable>(GetPosition));
            registry.Register("unity_set_position", new Func<int, double, double, double, bool>(SetPosition));
            registry.Register("unity_get_transform", new Func<int, LuaValue>(instanceId =>
                ToLuaValue(GetTransform(instanceId))));
            registry.Register("unity_set_rotation_euler",
                new Func<int, double, double, double, bool>(SetRotationEuler));
            registry.Register("unity_set_scale", new Func<int, double, double, double, bool>(SetScale));
            registry.Register("unity_parent", new Func<int, int, bool, bool>(SetParent));
            registry.Register("unity_get_children", new Func<int, LuaValue>(instanceId =>
                ToLuaValue(GetChildren(instanceId))));
            registry.Register("unity_list_components", new Func<int, List<string>>(ListComponents));
            registry.RegisterCallback("unity_list_members", (ctx, ct) =>
                new ValueTask<int>(ctx.Return(ToLuaValue(ListMembers(
                    ReadInt(ctx, 0, "unity_list_members"),
                    ReadString(ctx, 1, "unity_list_members"))))));
            registry.Register("unity_get_member", new Func<int, string, string, LuaValue>(GetMember));
            registry.Register("unity_set_member", new Func<int, string, string, LuaValue, bool>(SetMember));
            registry.RegisterCallback("unity_call", (ctx, ct) =>
            {
                int instanceId = ReadInt(ctx, 0, "unity_call");
                string componentType = ReadString(ctx, 1, "unity_call");
                string methodName = ReadString(ctx, 2, "unity_call");
                int count = Math.Max(0, ctx.ArgumentCount - 3);
                LuaValue[] args = new LuaValue[count];
                for (int i = 0; i < count; i++)
                {
                    args[i] = ctx.GetArgument(i + 3);
                }

                return new ValueTask<int>(ctx.Return(CallMethod(instanceId, componentType, methodName, args)));
            });
            registry.Register("unity_add_component", new Func<int, string, bool>(AddComponent));
            registry.Register("unity_destroy", new Func<int, bool>(DestroyObject));
        }

        private BindingFlags MemberFlags()
        {
            return BindingFlags.Instance | BindingFlags.Public |
                   (_allowNonPublic ? BindingFlags.NonPublic : BindingFlags.Default);
        }

        private static int ReadOptionalMax(LuaFunctionExecutionContext ctx, int index)
        {
            if (ctx == null || !ctx.HasArgument(index) || ctx.GetArgument(index).Type == LuaValueType.Nil)
            {
                return 100;
            }

            return (int)ctx.GetArgument(index).Read<double>();
        }

        private static string ReadString(LuaFunctionExecutionContext ctx, int index, string apiName)
        {
            if (ctx == null || !ctx.HasArgument(index) || ctx.GetArgument(index).Type != LuaValueType.String)
            {
                throw new ArgumentException($"{apiName}: argument {index + 1} must be a string.");
            }

            return ctx.GetArgument(index).Read<string>();
        }

        private static int ReadInt(LuaFunctionExecutionContext ctx, int index, string apiName)
        {
            if (ctx == null || !ctx.HasArgument(index) || ctx.GetArgument(index).Type != LuaValueType.Number)
            {
                throw new ArgumentException($"{apiName}: argument {index + 1} must be a number.");
            }

            return (int)ctx.GetArgument(index).Read<double>();
        }

        private static LuaValue ToLuaValue(object value)
        {
            if (value == null)
            {
                return LuaValue.Nil;
            }

            switch (value)
            {
                case LuaValue lua:
                    return lua;
                case LuaTable table:
                    return new LuaValue(table);
                case bool b:
                    return new LuaValue(b);
                case string s:
                    return new LuaValue(s);
                case int i:
                    return new LuaValue((double)i);
                case long l:
                    return new LuaValue((double)l);
                case float f:
                    return new LuaValue((double)f);
                case double d:
                    return new LuaValue(d);
                case IDictionary<string, object> dict:
                {
                    LuaTable table = new();
                    foreach (KeyValuePair<string, object> kv in dict)
                    {
                        table[kv.Key] = ToLuaValue(kv.Value);
                    }

                    return new LuaValue(table);
                }
                case IEnumerable<string> strings:
                {
                    LuaTable table = new();
                    int index = 1;
                    foreach (string item in strings)
                    {
                        table[new LuaValue((double)index++)] = new LuaValue(item);
                    }

                    return new LuaValue(table);
                }
                case IEnumerable<object> list:
                {
                    LuaTable table = new();
                    int index = 1;
                    foreach (object item in list)
                    {
                        table[new LuaValue((double)index++)] = ToLuaValue(item);
                    }

                    return new LuaValue(table);
                }
                default:
                    return new LuaValue(value.ToString());
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

        private List<object> FindByComponent(string componentType)
        {
            return FindByComponent(componentType, 100);
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
                throw new ArgumentException(
                    $"unity_add_component: '{componentType}' is not a resolvable Component type.");
            }

            if (!IsTypeAllowed(type))
            {
                throw new InvalidOperationException(
                    $"Full Lua access to type '{type.Name}' is denied by host policy.");
            }

            go.AddComponent(type);
            return true;
        }

        private static bool DestroyObject(int instanceId)
        {
            GameObject go = Resolve(instanceId);
            if (go == null)
            {
                return false;
            }

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

        private static LuaTable GetPosition(int instanceId)
        {
            GameObject go = Resolve(instanceId);
            if (go == null)
            {
                throw new InvalidOperationException($"unity_get_position: object id {instanceId} not found.");
            }

            Vector3 p = go.transform.position;
            LuaTable t = new();
            t["x"] = new LuaValue((double)p.x);
            t["y"] = new LuaValue((double)p.y);
            t["z"] = new LuaValue((double)p.z);
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
                children.Add(BuildObjectSummary(t.GetChild(i).gameObject, true, false));
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
                throw new InvalidOperationException(
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

        private LuaValue GetMember(int instanceId, string componentType, string memberName)
        {
            object target = ResolveComponent(instanceId, componentType);
            if (target == null)
            {
                throw new InvalidOperationException(
                    $"unity_get_member: component '{componentType}' on id {instanceId} not found.");
            }

            MemberInfo member = ResolveMember(target.GetType(), memberName);
            object value = member switch
            {
                FieldInfo fi => fi.GetValue(target),
                PropertyInfo pi => pi.GetValue(target),
                _ => throw new InvalidOperationException($"unity_get_member: '{memberName}' is not readable.")
            };

            return ToLuaValue(value);
        }

        private bool SetMember(int instanceId, string componentType, string memberName, LuaValue value)
        {
            object target = ResolveComponent(instanceId, componentType);
            if (target == null)
            {
                return false;
            }

            MemberInfo member = ResolveMember(target.GetType(), memberName);
            object converted = FromLuaValue(value, member);
            switch (member)
            {
                case FieldInfo fi:
                    fi.SetValue(target, converted);
                    return true;
                case PropertyInfo pi when pi.CanWrite:
                    pi.SetValue(target, converted);
                    return true;
                default:
                    throw new InvalidOperationException($"unity_set_member: '{memberName}' is not writable.");
            }
        }

        private LuaValue CallMethod(int instanceId, string componentType, string methodName, LuaValue[] args)
        {
            object target = ResolveComponent(instanceId, componentType);
            if (target == null)
            {
                throw new InvalidOperationException(
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
                throw new InvalidOperationException(
                    $"unity_call: method '{methodName}' is ambiguous on {type.Name} (overloaded); call is not supported.");
            }

            if (method == null)
            {
                throw new InvalidOperationException($"unity_call: method '{methodName}' not found on {type.Name}.");
            }

            EnsureMemberAllowed(method);

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != (args?.Length ?? 0))
            {
                throw new InvalidOperationException(
                    $"unity_call: '{methodName}' expects {parameters.Length} args, got {args?.Length ?? 0}.");
            }

            object[] invokeArgs = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                invokeArgs[i] = ConvertArg(args[i], parameters[i].ParameterType);
            }

            object result = method.Invoke(target, invokeArgs);
            return ToLuaValue(result);
        }

        private static GameObject Resolve(int instanceId)
        {
            if (instanceId == 0)
            {
                return null;
            }

            if (GameObjectResolveCache.TryGetValue(instanceId, out GameObject cached) && cached != null)
            {
                return cached;
            }

            float now = Time.realtimeSinceStartup;
            if (GameObjectMissedScanTime.TryGetValue(instanceId, out float lastMiss)
                && now - lastMiss < MissedIdRescanIntervalSeconds)
            {
                return null;
            }

            GameObjectResolveCache.Clear();
            GameObject[] loaded = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int i = 0; i < loaded.Length; i++)
            {
                GameObjectResolveCache[GetObjectId(loaded[i])] = loaded[i];
            }

            if (GameObjectResolveCache.TryGetValue(instanceId, out cached))
            {
                return cached;
            }

            GameObjectMissedScanTime[instanceId] = now;
            return null;
        }

        private static UnityEngine.Object ResolveUnityObject(int objectId, Type wanted)
        {
            if (objectId == 0)
            {
                return null;
            }

            ConcurrentDictionary<int, UnityEngine.Object> cache =
                UnityObjectResolveCacheByType.GetOrAdd(wanted,
                    static _ => new ConcurrentDictionary<int, UnityEngine.Object>());
            if (cache.TryGetValue(objectId, out UnityEngine.Object cached) && cached != null)
            {
                return cached;
            }

            (Type Type, int ObjectId) missKey = (wanted, objectId);
            float now = Time.realtimeSinceStartup;
            if (UnityObjectMissedScanTime.TryGetValue(missKey, out float lastMiss)
                && now - lastMiss < MissedIdRescanIntervalSeconds)
            {
                return null;
            }

            cache.Clear();
            UnityEngine.Object[] loaded = Resources.FindObjectsOfTypeAll(wanted);
            for (int i = 0; i < loaded.Length; i++)
            {
                cache[GetObjectId(loaded[i])] = loaded[i];
            }

            if (cache.TryGetValue(objectId, out cached))
            {
                return cached;
            }

            UnityObjectMissedScanTime[missKey] = now;
            return null;
        }

        private static Vector3 ReadVector3(LuaTable t)
        {
            if (t == null)
            {
                return Vector3.zero;
            }

            return new Vector3(
                (float)ReadRequiredTableNumber(t, "x"),
                (float)ReadRequiredTableNumber(t, "y"),
                (float)ReadRequiredTableNumber(t, "z"));
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
                throw new InvalidOperationException($"Full Lua access to type '{type.Name}' is denied by host policy.");
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
                throw new InvalidOperationException(
                    $"unity_call: member '{memberName}' is ambiguous on {type.Name} (overloaded); call is not supported.");
            }

            if (member == null)
            {
                throw new InvalidOperationException(
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

            Type memberType = member switch
            {
                MethodInfo mi => mi.ReturnType,
                PropertyInfo pi => pi.PropertyType,
                FieldInfo fi => fi.FieldType,
                _ => null
            };
            if (IsHardDeniedType(member.DeclaringType) || IsHardDeniedType(memberType))
            {
                throw new InvalidOperationException(
                    $"Full Lua access to member '{member.DeclaringType?.Name}.{member.Name}' is denied " +
                    "(dangerous type; blocked by the Full Lua hard deny-list).");
            }

            if (!(_blacklistPolicy?.IsMemberAllowed(member) ?? true))
            {
                throw new InvalidOperationException(
                    $"Full Lua access to member '{member.DeclaringType?.Name}.{member.Name}' is denied by host policy.");
            }
        }

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

        private static object FromLuaValue(LuaValue value, MemberInfo member)
        {
            Type targetType = member switch
            {
                FieldInfo fi => fi.FieldType,
                PropertyInfo pi => pi.PropertyType,
                _ => typeof(object)
            };
            return ConvertArg(value, targetType);
        }

        private static object ConvertArg(LuaValue value, Type targetType)
        {
            if (value.Type == LuaValueType.Nil)
            {
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
            }

            if (targetType == typeof(string))
            {
                return ReadStringValue(value);
            }

            if (targetType == typeof(bool))
            {
                return value.Type == LuaValueType.Boolean ? value.Read<bool>() : value.Read<double>() != 0d;
            }

            if (targetType == typeof(int))
            {
                return (int)ReadNumber(value);
            }

            if (targetType == typeof(float))
            {
                return (float)ReadNumber(value);
            }

            if (targetType == typeof(double))
            {
                return ReadNumber(value);
            }

            if (targetType == typeof(long))
            {
                return (long)ReadNumber(value);
            }

            if (targetType == typeof(uint))
            {
                return (uint)ReadNumber(value);
            }

            if (targetType == typeof(ulong))
            {
                return (ulong)ReadNumber(value);
            }

            if (targetType == typeof(short))
            {
                return (short)ReadNumber(value);
            }

            if (targetType == typeof(ushort))
            {
                return (ushort)ReadNumber(value);
            }

            if (targetType == typeof(byte))
            {
                return (byte)ReadNumber(value);
            }

            if (targetType == typeof(sbyte))
            {
                return (sbyte)ReadNumber(value);
            }

            if (targetType.IsEnum && value.Type == LuaValueType.String)
            {
                return Enum.Parse(targetType, value.Read<string>(), true);
            }

            if (targetType.IsEnum && value.Type == LuaValueType.Number)
            {
                return Enum.ToObject(targetType, (long)ReadNumber(value));
            }

            if (targetType == typeof(Vector3) && value.Type == LuaValueType.Table)
            {
                LuaTable t = value.Read<LuaTable>();
                return new Vector3(
                    (float)ReadRequiredTableNumber(t, "x"),
                    (float)ReadRequiredTableNumber(t, "y"),
                    (float)ReadRequiredTableNumber(t, "z"));
            }

            if (targetType == typeof(Vector2) && value.Type == LuaValueType.Table)
            {
                LuaTable t = value.Read<LuaTable>();
                return new Vector2(
                    (float)ReadRequiredTableNumber(t, "x"),
                    (float)ReadRequiredTableNumber(t, "y"));
            }

            if (targetType == typeof(Vector4) && value.Type == LuaValueType.Table)
            {
                LuaTable t = value.Read<LuaTable>();
                return new Vector4(
                    (float)ReadRequiredTableNumber(t, "x"),
                    (float)ReadRequiredTableNumber(t, "y"),
                    (float)ReadRequiredTableNumber(t, "z"),
                    (float)ReadRequiredTableNumber(t, "w"));
            }

            if (targetType == typeof(Color))
            {
                if (value.Type == LuaValueType.String)
                {
                    string text = value.Read<string>();
                    if (ColorUtility.TryParseHtmlString(text, out Color color))
                    {
                        return color;
                    }

                    throw new InvalidOperationException($"Could not parse Color from '{text}'.");
                }

                if (value.Type == LuaValueType.Table)
                {
                    LuaTable t = value.Read<LuaTable>();
                    return new Color(
                        (float)ReadRequiredTableNumber(t, "r"),
                        (float)ReadRequiredTableNumber(t, "g"),
                        (float)ReadRequiredTableNumber(t, "b"),
                        ReadOptionalTableNumber(t, "a", 1f));
                }
            }

            if (targetType == typeof(Quaternion) && value.Type == LuaValueType.Table)
            {
                LuaTable t = value.Read<LuaTable>();
                LuaValue w = t["w"];
                if (w.Type != LuaValueType.Nil)
                {
                    return new Quaternion(
                        (float)ReadRequiredTableNumber(t, "x"),
                        (float)ReadRequiredTableNumber(t, "y"),
                        (float)ReadRequiredTableNumber(t, "z"),
                        (float)ReadNumber(w));
                }

                return Quaternion.Euler(
                    (float)ReadRequiredTableNumber(t, "x"),
                    (float)ReadRequiredTableNumber(t, "y"),
                    (float)ReadRequiredTableNumber(t, "z"));
            }

            if (targetType == typeof(Rect) && value.Type == LuaValueType.Table)
            {
                LuaTable t = value.Read<LuaTable>();
                return new Rect(
                    (float)ReadRequiredTableNumber(t, "x"),
                    (float)ReadRequiredTableNumber(t, "y"),
                    (float)ReadRequiredTableNumber(t, "width"),
                    (float)ReadRequiredTableNumber(t, "height"));
            }

            if (targetType == typeof(Bounds) && value.Type == LuaValueType.Table)
            {
                LuaTable t = value.Read<LuaTable>();
                Vector3 center = ReadVector3(ReadRequiredTable(t, "center"));
                Vector3 size = ReadVector3(ReadRequiredTable(t, "size"));
                return new Bounds(center, size);
            }

            if (targetType == typeof(Color32))
            {
                if (value.Type == LuaValueType.String &&
                    ColorUtility.TryParseHtmlString(value.Read<string>(), out Color parsed))
                {
                    return (Color32)parsed;
                }

                if (value.Type == LuaValueType.Table)
                {
                    LuaTable t = value.Read<LuaTable>();
                    return new Color32(
                        (byte)ReadRequiredTableNumber(t, "r"),
                        (byte)ReadRequiredTableNumber(t, "g"),
                        (byte)ReadRequiredTableNumber(t, "b"),
                        (byte)ReadOptionalTableNumber(t, "a", 255f));
                }
            }

            if (typeof(UnityEngine.Object).IsAssignableFrom(targetType) && value.Type == LuaValueType.Number)
            {
                return ResolveUnityObject((int)ReadNumber(value), targetType);
            }

            object obj = value.Read<object>();
            return obj == null || targetType.IsInstanceOfType(obj)
                ? obj
                : Convert.ChangeType(obj, targetType, CultureInfo.InvariantCulture);
        }

        private static double ReadNumber(LuaValue value)
        {
            if (value.Type != LuaValueType.Number)
            {
                throw new ArgumentException($"value must be a number, got {value.Type}.");
            }

            return value.Read<double>();
        }

        private static string ReadStringValue(LuaValue value)
        {
            return value.Type switch
            {
                LuaValueType.String => value.Read<string>(),
                LuaValueType.Number => value.Read<double>().ToString(CultureInfo.InvariantCulture),
                LuaValueType.Boolean => value.Read<bool>() ? "true" : "false",
                LuaValueType.Nil => "",
                _ => value.ToString()
            };
        }

        private static LuaTable ReadRequiredTable(LuaTable table, string key)
        {
            LuaValue value = table[key];
            if (value.Type != LuaValueType.Table)
            {
                throw new ArgumentException($"'{key}' must be a table.");
            }

            return value.Read<LuaTable>();
        }

        private static double ReadRequiredTableNumber(LuaTable table, string key)
        {
            LuaValue value = table[key];
            if (value.Type != LuaValueType.Number)
            {
                throw new ArgumentException($"'{key}' must be a number.");
            }

            return value.Read<double>();
        }

        private static float ReadOptionalTableNumber(LuaTable table, string key, float defaultValue)
        {
            LuaValue value = table[key];
            return value.Type == LuaValueType.Nil ? defaultValue : (float)ReadNumber(value);
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
