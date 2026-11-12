using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>Names one voice of a world: a <c>Sound</c> instance, or a local copy of one.</summary>
    public readonly struct RbxSoundVoiceKey : IEquatable<RbxSoundVoiceKey>
    {
        private RbxSoundVoiceKey(ulong value, bool isLocalCopy)
        {
            Value = value;
            IsLocalCopy = isLocalCopy;
        }

        /// <summary>The sound's instance id value, or the copy's id.</summary>
        public ulong Value { get; }

        /// <summary>True for a copy started by <c>PlayLocalSound</c> or <c>PlayOnRemove</c>.</summary>
        public bool IsLocalCopy { get; }

        public static RbxSoundVoiceKey ForSound(InstanceId id)
        {
            return new RbxSoundVoiceKey(id.Value, false);
        }

        public static RbxSoundVoiceKey ForLocalCopy(ulong copyId)
        {
            return new RbxSoundVoiceKey(copyId, true);
        }

        public bool Equals(RbxSoundVoiceKey other)
        {
            return Value == other.Value && IsLocalCopy == other.IsLocalCopy;
        }

        public override bool Equals(object obj)
        {
            return obj is RbxSoundVoiceKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return Value.GetHashCode() ^ (IsLocalCopy ? 0x5bd1e995 : 0);
        }

        public override string ToString()
        {
            return (IsLocalCopy ? "copy " : "sound ") + Value;
        }
    }

    /// <summary>One audible voice as its backend plays it.</summary>
    public readonly struct RbxSoundRenderState
    {
        public RbxSoundRenderState(object clip, double lengthSeconds, double timePosition, int seekVersion,
            float volume, float playbackSpeed, bool looped, bool positional, RbxVector3 positionStuds)
        {
            Clip = clip;
            LengthSeconds = lengthSeconds;
            TimePosition = timePosition;
            SeekVersion = seekVersion;
            Volume = volume;
            PlaybackSpeed = playbackSpeed;
            Looped = looped;
            Positional = positional;
            PositionStuds = positionStuds;
        }

        /// <summary>The host's clip object (a Unity <c>AudioClip</c>).</summary>
        public object Clip { get; }

        public double LengthSeconds { get; }

        /// <summary>Where the timeline is, in seconds; the backend follows it.</summary>
        public double TimePosition { get; }

        /// <summary>Changes when a script moved the playhead; the backend then seeks instead of drifting.</summary>
        public int SeekVersion { get; }

        /// <summary>The final volume: the sound's, its group's and the distance attenuation's product.</summary>
        public float Volume { get; }

        public float PlaybackSpeed { get; }

        public bool Looped { get; }

        /// <summary>True for a sound heard from a part, false for a global one.</summary>
        public bool Positional { get; }

        /// <summary>The part's position in studs, for a positional voice.</summary>
        public RbxVector3 PositionStuds { get; }
    }

    /// <summary>What plays a world's voices: the Unity default pools <c>AudioSource</c>s; a headless world has none.</summary>
    public interface IRbxAudioBackend
    {
        /// <summary>Starts or updates <paramref name="voice"/>; called every frame while it is audible.</summary>
        void Apply(RbxSoundVoiceKey voice, in RbxSoundRenderState state);

        /// <summary>Stops <paramref name="voice"/>; unknown voices are ignored.</summary>
        void Release(RbxSoundVoiceKey voice);

        /// <summary>True while the backend still plays <paramref name="voice"/>.</summary>
        bool Holds(RbxSoundVoiceKey voice);
    }

    /// <summary>How many voices one tier plays at once; past it the loudest stay and the rest are silent.</summary>
    public sealed class RbxSoundBudget
    {
        /// <summary>Desktop: well under Unity's default of 32 real voices per mixer, plus virtual ones.</summary>
        public static readonly RbxSoundBudget Desktop = new("desktop", 32);

        /// <summary>WebGL: every voice is a Web Audio graph of its own, so fewer play at once.</summary>
        public static readonly RbxSoundBudget WebGl = new("WebGL", 16);

        public RbxSoundBudget(string tier, int maxVoices)
        {
            Tier = string.IsNullOrWhiteSpace(tier) ? "custom" : tier;
            MaxVoices = maxVoices < 0 ? 0 : maxVoices;
        }

        public string Tier { get; }

        public int MaxVoices { get; }
    }

    /// <summary>
    /// <c>Enum.RollOffMode</c> as a gain in [0, 1] (RollOffMode.yaml): Inverse is <c>min / distance</c>, Linear
    /// falls straight from 1 at <c>RollOffMinDistance</c> to 0 at <c>RollOffMaxDistance</c>, LinearSquare is Linear
    /// squared and InverseTapered the lesser of Inverse and LinearSquare. Nothing is heard past
    /// <c>RollOffMaxDistance</c>, and everything within <c>RollOffMinDistance</c> is at full volume.
    /// </summary>
    public static class RbxSoundRollOff
    {
        public static float Gain(RbxRollOffMode mode, float minDistance, float maxDistance, float distance)
        {
            float min = Math.Max(0f, minDistance);
            float max = Math.Max(min, maxDistance);
            float d = float.IsNaN(distance) || distance < 0f ? 0f : distance;
            if (d <= min)
            {
                return 1f;
            }

            if (d >= max)
            {
                return 0f;
            }

            float inverse = min / d;
            float linear = max > min ? (max - d) / (max - min) : 0f;
            switch (mode)
            {
                case RbxRollOffMode.Linear:
                    return linear;
                case RbxRollOffMode.LinearSquare:
                    return linear * linear;
                case RbxRollOffMode.InverseTapered:
                    return Math.Min(inverse, linear * linear);
                default:
                    return inverse;
            }
        }
    }

    /// <summary>
    /// Keeps a world's audible voices in step with its <see cref="RbxSound"/> instances and local copies, once per
    /// frame: a voice plays while its sound is playing and loaded inside the DataModel; one parented to a part in
    /// the Workspace is heard from that part through <see cref="RbxSoundRollOff"/> against the listener (the
    /// camera), any other globally. Over the budget only the loudest voices play. The backend is told every audible
    /// voice each frame and released for every voice that went silent; a still world allocates nothing.
    /// </summary>
    public sealed class RbxSoundReconciler : IDisposable
    {
        private readonly InstanceRegistry _registry;
        private readonly RbxDataModel _game;
        private readonly IRbxAudioBackend _backend;
        private readonly Func<InstanceId, RbxVector3> _partPosition;
        private readonly Action<string> _notice;
        private readonly Dictionary<InstanceId, RbxSound> _sounds = new();
        private readonly List<Candidate> _candidates = new();
        private readonly Dictionary<RbxSoundVoiceKey, int> _applied = new();
        private readonly List<RbxSoundVoiceKey> _stale = new();
        private int _tick;
        private bool _overBudgetReported;
        private bool _disposed;

        private struct Candidate
        {
            public RbxSoundVoiceKey Key;
            public RbxSoundRenderState State;
        }

        /// <param name="partPosition">A part's position in studs; read for positional voices.</param>
        /// <param name="notice">Receives the one-line budget diagnostic; null drops it.</param>
        public RbxSoundReconciler(InstanceRegistry registry, RbxDataModel game, IRbxAudioBackend backend,
            Func<InstanceId, RbxVector3> partPosition, RbxSoundBudget budget, Action<string> notice = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _partPosition = partPosition ?? throw new ArgumentNullException(nameof(partPosition));
            Budget = budget ?? RbxSoundBudget.Desktop;
            _notice = notice;
            _registry.Registered += OnRegistered;
            _registry.Unregistered += OnUnregistered;
            IReadOnlyList<RbxInstance> live = _registry.GetLiveInstances();
            for (int index = 0; index < live.Count; index++)
            {
                if (live[index] is RbxSound sound)
                {
                    _sounds[sound.Id] = sound;
                }
            }
        }

        /// <summary>The budget in force; a host may replace it at any time.</summary>
        public RbxSoundBudget Budget { get; set; }

        /// <summary>Sound instances in the world, audible or not.</summary>
        public int TrackedSounds => _sounds.Count;

        /// <summary>Voices that wanted to play at the last tick.</summary>
        public int AudibleCandidates { get; private set; }

        /// <summary>Voices playing after the last tick.</summary>
        public int PlayingVoices { get; private set; }

        /// <summary>Voices the budget silenced at the last tick.</summary>
        public int CulledByBudget { get; private set; }

        /// <summary>Brings every voice up to date; <paramref name="listenerStuds"/> is where the world is heard from.</summary>
        public void Tick(RbxVector3 listenerStuds)
        {
            if (_disposed)
            {
                return;
            }

            _tick++;
            _candidates.Clear();
            foreach (KeyValuePair<InstanceId, RbxSound> pair in _sounds)
            {
                RbxSound sound = pair.Value;
                if (sound.IsDestroyed || !sound.Playing || !sound.IsLoaded || !sound.IsDescendantOf(_game))
                {
                    continue;
                }

                InstanceId host = RbxSoundService.ResolveHostPart(sound);
                AddCandidate(RbxSoundVoiceKey.ForSound(sound.Id), sound.LoadedAsset, sound.TimePosition,
                    sound.SeekVersion, sound.EffectiveVolume, sound.PlaybackSpeed, sound.Looped, host,
                    sound.RollOffMode, sound.RollOffMinDistance, sound.RollOffMaxDistance, listenerStuds);
            }

            if (_game.FindFirstChildOfClass("SoundService") is RbxSoundService service)
            {
                IReadOnlyList<RbxSoundLocalVoice> copies = service.LocalVoices;
                for (int index = 0; index < copies.Count; index++)
                {
                    RbxSoundLocalVoice copy = copies[index];
                    if (copy.Finished)
                    {
                        continue;
                    }

                    InstanceId host = copy.HostPartId.IsValid && _registry.TryGet(copy.HostPartId, out RbxInstance part)
                                                              && !part.IsDestroyed
                        ? copy.HostPartId
                        : InstanceId.None;
                    AddCandidate(RbxSoundVoiceKey.ForLocalCopy(copy.Id), copy.Asset, copy.TimePosition, 0,
                        copy.EffectiveVolume, copy.PlaybackSpeed, false, host, copy.RollOffMode,
                        copy.RollOffMinDistance, copy.RollOffMaxDistance, listenerStuds);
                }
            }

            AudibleCandidates = _candidates.Count;
            int budget = Budget.MaxVoices;
            if (_candidates.Count > budget)
            {
                _candidates.Sort(LoudestFirst.Instance);
            }

            int playing = Math.Min(budget, _candidates.Count);
            for (int index = 0; index < playing; index++)
            {
                Candidate candidate = _candidates[index];
                _backend.Apply(candidate.Key, in candidate.State);
                _applied[candidate.Key] = _tick;
            }

            _stale.Clear();
            foreach (KeyValuePair<RbxSoundVoiceKey, int> pair in _applied)
            {
                if (pair.Value != _tick)
                {
                    _stale.Add(pair.Key);
                }
            }

            for (int index = 0; index < _stale.Count; index++)
            {
                _applied.Remove(_stale[index]);
                _backend.Release(_stale[index]);
            }

            PlayingVoices = playing;
            CulledByBudget = _candidates.Count - playing;
            ReportBudget();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _registry.Registered -= OnRegistered;
            _registry.Unregistered -= OnUnregistered;
            _stale.Clear();
            foreach (KeyValuePair<RbxSoundVoiceKey, int> pair in _applied)
            {
                _stale.Add(pair.Key);
            }

            for (int index = 0; index < _stale.Count; index++)
            {
                _backend.Release(_stale[index]);
            }

            _stale.Clear();
            _applied.Clear();
            _sounds.Clear();
        }

        private void AddCandidate(RbxSoundVoiceKey key, in RbxSoundAsset asset, double timePosition, int seekVersion,
            float volume, float speed, bool looped, InstanceId host, RbxRollOffMode mode, float min, float max,
            RbxVector3 listenerStuds)
        {
            bool positional = host.IsValid;
            RbxVector3 position = positional ? _partPosition(host) : new RbxVector3(0f, 0f, 0f);
            float gain = 1f;
            if (positional)
            {
                RbxVector3 offset = position - listenerStuds;
                float distance = (float)Math.Sqrt(offset.X * offset.X + offset.Y * offset.Y + offset.Z * offset.Z);
                gain = RbxSoundRollOff.Gain(mode, min, max, distance);
            }

            float final = volume * gain;
            if (!(final > 0f))
            {
                return;
            }

            _candidates.Add(new Candidate
            {
                Key = key,
                State = new RbxSoundRenderState(asset.Clip, asset.LengthSeconds, timePosition, seekVersion, final,
                    speed, looped, positional, position)
            });
        }

        private void ReportBudget()
        {
            if (CulledByBudget == 0)
            {
                _overBudgetReported = false;
                return;
            }

            if (_overBudgetReported)
            {
                return;
            }

            _overBudgetReported = true;
            _notice?.Invoke("[CoreAI.RbxApi] sound budget: " + PlayingVoices + " of " + AudibleCandidates
                            + " sounds are heard; the " + Budget.Tier + " tier plays the loudest " + Budget.MaxVoices
                            + " and keeps the rest silent (their timelines still run) until fewer play.");
        }

        private void OnRegistered(InstanceRecord record)
        {
            if (record?.Instance is RbxSound sound)
            {
                _sounds[sound.Id] = sound;
            }
        }

        private void OnUnregistered(InstanceRecord record)
        {
            if (record == null || !_sounds.Remove(record.Id))
            {
                return;
            }

            RbxSoundVoiceKey key = RbxSoundVoiceKey.ForSound(record.Id);
            if (_applied.Remove(key))
            {
                _backend.Release(key);
            }
        }

        private sealed class LoudestFirst : IComparer<Candidate>
        {
            public static readonly LoudestFirst Instance = new();

            public int Compare(Candidate x, Candidate y)
            {
                int byVolume = y.State.Volume.CompareTo(x.State.Volume);
                if (byVolume != 0)
                {
                    return byVolume;
                }

                int byKind = x.Key.IsLocalCopy.CompareTo(y.Key.IsLocalCopy);
                return byKind != 0 ? byKind : x.Key.Value.CompareTo(y.Key.Value);
            }
        }
    }
}
