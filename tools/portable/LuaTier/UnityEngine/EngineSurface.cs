using System;

namespace UnityEngine
{
    /// <summary>
    /// Compile-time surface of <c>UnityEngine.GameObject</c>. Scene objects need the engine, so it cannot
    /// be constructed and every member refuses with <see cref="PortableEngineUnavailableException"/>.
    /// It exists only because the classic <c>coreai_*</c> gameplay bindings, which the production mod
    /// stack always constructs, name it in signatures and method bodies.
    /// </summary>
    public sealed class GameObject : Object
    {
        public GameObject()
        {
            throw PortableEngine.Unavailable("GameObject..ctor");
        }

        public GameObject(string name)
        {
            throw PortableEngine.Unavailable("GameObject..ctor");
        }

        public Transform transform => throw PortableEngine.Unavailable("GameObject.transform");

        public string tag => throw PortableEngine.Unavailable("GameObject.tag");

        public int layer => throw PortableEngine.Unavailable("GameObject.layer");

        public bool activeSelf => throw PortableEngine.Unavailable("GameObject.activeSelf");

        public bool activeInHierarchy => throw PortableEngine.Unavailable("GameObject.activeInHierarchy");

        public static GameObject Find(string name)
        {
            throw PortableEngine.Unavailable("GameObject.Find");
        }

        public static GameObject CreatePrimitive(PrimitiveType type)
        {
            throw PortableEngine.Unavailable("GameObject.CreatePrimitive");
        }

        public void SetActive(bool value)
        {
            throw PortableEngine.Unavailable("GameObject.SetActive");
        }

        public bool CompareTag(string tag)
        {
            throw PortableEngine.Unavailable("GameObject.CompareTag");
        }

        public Component GetComponent(Type type)
        {
            throw PortableEngine.Unavailable("GameObject.GetComponent");
        }

        public T GetComponent<T>()
        {
            throw PortableEngine.Unavailable("GameObject.GetComponent");
        }

        public T[] GetComponents<T>()
        {
            throw PortableEngine.Unavailable("GameObject.GetComponents");
        }

        public Component AddComponent(Type componentType)
        {
            throw PortableEngine.Unavailable("GameObject.AddComponent");
        }

        public T AddComponent<T>() where T : Component
        {
            throw PortableEngine.Unavailable("GameObject.AddComponent");
        }
    }

    /// <summary>Compile-time surface of <c>UnityEngine.Component</c>; nothing can construct one here.</summary>
    public class Component : Object
    {
        protected Component()
        {
            throw PortableEngine.Unavailable("Component..ctor");
        }

        public GameObject gameObject => throw PortableEngine.Unavailable("Component.gameObject");

        public Transform transform => throw PortableEngine.Unavailable("Component.transform");
    }

    /// <summary>Compile-time surface of <c>UnityEngine.Behaviour</c>; nothing can construct one here.</summary>
    public class Behaviour : Component
    {
        protected Behaviour()
        {
        }

        public bool enabled
        {
            get => throw PortableEngine.Unavailable("Behaviour.enabled");
            set => throw PortableEngine.Unavailable("Behaviour.enabled");
        }
    }

    /// <summary>
    /// Compile-time surface of <c>UnityEngine.MonoBehaviour</c>. Only the engine instantiates components
    /// (through <c>AddComponent</c>, which refuses here), so no MonoBehaviour ever exists in this suite and
    /// no Update/FixedUpdate message is ever sent.
    /// </summary>
    public class MonoBehaviour : Behaviour
    {
        protected MonoBehaviour()
        {
        }
    }

    /// <summary>Compile-time surface of <c>UnityEngine.TextAsset</c>; only the asset database loads one.</summary>
    public class TextAsset : Object
    {
        private TextAsset()
        {
        }

        public string text => throw PortableEngine.Unavailable("TextAsset.text");
    }

    /// <summary>Compile-time surface of <c>UnityEngine.Transform</c>; nothing can construct one here.</summary>
    public class Transform : Component
    {
        protected Transform()
        {
        }

        public Vector3 position
        {
            get => throw PortableEngine.Unavailable("Transform.position");
            set => throw PortableEngine.Unavailable("Transform.position");
        }

        public Quaternion rotation
        {
            get => throw PortableEngine.Unavailable("Transform.rotation");
            set => throw PortableEngine.Unavailable("Transform.rotation");
        }

        public Vector3 eulerAngles
        {
            get => throw PortableEngine.Unavailable("Transform.eulerAngles");
            set => throw PortableEngine.Unavailable("Transform.eulerAngles");
        }

