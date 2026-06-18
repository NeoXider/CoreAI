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
        private static readonly ConcurrentDictionary<(Type type, string member, bool nonPublic), MemberInfo> MemberCache = new();

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
            registry.Register("unity_set_rotation_euler", new Func<int, double, double, double, bool>(SetRotationEuler));
            registry.Register("unity_set_scale", new Func<int, double, double, double, bool>(SetScale));
            registry.Register("unity_parent", new Func<int, int, bool, bool>(SetParent));
            registry.Register("unity_get_children", new Func<int, List<object>>(GetChildren));
            registry.Register("unity_list_components", new Func<int, List<string>>(ListComponents));
            registry.Register("unity_get_member", new Func<int, string, string, DynValue>(GetMember));
            registry.Register("unity_set_member", new Func<int, string, string, DynValue, bool>(SetMember));
            registry.Register("unity_call", new Func<int, string, string, DynValue[], DynValue>(CallMethod));
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
            return go != null ? go.GetInstanceID() : 0;
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
            return go == null ? null : BuildObjectSummary(go, includeTransform: true, includeComponents: true);
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
                children.Add(BuildObjectSummary(t.GetChild(i).gameObject, includeTransform: true,
                    includeComponents: false));
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
            MethodInfo method = type.GetMethod(methodName, MemberFlags());
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

            UnityEngine.Object obj = Resources.InstanceIDToObject(instanceId);
            if (obj is GameObject go)
            {
                return go;
            }

            if (obj is Component comp)
            {
                return comp.gameObject;
            }

            return null;
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
                results.Add(BuildObjectSummary(go, includeTransform: false, includeComponents: false));
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
                { "id", go.GetInstanceID() },
                { "name", go.name },
                { "path", GetPath(go) },
                { "tag", go.tag },
                { "layer", LayerMask.LayerToName(go.layer) },
                { "layer_index", go.layer },
                { "active", go.activeSelf },
                { "active_in_hierarchy", go.activeInHierarchy },
                { "parent_id", go.transform.parent != null ? go.transform.parent.gameObject.GetInstanceID() : 0 },
                { "parent", go.transform.parent != null ? go.transform.parent.gameObject.name : "" },
                { "child_count", go.transform.childCount }
            };

            if (includeTransform)
            {
                summary["transform"] = BuildTransformSummary(go.transform);
            }

            if (includeComponents)
            {
                summary["components"] = ListComponents(go.GetInstanceID());
            }

            return summary;
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
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    t = asm.GetType(name);
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
            MemberInfo member = type.GetField(memberName, flags) as MemberInfo ??
                                type.GetProperty(memberName, flags);
            if (member == null)
            {
                throw new ScriptRuntimeException($"Member '{memberName}' not found on {type.Name}.");
            }

            EnsureMemberAllowed(member);
            MemberCache[key] = member;
            return member;
        }

        private bool IsTypeAllowed(Type type)
        {
            return type != null && (_blacklistPolicy?.IsTypeAllowed(type) ?? true);
        }

        private void EnsureMemberAllowed(MemberInfo member)
        {
            if (member == null)
            {
                return;
            }

            if (!(_blacklistPolicy?.IsMemberAllowed(member) ?? true))
            {
                throw new ScriptRuntimeException(
                    $"Full Lua access to member '{member.DeclaringType?.Name}.{member.Name}' is denied by host policy.");
            }
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
                Vector3 v => DynValue.NewString($"({v.x},{v.y},{v.z})"),
                _ => DynValue.NewString(value.ToString())
            };
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

            if (targetType.IsEnum && value.Type == DataType.String)
            {
                return Enum.Parse(targetType, value.String, true);
            }

            if (targetType == typeof(Vector3) && value.Type == DataType.Table)
            {
                Table t = value.Table;
                return new Vector3(
                    (float)t.Get("x").CastToNumber(),
                    (float)t.Get("y").CastToNumber(),
                    (float)t.Get("z").CastToNumber());
            }

            return Convert.ChangeType(value.ToObject(), targetType, CultureInfo.InvariantCulture);
        }
    }
}
#endif
