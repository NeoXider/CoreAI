using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Spatial;
using UnityEngine;

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// The default <see cref="IRbxAudioBackend"/>: a pool of <c>AudioSource</c>s under one holder object of the world
    /// host. A global voice plays 2D; a positional one plays 3D at its part's position, panned by Unity while the
    /// distance attenuation is the engine-free <see cref="RbxSoundRollOff"/> already folded into the volume (Unity's own
    /// roll-off is pushed past any world's size, which works the same in WebGL's Web Audio panner). The source
    /// follows the engine-free timeline: it seeks when a script moved the playhead and when it drifted further than
    /// <see cref="DriftToleranceSeconds"/>, so a browser that unlocks audio after the first click joins a sound
    /// where its timeline is.
    /// </summary>
    public sealed class UnityRbxAudioBackend : IRbxAudioBackend, IDisposable
    {
        /// <summary>Name of the object that holds the pooled sources.</summary>
        public const string HolderName = "CoreAI Audio";

        /// <summary>How far a source may drift from its timeline before it is put back, in seconds.</summary>
        public const double DriftToleranceSeconds = 0.25d;

        /// <summary>Unity's pitch range ends at 3; a faster <c>PlaybackSpeed</c> sounds at 3.</summary>
        public const float MaxPitch = 3f;

        /// <summary>Idle sources kept for reuse; more are destroyed.</summary>
        public const int MaxIdleSources = 32;

        private const float NeutralRolloffMeters = 1000000f;
        private const double EndGuardSeconds = 0.05d;

        private readonly Dictionary<RbxSoundVoiceKey, Entry> _voices = new();
        private readonly Stack<AudioSource> _idle = new();
        private readonly List<RbxSoundVoiceKey> _scratch = new();
        private GameObject _holder;

        /// <param name="parent">The world host's transform; positional voices are placed in its local space, as parts are.</param>
        public UnityRbxAudioBackend(Transform parent)
        {
            _holder = new GameObject(HolderName);
            if (parent != null)
            {
                _holder.transform.SetParent(parent, false);
            }
        }

        /// <summary>Scales the final volume into the source's 0..1 (1 keeps them equal; louder is clipped to 1).</summary>
        public float VolumeScale { get; set; } = 1f;

        /// <summary>Voices this backend currently plays.</summary>
        public int Count => _voices.Count;

        /// <summary>The source playing <paramref name="voice"/>, or null (tests and diagnostics).</summary>
        public AudioSource GetSource(RbxSoundVoiceKey voice)
        {
            return _voices.TryGetValue(voice, out Entry entry) && entry.Source != null ? entry.Source : null;
        }

        /// <summary>
        /// The <c>Resources</c> fallback of <see cref="RbxSoundLibrary"/>: the id is a path under a <c>Resources</c>
        /// folder, with or without its extension.
        /// </summary>
        public static bool TryLoadFromResources(string soundId, out RbxSoundAsset asset)
        {
            asset = default;
            if (string.IsNullOrWhiteSpace(soundId) || soundId.IndexOf(':') >= 0)
            {
                return false;
            }

            string path = soundId.Trim();
            int slash = path.LastIndexOf('/');
            int dot = path.LastIndexOf('.');
            if (dot > slash + 1)
            {
                path = path.Substring(0, dot);
            }

            AudioClip clip = Resources.Load<AudioClip>(path);
            if (clip == null || !(clip.length > 0f))
            {
                return false;
            }

            asset = new RbxSoundAsset(soundId, clip.length, clip);
            return true;
        }

        /// <inheritdoc />
        public void Apply(RbxSoundVoiceKey voice, in RbxSoundRenderState state)
        {
            AudioClip clip = state.Clip as AudioClip;
            if (clip == null || _holder == null)
            {
                Release(voice);
                return;
            }

            if (!_voices.TryGetValue(voice, out Entry entry) || entry.Source == null)
            {
                entry = new Entry { Source = Acquire(voice), SeekVersion = int.MinValue };
                _voices[voice] = entry;
            }

            AudioSource source = entry.Source;
            bool newClip = source.clip != clip;
            if (newClip)
            {
                source.Stop();
                source.clip = clip;
            }

            source.loop = state.Looped;
            source.pitch = Mathf.Clamp(state.PlaybackSpeed, 0f, MaxPitch);
            source.volume = Mathf.Clamp01(state.Volume * VolumeScale);
            source.spatialBlend = state.Positional ? 1f : 0f;
            if (state.Positional)
            {
                source.transform.localPosition = RbxSpace.ToUnity(state.PositionStuds);
            }

            double length = clip.length;
            double target = Math.Max(0d, Math.Min(state.TimePosition, length - 0.001d));
            if (newClip || entry.SeekVersion != state.SeekVersion)
            {
                entry.SeekVersion = state.SeekVersion;
                source.time = (float)target;
                if (!source.isPlaying)
                {
                    source.Play();
                }

                return;
            }

            if (!source.isPlaying)
            {
                // WHY the guard: a clip that finished a moment before its timeline must not start over near the end.
                if (state.Looped || target < length - EndGuardSeconds)
                {
                    source.time = (float)target;
                    source.Play();
                }

                return;
            }

            double drift = Math.Abs(source.time - target);
            if (state.Looped && length > 0d)
            {
                drift = Math.Min(drift, length - drift);
            }

            if (drift > DriftToleranceSeconds)
            {
                source.time = (float)target;
            }
        }

        /// <inheritdoc />
        public void Release(RbxSoundVoiceKey voice)
        {
            if (!_voices.TryGetValue(voice, out Entry entry))
            {
                return;
            }

            _voices.Remove(voice);
            Recycle(entry.Source);
        }

        /// <inheritdoc />
        public bool Holds(RbxSoundVoiceKey voice)
        {
            return _voices.TryGetValue(voice, out Entry entry) && entry.Source != null;
        }

        /// <summary>Stops every voice and destroys the holder with its sources.</summary>
        public void Dispose()
        {
            _scratch.Clear();
            _scratch.AddRange(_voices.Keys);
            for (int index = 0; index < _scratch.Count; index++)
            {
                Release(_scratch[index]);
            }

            _scratch.Clear();
            _idle.Clear();
            DestroyOwned(_holder);
            _holder = null;
        }

        private AudioSource Acquire(RbxSoundVoiceKey voice)
        {
            while (_idle.Count > 0)
            {
                AudioSource pooled = _idle.Pop();
                if (pooled != null)
                {
                    pooled.gameObject.name = "Voice " + voice;
                    return pooled;
                }
            }

            GameObject holder = new("Voice " + voice);
            holder.transform.SetParent(_holder.transform, false);
            AudioSource source = holder.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.dopplerLevel = 0f;
            source.spread = 0f;
            source.rolloffMode = AudioRolloffMode.Linear;
            // WHY max first: Unity holds minDistance at or below maxDistance, so the default max of 500 would clamp it.
            source.maxDistance = NeutralRolloffMeters * 2f;
            source.minDistance = NeutralRolloffMeters;
            return source;
        }

        private void Recycle(AudioSource source)
        {
            if (source == null)
            {
                return;
            }

            source.Stop();
            source.clip = null;
            source.transform.localPosition = Vector3.zero;
            if (_idle.Count >= MaxIdleSources || _holder == null)
            {
                DestroyOwned(source.gameObject);
                return;
            }

            _idle.Push(source);
        }

        private static void DestroyOwned(GameObject target)
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
            public AudioSource Source;
            public int SeekVersion;
        }
    }
}