        public Vector3 localScale
        {
            get => throw PortableEngine.Unavailable("Transform.localScale");
            set => throw PortableEngine.Unavailable("Transform.localScale");
        }

        public Transform parent => throw PortableEngine.Unavailable("Transform.parent");

        public int childCount => throw PortableEngine.Unavailable("Transform.childCount");

        public Transform GetChild(int index)
        {
            throw PortableEngine.Unavailable("Transform.GetChild");
        }

        public void SetParent(Transform parent, bool worldPositionStays)
        {
            throw PortableEngine.Unavailable("Transform.SetParent");
        }
    }

    /// <summary>Compile-time surface of <c>UnityEngine.Collider</c>; nothing can construct one here.</summary>
    public class Collider : Component
    {
        protected Collider()
        {
        }
    }

    /// <summary>Compile-time surface of <c>UnityEngine.ScriptableObject</c>; nothing can construct one here.</summary>
    public class ScriptableObject : Object
    {
        protected ScriptableObject()
        {
            throw PortableEngine.Unavailable("ScriptableObject..ctor");
        }
    }

    /// <summary>
    /// Compile-time surface of <c>UnityEngine.Rigidbody</c>; only the refused <c>AddComponent</c> could
    /// create one, so every member refuses.
    /// </summary>
    public sealed class Rigidbody : Component
    {
        private Rigidbody()
        {
        }

        public Vector3 position => throw PortableEngine.Unavailable("Rigidbody.position");

        public Vector3 linearVelocity
        {
            get => throw PortableEngine.Unavailable("Rigidbody.linearVelocity");
            set => throw PortableEngine.Unavailable("Rigidbody.linearVelocity");
        }

        public bool isKinematic => throw PortableEngine.Unavailable("Rigidbody.isKinematic");

        public bool freezeRotation
        {
            get => throw PortableEngine.Unavailable("Rigidbody.freezeRotation");
            set => throw PortableEngine.Unavailable("Rigidbody.freezeRotation");
        }

        public bool useGravity
        {
            get => throw PortableEngine.Unavailable("Rigidbody.useGravity");
            set => throw PortableEngine.Unavailable("Rigidbody.useGravity");
        }
    }

    /// <summary>Unity's primitive mesh kinds, with Unity's numeric values.</summary>
    public enum PrimitiveType
    {
        Sphere = 0,
        Capsule = 1,
        Cylinder = 2,
        Cube = 3,
        Plane = 4,
        Quad = 5
    }

    /// <summary>Unity's trigger-query modes, with Unity's numeric values.</summary>
    public enum QueryTriggerInteraction
    {
        UseGlobal = 0,
        Ignore = 1,
        Collide = 2
    }

    /// <summary>Compile-time surface of <c>UnityEngine.RaycastHit</c>; only a refused raycast could fill one.</summary>
    public struct RaycastHit
    {
        public Vector3 point => throw PortableEngine.Unavailable("RaycastHit.point");

        public float distance => throw PortableEngine.Unavailable("RaycastHit.distance");

        public Collider collider => throw PortableEngine.Unavailable("RaycastHit.collider");
    }

    /// <summary>Compile-time surface of <c>UnityEngine.Physics</c>; physics needs the engine and is refused.</summary>
    public static class Physics
    {
        /// <summary>Unity's value: every layer except Ignore Raycast (layer 2).</summary>
        public const int DefaultRaycastLayers = ~(1 << 2);

        public static Vector3 gravity
        {
            get => throw PortableEngine.Unavailable("Physics.gravity");
            set => throw PortableEngine.Unavailable("Physics.gravity");
        }

        public static bool Raycast(Vector3 origin, Vector3 direction, out RaycastHit hitInfo, float maxDistance)
        {
            throw PortableEngine.Unavailable("Physics.Raycast");
        }

        public static bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, int layerMask,
            QueryTriggerInteraction queryTriggerInteraction)
        {
            throw PortableEngine.Unavailable("Physics.Raycast");
        }

