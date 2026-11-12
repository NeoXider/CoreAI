using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Spatial;
using UnityEngine;

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// The default <see cref="IRbxParticleBackend"/>: one Unity <c>ParticleSystem</c> per drawn emitter, on a pooled
    /// child object of the host part's GameObject, so it follows the part. Particles start anywhere in the part's box
    /// (the box shape takes the part's size through the part's scale, as <c>ScalingMode.Shape</c> scales only the
    /// shape), leave by the <c>EmissionDirection</c> face, and live in world space. <c>Lifetime</c> and <c>Speed</c> pick
    /// per particle between their bounds, <c>Size</c> and <c>Transparency</c> follow their sequences over the life,
    /// <c>Color</c> its gradient, <c>Acceleration</c> is a world force and <c>SpreadAngle</c> randomises the direction
    /// (the larger angle over 180 degrees, so 360 sends particles every way). The texture is a key registered here, a
    /// <c>Resources</c> path, or CoreAI's soft dot for an empty, Roblox or unresolved id.
    /// </summary>
    public sealed class UnityRbxParticleBackend : IRbxParticleBackend, IDisposable
    {
        /// <summary>Suffix of the child object that carries a drawn emitter.</summary>
        public const string ObjectSuffix = " (particles)";

        /// <summary>Unity's gradients hold eight keys; a longer sequence is sampled at eight evenly spaced times.</summary>
        public const int MaxGradientKeys = 8;

        private const string UrpParticleShader = "Universal Render Pipeline/Particles/Unlit";
        private const string FallbackParticleShader = "Sprites/Default";
        private const int DotSize = 64;

        private readonly Func<InstanceId, GameObject> _resolvePart;
        private readonly Func<InstanceId, string> _resolveName;
        private readonly Dictionary<InstanceId, Entry> _entries = new();
        private readonly Stack<Entry> _idle = new();
        private readonly Dictionary<string, Texture2D> _registeredTextures = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Texture2D> _loadedTextures = new(StringComparer.Ordinal);
        private readonly HashSet<string> _missingTextures = new(StringComparer.Ordinal);
        private readonly Dictionary<Texture2D, Material> _materials = new();
        private readonly List<InstanceId> _scratch = new();
        private GameObject _poolRoot;
        private Texture2D _dot;

        /// <param name="resolvePart">The host part's GameObject, or null while it has none.</param>
        /// <param name="poolParent">Where idle systems wait; null keeps them at the scene root.</param>
        /// <param name="resolveName">The emitter's name for the child object; null names it "ParticleEmitter".</param>
        public UnityRbxParticleBackend(Func<InstanceId, GameObject> resolvePart, Transform poolParent,
            RbxParticleBudget budget, Func<InstanceId, string> resolveName = null)
        {
            _resolvePart = resolvePart ?? throw new ArgumentNullException(nameof(resolvePart));
            _resolveName = resolveName;
            Budget = budget ?? RbxParticleBudget.Desktop;
            _poolRoot = new GameObject("CoreAI Particles (idle)");
            _poolRoot.SetActive(false);
            if (poolParent != null)
            {
                _poolRoot.transform.SetParent(poolParent, false);
            }
        }

        /// <summary>Over the binder: a part's GameObject is the one it bound for the part's instance.</summary>
        public UnityRbxParticleBackend(InstanceGameObjectBinder binder, InstanceRegistry registry, Transform poolParent,
            RbxParticleBudget budget)
            : this(id => binder != null && binder.TryGetBoundObject(id, out GameObject bound) ? bound : null,
                poolParent, budget,
                id => registry != null && registry.TryGetRecord(id, out InstanceRecord record)
                    ? record.Instance.Name
                    : null)
        {
        }

        /// <summary>The per-emitter particle cap comes from here.</summary>
        public RbxParticleBudget Budget { get; set; }

        /// <summary>Emitters this backend currently draws.</summary>
        public int Count => _entries.Count;

        /// <summary>CoreAI's soft dot, drawn for an empty, Roblox or unresolved texture.</summary>
        public Texture2D DefaultTexture => _dot != null ? _dot : _dot = CreateDot();

        /// <summary>Makes <paramref name="texture"/> the image of every emitter whose <c>Texture</c> is <paramref name="key"/>.</summary>
        public void RegisterTexture(string key, Texture2D texture)
        {
            if (!string.IsNullOrWhiteSpace(key) && texture != null)
            {
                _registeredTextures[key.Trim()] = texture;
            }
        }

        /// <summary>The system drawn for <paramref name="emitterId"/>, or null (tests and diagnostics).</summary>
        public ParticleSystem GetSystem(InstanceId emitterId)
        {
            return _entries.TryGetValue(emitterId, out Entry entry) && entry.System != null ? entry.System : null;
        }

        /// <summary>The texture a <c>Texture</c> value draws with.</summary>
        public Texture2D ResolveTexture(string texture)
        {
            if (string.IsNullOrWhiteSpace(texture) || RbxSoundLibrary.IsRobloxAssetId(texture))
            {
                return DefaultTexture;
            }

            string key = texture.Trim();
            if (_registeredTextures.TryGetValue(key, out Texture2D registered) && registered != null)
            {
                return registered;
            }

            if (_loadedTextures.TryGetValue(key, out Texture2D loaded) && loaded != null)
            {
                return loaded;
            }

            if (_missingTextures.Contains(key) || key.IndexOf(':') >= 0)
            {
                return DefaultTexture;
            }

            int slash = key.LastIndexOf('/');
            int dot = key.LastIndexOf('.');
            Texture2D found = Resources.Load<Texture2D>(dot > slash + 1 ? key.Substring(0, dot) : key);
            if (found == null)
            {
                _missingTextures.Add(key);
                return DefaultTexture;
            }

            _loadedTextures[key] = found;
            return found;
        }

        /// <inheritdoc />
        public void Apply(InstanceId emitterId, InstanceId hostPartId, in RbxParticleRenderState state)
        {
            GameObject part = _resolvePart(hostPartId);
            if (part == null)
            {
                Release(emitterId);
                return;
            }

            if (!_entries.TryGetValue(emitterId, out Entry entry) || entry.System == null)
            {
                entry = Acquire(emitterId);
                _entries[emitterId] = entry;
            }

            Transform holder = entry.Holder.transform;
            if (holder.parent != part.transform)
            {
                holder.SetParent(part.transform, false);
                holder.localPosition = Vector3.zero;
            }

            holder.localRotation = FaceRotation(state.EmissionDirection);
            Configure(entry.System, entry.Renderer, in state);
            if (!entry.System.isPlaying)
            {
                entry.System.Play();
            }
        }

        /// <inheritdoc />
        public void Emit(InstanceId emitterId, int count)
        {
            if (!_entries.TryGetValue(emitterId, out Entry entry) || entry.System == null || count <= 0)
            {
                return;
            }

            int room = entry.System.main.maxParticles - entry.System.particleCount;
            if (room > 0)
            {
                entry.System.Emit(Math.Min(count, room));
            }
        }

        /// <inheritdoc />
        public void Clear(InstanceId emitterId)
        {
            if (_entries.TryGetValue(emitterId, out Entry entry) && entry.System != null)
            {
                entry.System.Clear();
            }
        }

        /// <inheritdoc />
        public void Release(InstanceId emitterId)
        {
            if (!_entries.TryGetValue(emitterId, out Entry entry))
            {
                return;
            }

            _entries.Remove(emitterId);
            Recycle(entry);
        }

        /// <inheritdoc />
        public bool Holds(InstanceId emitterId)
        {
            return _entries.TryGetValue(emitterId, out Entry entry) && entry.System != null;
        }

        /// <summary>Destroys every drawn and idle system, the materials and the soft dot.</summary>
        public void Dispose()
        {
            _scratch.Clear();
            _scratch.AddRange(_entries.Keys);
            for (int index = 0; index < _scratch.Count; index++)
            {
                if (_entries.TryGetValue(_scratch[index], out Entry entry))
                {
                    DestroyOwned(entry.Holder);
                }
            }

            _entries.Clear();
            _idle.Clear();
            foreach (KeyValuePair<Texture2D, Material> pair in _materials)
            {
                DestroyOwned(pair.Value);
            }

            _materials.Clear();
            DestroyOwned(_dot);
            _dot = null;
            DestroyOwned(_poolRoot);
            _poolRoot = null;
        }

        private void Configure(ParticleSystem system, ParticleSystemRenderer renderer, in RbxParticleRenderState state)
        {
            float metersPerStud = RbxSpace.MetersPerStud;
            ParticleSystem.MainModule main = system.main;
            main.loop = true;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Shape;
            main.maxParticles = Budget.MaxParticlesPerEmitter;
            main.startLifetime = new ParticleSystem.MinMaxCurve(state.Lifetime.Min, state.Lifetime.Max);
            main.startSpeed = new ParticleSystem.MinMaxCurve(state.Speed.Min * metersPerStud,
                state.Speed.Max * metersPerStud);
            float maxSize = Mathf.Max(0f, state.Size.MaxValue());
            main.startSize = maxSize * metersPerStud;
            main.startColor = Color.white;

            ParticleSystem.EmissionModule emission = system.emission;
            emission.enabled = true;
            emission.rateOverTime = state.Enabled && state.Lifetime.Max > 0f ? state.Rate : 0f;

            ParticleSystem.ShapeModule shape = system.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.position = Vector3.zero;
            shape.rotation = Vector3.zero;
            shape.scale = Vector3.one;
            float spread = Mathf.Max(Mathf.Abs(state.SpreadAngle.X), Mathf.Abs(state.SpreadAngle.Y));
            shape.randomDirectionAmount = Mathf.Clamp01(spread / 180f);

            ParticleSystem.SizeOverLifetimeModule size = system.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, NormalisedCurve(state.Size, maxSize));

            ParticleSystem.ColorOverLifetimeModule color = system.colorOverLifetime;
            color.enabled = true;
            color.color = new ParticleSystem.MinMaxGradient(BuildGradient(state.Color, state.Transparency));

            ParticleSystem.ForceOverLifetimeModule force = system.forceOverLifetime;
            Vector3 acceleration = RbxSpace.VelocityToUnity(state.Acceleration);
            force.enabled = acceleration != Vector3.zero;
            force.space = ParticleSystemSimulationSpace.World;
            force.x = acceleration.x;
            force.y = acceleration.y;
            force.z = acceleration.z;

            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sharedMaterial = MaterialFor(ResolveTexture(state.Texture));
        }

        /// <summary>The holder's turn that points the box's emission axis (+Z) out of <paramref name="face"/>.</summary>
        private static Quaternion FaceRotation(RbxParticleFace face)
        {
            Vector3 normal;
            switch (face)
            {
                case RbxParticleFace.Right:
                    normal = Vector3.right;
                    break;
                case RbxParticleFace.Left:
                    normal = Vector3.left;
                    break;
                case RbxParticleFace.Bottom:
                    normal = Vector3.down;
                    break;
                case RbxParticleFace.Back:
                    normal = RbxSpace.DirectionToUnity(new RbxVector3(0f, 0f, 1f));
                    break;
                case RbxParticleFace.Front:
                    normal = RbxSpace.DirectionToUnity(new RbxVector3(0f, 0f, -1f));
                    break;
                default:
                    normal = Vector3.up;
                    break;
            }

            Vector3 up = Mathf.Abs(normal.y) > 0.9f ? Vector3.forward : Vector3.up;
            return Quaternion.LookRotation(normal, up);
        }

        private static AnimationCurve NormalisedCurve(RbxNumberSequence sequence, float max)
        {
            IReadOnlyList<RbxNumberSequenceKeypoint> points = sequence.Keypoints;
            Keyframe[] keys = new Keyframe[points.Count];
            for (int index = 0; index < points.Count; index++)
            {
                float value = max > 0f ? Mathf.Max(0f, points[index].Value) / max : 0f;
                keys[index] = new Keyframe(points[index].Time, value);
            }

            AnimationCurve curve = new(keys);
            for (int index = 0; index < keys.Length; index++)
            {
                curve.SmoothTangents(index, 0f);
            }

            return curve;
        }

        private static Gradient BuildGradient(RbxColorSequence colors, RbxNumberSequence transparency)
        {
            GradientColorKey[] colorKeys;
            IReadOnlyList<RbxColorSequenceKeypoint> colorPoints = colors.Keypoints;
            if (colorPoints.Count <= MaxGradientKeys)
            {
                colorKeys = new GradientColorKey[colorPoints.Count];
                for (int index = 0; index < colorPoints.Count; index++)
                {
                    RbxColor3 value = colorPoints[index].Value;
                    colorKeys[index] = new GradientColorKey(new Color(value.R, value.G, value.B),
                        colorPoints[index].Time);
                }
            }
            else
            {
                colorKeys = new GradientColorKey[MaxGradientKeys];
                for (int index = 0; index < MaxGradientKeys; index++)
                {
                    float time = index / (float)(MaxGradientKeys - 1);
                    RbxColor3 value = colors.Evaluate(time);
                    colorKeys[index] = new GradientColorKey(new Color(value.R, value.G, value.B), time);
                }
            }

            GradientAlphaKey[] alphaKeys;
            IReadOnlyList<RbxNumberSequenceKeypoint> alphaPoints = transparency.Keypoints;
            int alphaCount = Math.Min(alphaPoints.Count, MaxGradientKeys);
            alphaKeys = new GradientAlphaKey[alphaCount];
            for (int index = 0; index < alphaCount; index++)
            {
                float time = alphaPoints.Count <= MaxGradientKeys
                    ? alphaPoints[index].Time
                    : index / (float)(MaxGradientKeys - 1);
                alphaKeys[index] = new GradientAlphaKey(Mathf.Clamp01(1f - transparency.Evaluate(time)), time);
            }

            Gradient gradient = new();
            gradient.SetKeys(colorKeys, alphaKeys);
            return gradient;
        }

        private Material MaterialFor(Texture2D texture)
        {
            if (_materials.TryGetValue(texture, out Material material) && material != null)
            {
                return material;
            }

            Shader shader = Shader.Find(UrpParticleShader);
            if (shader == null)
            {
                shader = Shader.Find(FallbackParticleShader);
            }

            material = new Material(shader) { name = "CoreAI particles (" + texture.name + ")" };
            material.mainTexture = texture;
            if (material.HasProperty("_BaseMap"))
            {
                material.SetTexture("_BaseMap", texture);
            }

            if (material.HasProperty("_Surface"))
            {
                // WHY transparent: a particle's alpha is its texture's alpha times 1 - Transparency.
                material.SetFloat("_Surface", 1f);
                material.SetFloat("_Blend", 0f);
                material.SetOverrideTag("RenderType", "Transparent");
                material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                material.SetInt("_ZWrite", 0);
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }

            _materials[texture] = material;
            return material;
        }

        private Entry Acquire(InstanceId emitterId)
        {
            string name = (_resolveName?.Invoke(emitterId) ?? "ParticleEmitter") + ObjectSuffix;
            while (_idle.Count > 0)
            {
                Entry pooled = _idle.Pop();
                if (pooled.Holder != null && pooled.System != null)
                {
                    pooled.Holder.name = name;
                    return pooled;
                }
            }

            GameObject holder = new(name);
            ParticleSystem system = holder.AddComponent<ParticleSystem>();
            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ParticleSystemRenderer renderer = holder.GetComponent<ParticleSystemRenderer>();
            return new Entry { Holder = holder, System = system, Renderer = renderer };
        }

        private void Recycle(Entry entry)
        {
            if (entry.Holder == null || entry.System == null || _poolRoot == null)
            {
                DestroyOwned(entry.Holder);
                return;
            }

            entry.System.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            entry.Holder.transform.SetParent(_poolRoot.transform, false);
            _idle.Push(entry);
        }

        private static Texture2D CreateDot()
        {
            Texture2D dot = new(DotSize, DotSize, TextureFormat.RGBA32, false)
            {
                name = "CoreAI particle dot",
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave
            };
            Color32[] pixels = new Color32[DotSize * DotSize];
            float centre = (DotSize - 1) * 0.5f;
            for (int y = 0; y < DotSize; y++)
            {
                for (int x = 0; x < DotSize; x++)
                {
                    float dx = (x - centre) / centre;
                    float dy = (y - centre) / centre;
                    float fade = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                    pixels[y * DotSize + x] = new Color32(255, 255, 255, (byte)(255f * fade * fade));
                }
            }

            dot.SetPixels32(pixels);
            dot.Apply(false, true);
            return dot;
        }

        private static void DestroyOwned(UnityEngine.Object target)
        {
            if (target == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(target);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        private sealed class Entry
        {
            public GameObject Holder;
            public ParticleSystem System;
            public ParticleSystemRenderer Renderer;
        }
    }
}
