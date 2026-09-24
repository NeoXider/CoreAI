using System;

namespace UnityEngine
{
    /// <summary>
    /// Compile-time surface of <c>UnityEngine.Object</c> so signatures such as
    /// <c>IGameLogger.LogInfo(feature, message, Object context = null)</c> compile. No engine object can
    /// exist here (every engine-backed subclass refuses construction), so there is no fake-null to
    /// reproduce; the object-lifetime members refuse with <see cref="PortableEngineUnavailableException"/>.
    /// </summary>
    public class Object
    {
        public string name
        {
            get => throw PortableEngine.Unavailable("Object.name");
            set => throw PortableEngine.Unavailable("Object.name");
        }

        public EntityId GetEntityId()
        {
            throw PortableEngine.Unavailable("Object.GetEntityId");
        }

        public int GetInstanceID()
        {
            throw PortableEngine.Unavailable("Object.GetInstanceID");
        }

        public static void Destroy(Object obj)
        {
            throw PortableEngine.Unavailable("Object.Destroy");
        }

        public static void DestroyImmediate(Object obj)
        {
            throw PortableEngine.Unavailable("Object.DestroyImmediate");
        }
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SerializeField : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class HideInInspector : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Field, AllowMultiple = true)]
    public sealed class TooltipAttribute : Attribute
    {
        public TooltipAttribute(string tooltip)
        {
            this.tooltip = tooltip;
        }

        public readonly string tooltip;
    }

    [AttributeUsage(AttributeTargets.Field, AllowMultiple = true)]
    public sealed class HeaderAttribute : Attribute
    {
        public HeaderAttribute(string header)
        {
            this.header = header;
        }

        public readonly string header;
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class MinAttribute : Attribute
    {
        public MinAttribute(float min)
        {
            this.min = min;
        }

        public readonly float min;
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class RangeAttribute : Attribute
    {
        public RangeAttribute(float min, float max)
        {
            this.min = min;
            this.max = max;
        }

        public readonly float min;

        public readonly float max;
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class TextAreaAttribute : Attribute
    {
        public TextAreaAttribute()
        {
        }

        public TextAreaAttribute(int minLines, int maxLines)
        {
        }
    }

    /// <summary>When a <see cref="RuntimeInitializeOnLoadMethodAttribute"/> method runs in a player.</summary>
    public enum RuntimeInitializeLoadType
    {
        AfterSceneLoad = 0,
        BeforeSceneLoad = 1,
        AfterAssembliesLoaded = 2,
        BeforeSplashScreen = 3,
        SubsystemRegistration = 4
    }

    /// <summary>
    /// Marker only. Unity invokes such methods at player start-up; nothing invokes them here, which
    /// matches an EditMode test run (the editor does not run them before EditMode tests either).
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class RuntimeInitializeOnLoadMethodAttribute : Attribute
    {
        public RuntimeInitializeOnLoadMethodAttribute()
        {
        }

        public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType loadType)
        {
        }
    }
}
