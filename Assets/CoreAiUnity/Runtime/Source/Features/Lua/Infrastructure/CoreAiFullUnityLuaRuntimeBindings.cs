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

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// Full-tier Lua bindings: reflection access to live <see cref="GameObject"/>s and components.
    /// Registered only when <see cref="LuaCapabilities.Full"/> is granted. Policy is allow-all
    /// (no type blacklist yet — see <c>LUA_ACCESS_MODES_AUDIT_RU.md</c> Planned section).
    /// </summary>
    public sealed class CoreAiFullUnityLuaRuntimeBindings : IGameLuaRuntimeBindings
    {
        private static readonly ConcurrentDictionary<string, Type> TypeCache = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<(Type type, string member), MemberInfo> MemberCache = new();

        private readonly IGameLogger _logger;

        public CoreAiFullUnityLuaRuntimeBindings(IGameLogger logger = null)
        {
            _logger = logger;
        }

        public void RegisterGameplayApis(LuaApiRegistry registry)
        {
            registry.Register("unity_find", new Func<string, int>(FindByName));
            registry.Register("unity_id", new Func<string, int>(FindByName));
            registry.Register("unity_set_active", new Func<int, bool, bool>(SetActive));
            registry.Register("unity_get_position", new Func<int, Table>(GetPosition));
            registry.Register("unity_set_position", new Func<int, double, double, double, bool>(SetPosition));
            registry.Register("unity_list_components", new Func<int, List<string>>(ListComponents));
            registry.Register("unity_get_member", new Func<int, string, string, DynValue>(GetMember));
            registry.Register("unity_set_member", new Func<int, string, string, DynValue, bool>(SetMember));
            registry.Register("unity_call", new Func<int, string, string, DynValue[], DynValue>(CallMethod));
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

        private static List<string> ListComponents(int instanceId)
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
                if (c != null)
                {
                    names.Add(c.GetType().Name);
                }
            }

            return names;
        }

        private static DynValue GetMember(int instanceId, string componentType, string memberName)
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

        private static bool SetMember(int instanceId, string componentType, string memberName, DynValue value)
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

        private static DynValue CallMethod(int instanceId, string componentType, string methodName, DynValue[] args)
        {
            object target = ResolveComponent(instanceId, componentType);
            if (target == null)
            {
                throw new ScriptRuntimeException(
                    $"unity_call: component '{componentType}' on id {instanceId} not found.");
            }

            Type type = target.GetType();
            MethodInfo method = type.GetMethod(methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
            {
                throw new ScriptRuntimeException($"unity_call: method '{methodName}' not found on {type.Name}.");
            }

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

        private static object ResolveComponent(int instanceId, string componentTypeName)
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

        private static MemberInfo ResolveMember(Type type, string memberName)
        {
            (Type, string) key = (type, memberName);
            if (MemberCache.TryGetValue(key, out MemberInfo cached))
            {
                return cached;
            }

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            MemberInfo member = type.GetField(memberName, flags) as MemberInfo ??
                                type.GetProperty(memberName, flags);
            if (member == null)
            {
                throw new ScriptRuntimeException($"Member '{memberName}' not found on {type.Name}.");
            }

            MemberCache[key] = member;
            return member;
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
                return Enum.Parse(targetType, value.String, ignoreCase: true);
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
