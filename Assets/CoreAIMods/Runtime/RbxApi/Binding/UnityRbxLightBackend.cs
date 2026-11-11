using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Spatial;
using UnityEngine;

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// The default <see cref="IRbxLightBackend"/>: a Unity point <c>Light</c> on a child object of the host
    /// part's GameObject, at the part's centre, with the range converted from studs through RbxSpace. The
    /// child moves, hides and dies with the part's GameObject; the binder never touches it.
    /// </summary>
    public sealed class UnityRbxLightBackend : IRbxLightBackend, IDisposable
    {
        /// <summary>Suffix of the child object that carries a drawn light.</summary>
        public const string LightObjectSuffix = " (light)";

        private readonly Func<InstanceId, GameObject> _resolvePart;
        private readonly Func<InstanceId, string> _resolveName;
        private readonly Dictionary<InstanceId, Entry> _entries = new();

        /// <param name="resolvePart">The host part's GameObject, or null while it has none.</param>
        /// <param name="resolveName">The light's name for the child object; null names it "PointLight".</param>
        public UnityRbxLightBackend(Func<InstanceId, GameObject> resolvePart,
            Func<InstanceId, string> resolveName = null)
        {
            _resolvePart = resolvePart ?? throw new ArgumentNullException(nameof(resolvePart));
            _resolveName = resolveName;
        }

        /// <summary>Over the binder: a part's GameObject is the one it bound for the part's instance.</summary>
        public UnityRbxLightBackend(InstanceGameObjectBinder binder, InstanceRegistry registry = null)
            : this(id => binder != null && binder.TryGetBoundObject(id, out GameObject bound) ? bound : null,
                id => registry != null && registry.TryGetRecord(id, out InstanceRecord record)
                    ? record.Instance.Name
                    : null)
        {
        }

        /// <summary>Scales <c>Brightness</c> into the Unity intensity (1 keeps them equal).</summary>
        public float IntensityScale { get; set; } = 1f;

        /// <summary>Lights this backend currently draws.</summary>
        public int Count => _entries.Count;

        /// <summary>The Unity light drawn for <paramref name="lightId"/>, or null (tests and diagnostics).</summary>
        public Light GetLight(InstanceId lightId)
        {
            return _entries.TryGetValue(lightId, out Entry entry) && entry.Light != null ? entry.Light : null;
        }

        /// <inheritdoc />
        public void Apply(InstanceId lightId, InstanceId hostPartId, in RbxLightRenderState state)
        {
            GameObject part = _resolvePart(hostPartId);
            if (part == null)
            {
                Release(lightId);
                return;
            }

            if (!_entries.TryGetValue(lightId, out Entry entry) || entry.Light == null)
            {
                if (entry != null)
                {
                    DestroyOwned(entry.Holder);
                }

                entry = Create(lightId);
                _entries[lightId] = entry;
            }

            Transform holder = entry.Holder.transform;
            if (holder.parent != part.transform)
            {
                holder.SetParent(part.transform, false);
                holder.localPosition = Vector3.zero;
                holder.localRotation = Quaternion.identity;
            }

            Light light = entry.Light;
            light.color = new Color(state.Color.R, state.Color.G, state.Color.B, 1f);
            light.intensity = Mathf.Max(0f, state.Brightness) * IntensityScale;
            light.range = RbxSpace.LengthToUnity(state.RangeStuds);
            light.shadows = state.Shadows ? LightShadows.Soft : LightShadows.None;
            light.enabled = state.Active;
        }

        /// <inheritdoc />
        public void Release(InstanceId lightId)
        {
            if (!_entries.TryGetValue(lightId, out Entry entry))
            {
                return;
            }

            _entries.Remove(lightId);
            DestroyOwned(entry.Holder);
        }

        /// <inheritdoc />
        public bool Holds(InstanceId lightId)
        {
            return _entries.TryGetValue(lightId, out Entry entry) && entry.Light != null;
        }

        /// <summary>Destroys every drawn light.</summary>
        public void Dispose()
        {
            foreach (KeyValuePair<InstanceId, Entry> pair in _entries)
            {
                DestroyOwned(pair.Value.Holder);
            }

            _entries.Clear();
        }

        private Entry Create(InstanceId lightId)
        {
            string name = _resolveName?.Invoke(lightId) ?? "PointLight";
            GameObject holder = new(name + LightObjectSuffix);
            Light light = holder.AddComponent<Light>();
            light.type = LightType.Point;
            return new Entry { Holder = holder, Light = light };
        }

        private static void DestroyOwned(GameObject holder)
        {
            if (holder == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(holder);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(holder);
            }
        }

        private sealed class Entry
        {
            public GameObject Holder;
            public Light Light;
        }
    }
}