        public static void SyncTransforms()
        {
            throw PortableEngine.Unavailable("Physics.SyncTransforms");
        }
    }

    /// <summary>Compile-time surface of <c>UnityEngine.Resources</c>; asset lookup needs the engine and is refused.</summary>
    public static class Resources
    {
        public static T Load<T>(string path) where T : Object
        {
            throw PortableEngine.Unavailable("Resources.Load");
        }

        public static T[] LoadAll<T>(string path) where T : Object
        {
            throw PortableEngine.Unavailable("Resources.LoadAll");
        }

        public static T[] FindObjectsOfTypeAll<T>() where T : Object
        {
            throw PortableEngine.Unavailable("Resources.FindObjectsOfTypeAll");
        }

        public static Object[] FindObjectsOfTypeAll(Type type)
        {
            throw PortableEngine.Unavailable("Resources.FindObjectsOfTypeAll");
        }
    }

    /// <summary>
    /// Compile-time surface of <c>UnityEngine.JsonUtility</c>. Unity's serializer is native and its exact
    /// output (field selection, float formatting) is not reproduced, so it is refused.
    /// </summary>
    public static class JsonUtility
    {
        public static string ToJson(object obj)
        {
            throw PortableEngine.Unavailable("JsonUtility.ToJson");
        }

        public static string ToJson(object obj, bool prettyPrint)
        {
            throw PortableEngine.Unavailable("JsonUtility.ToJson");
        }

        public static T FromJson<T>(string json)
        {
            throw PortableEngine.Unavailable("JsonUtility.FromJson");
        }
    }

    /// <summary>Compile-time surface of the legacy <c>UnityEngine.Input</c>; device input needs the engine.</summary>
    public static class Input
    {
        public static Vector3 mousePosition => throw PortableEngine.Unavailable("Input.mousePosition");

        public static bool GetKey(KeyCode key)
        {
            throw PortableEngine.Unavailable("Input.GetKey");
        }

        public static bool GetKeyDown(KeyCode key)
        {
            throw PortableEngine.Unavailable("Input.GetKeyDown");
        }

        public static bool GetKeyUp(KeyCode key)
        {
            throw PortableEngine.Unavailable("Input.GetKeyUp");
        }

        public static bool GetMouseButton(int button)
        {
            throw PortableEngine.Unavailable("Input.GetMouseButton");
        }

        public static bool GetMouseButtonDown(int button)
        {
            throw PortableEngine.Unavailable("Input.GetMouseButtonDown");
        }

        public static float GetAxis(string axisName)
        {
            throw PortableEngine.Unavailable("Input.GetAxis");
        }
    }

    /// <summary>
    /// The <c>UnityEngine.KeyCode</c> members the Lua tier names, with Unity's numeric values. Unity's
    /// other key codes are absent, so parsing any other key name yields <c>None</c> here; every consumer
    /// of a parsed key is a refused <see cref="Input"/> call, so no answer depends on it.
    /// </summary>
    public enum KeyCode
    {
        None = 0,
        Alpha0 = 48,
        Alpha1 = 49,
        Alpha2 = 50,
        Alpha3 = 51,
        Alpha4 = 52,
        Alpha5 = 53,
        Alpha6 = 54,
        Alpha7 = 55,
        Alpha8 = 56,
        Alpha9 = 57,
        UpArrow = 273,
        DownArrow = 274,
        RightArrow = 275,
        LeftArrow = 276
    }

    /// <summary>Compile-time surface of <c>UnityEngine.LayerMask</c>; layer names live in the engine.</summary>
    public struct LayerMask
    {
        public static string LayerToName(int layer)
        {
            throw PortableEngine.Unavailable("LayerMask.LayerToName");
        }
    }

    /// <summary>Compile-time surface of Unity 6's <c>UnityEngine.EntityId</c>; only the engine issues one.</summary>
    public struct EntityId
    {
    }

    /// <summary>Inspector metadata only; no editor reads it here.</summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class CreateAssetMenuAttribute : Attribute
    {
        public string menuName { get; set; }

        public string fileName { get; set; }

        public int order { get; set; }
    }
}

namespace UnityEngine.SceneManagement
{
    /// <summary>Compile-time surface of <c>UnityEngine.SceneManagement.Scene</c>; scenes need the engine.</summary>
    public struct Scene
    {
        public string name => throw PortableEngine.Unavailable("Scene.name");

        public bool IsValid()
        {
            throw PortableEngine.Unavailable("Scene.IsValid");
        }

        public GameObject[] GetRootGameObjects()
        {
            throw PortableEngine.Unavailable("Scene.GetRootGameObjects");
        }
    }

    /// <summary>Compile-time surface of <c>UnityEngine.SceneManagement.SceneManager</c>; refused.</summary>
    public static class SceneManager
    {
        public static Scene GetActiveScene()
        {
            throw PortableEngine.Unavailable("SceneManager.GetActiveScene");
        }
    }
}
