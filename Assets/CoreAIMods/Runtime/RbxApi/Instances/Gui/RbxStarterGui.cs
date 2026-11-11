using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances.Networking;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>Roblox <c>BasePlayerGui</c>: the abstract base of <c>PlayerGui</c> and <c>StarterGui</c>.</summary>
    public abstract class RbxBasePlayerGui : RbxInstance
    {
        private protected RbxBasePlayerGui(ClassDescriptor descriptor)
            : base(descriptor)
        {
        }
    }

    /// <summary>
    /// Roblox <c>PlayerGui</c>: the per-player container whose <c>ScreenGui</c> children are drawn on that
    /// player's screen. <c>ScreenOrientation</c> is recorded and <c>CurrentScreenOrientation</c> reads it back.
    /// </summary>
    public sealed class RbxPlayerGui : RbxBasePlayerGui
    {
        private int _screenOrientation = 2;

        internal RbxPlayerGui(ClassDescriptor descriptor)
            : base(descriptor)
        {
        }

        /// <summary><c>Enum.ScreenOrientation</c> value (default LandscapeSensor); recorded.</summary>
        public int ScreenOrientation => _screenOrientation;

        public void SetScreenOrientation(int value)
        {
            ThrowIfDestroyed(nameof(ScreenOrientation));
            if (_screenOrientation == value)
            {
                return;
            }

            _screenOrientation = value;
            NotifyPropertyChanged(nameof(ScreenOrientation));
        }

        /// <summary>The player this container belongs to, or null while it has none.</summary>
        public RbxPlayer Player => Parent as RbxPlayer;
    }

    /// <summary>
    /// Roblox <c>StarterGui</c>: the service whose children are copied into a player's <c>PlayerGui</c> each time
    /// that player's character spawns (<see cref="RbxStarterGuiReplicator"/>). <c>SetCoreGuiEnabled</c> and
    /// <c>GetCoreGuiEnabled</c> record per <c>Enum.CoreGuiType</c> (CoreAI draws no Roblox core GUI, so there is
    /// nothing else to switch); <c>ShowDevelopmentGui</c> and <c>ScreenOrientation</c> are recorded.
    /// </summary>
    public sealed class RbxStarterGui : RbxBasePlayerGui
    {
        /// <summary><c>Enum.CoreGuiType.All</c>.</summary>
        public const int CoreGuiTypeAll = 4;

        private const int CoreGuiTypeCount = 10;

        private readonly bool[] _coreGuiDisabled = new bool[CoreGuiTypeCount];
        private bool _showDevelopmentGui = true;
        private int _screenOrientation = 4;

        internal RbxStarterGui(ClassDescriptor descriptor)
            : base(descriptor)
        {
            Name = "StarterGui";
        }

        public bool ShowDevelopmentGui => _showDevelopmentGui;

        /// <summary><c>Enum.ScreenOrientation</c> value (default Sensor); recorded.</summary>
        public int ScreenOrientation => _screenOrientation;

        public void SetShowDevelopmentGui(bool value)
        {
            ThrowIfDestroyed(nameof(ShowDevelopmentGui));
            if (_showDevelopmentGui == value)
            {
                return;
            }

            _showDevelopmentGui = value;
            NotifyPropertyChanged(nameof(ShowDevelopmentGui));
        }

        public void SetScreenOrientation(int value)
        {
            ThrowIfDestroyed(nameof(ScreenOrientation));
            if (_screenOrientation == value)
            {
                return;
            }

            _screenOrientation = value;
            NotifyPropertyChanged(nameof(ScreenOrientation));
        }

        /// <summary>Mirror <c>StarterGui:SetCoreGuiEnabled(coreGuiType, enabled)</c>; <c>All</c> sets every type.</summary>
        public void SetCoreGuiEnabled(int coreGuiType, bool enabled)
        {
            ThrowIfDestroyed("SetCoreGuiEnabled");
            if (coreGuiType == CoreGuiTypeAll)
            {
                for (int i = 0; i < _coreGuiDisabled.Length; i++)
                {
                    _coreGuiDisabled[i] = !enabled;
                }

                return;
            }

            if (coreGuiType < 0 || coreGuiType >= _coreGuiDisabled.Length)
            {
                throw RbxError.BadArgument("SetCoreGuiEnabled expects an Enum.CoreGuiType item",
                    "pass e.g. Enum.CoreGuiType.Backpack or Enum.CoreGuiType.All");
            }

            _coreGuiDisabled[coreGuiType] = !enabled;
            _coreGuiDisabled[CoreGuiTypeAll] = false;
        }

        /// <summary>Mirror <c>StarterGui:GetCoreGuiEnabled(coreGuiType)</c>; <c>All</c> is true only while every type is.</summary>
        public bool GetCoreGuiEnabled(int coreGuiType)
        {
            ThrowIfDestroyed("GetCoreGuiEnabled");
            if (coreGuiType == CoreGuiTypeAll)
            {
                for (int i = 0; i < _coreGuiDisabled.Length; i++)
                {
                    if (i != CoreGuiTypeAll && _coreGuiDisabled[i])
                    {
                        return false;
                    }
                }

                return true;
            }

            if (coreGuiType < 0 || coreGuiType >= _coreGuiDisabled.Length)
            {
                throw RbxError.BadArgument("GetCoreGuiEnabled expects an Enum.CoreGuiType item",
                    "pass e.g. Enum.CoreGuiType.Backpack or Enum.CoreGuiType.All");
            }

            return !_coreGuiDisabled[coreGuiType];
        }
    }

    /// <summary>
    /// Copies <c>StarterGui</c> into each player's <c>PlayerGui</c> when that player's character spawns, the
    /// way Roblox does for the solo case: every archivable child is cloned (keeping its owning mod, so the copy
    /// goes away with the mod); on a respawn the previous copies are replaced, except a <c>LayerCollector</c>
    /// with <c>ResetOnSpawn</c> false, which is copied once and kept. Scripts are not copied: a CoreAI script is
    /// a mod, not an instance that runs where it is parented.
    /// </summary>
    /// <remarks>
    /// Three deviations, each because a CoreAI mod loads into a session that is already running and does not
    /// re-run on a respawn: (1) a respawn replaces only the copies, so a ScreenGui a mod parented into
    /// PlayerGui itself survives it (nothing would rebuild it); (2) a child added to StarterGui while a player
    /// already has a character is copied to that player on the next frame, as if it had been there when the
    /// character spawned; (3) a copy is destroyed when its StarterGui original is, so a hot reload, which
    /// rebuilds the original, leaves one copy rather than two.
    /// </remarks>
    public sealed class RbxStarterGuiReplicator
    {
        private readonly RbxDataModel _game;
        private readonly List<RbxPlayer> _players = new();
        private readonly Dictionary<RbxPlayer, PlayerState> _states = new();
        private readonly List<RbxInstance> _scratch = new();
        private readonly List<RbxInstance> _pending = new();
        private bool _sourceDestroyed;

        public RbxStarterGuiReplicator(RbxDataModel game)
        {
            _game = game ?? throw new ArgumentNullException(nameof(game));
            InstanceRegistry registry = game.Registry;
            if (registry != null)
            {
                registry.ParentChanged += OnParentChanged;
                registry.Unregistered += OnUnregistered;
            }
        }

        /// <summary>Spawns seen so far (tests).</summary>
        public int SpawnCount { get; private set; }

        /// <summary>Starts watching a player's character.</summary>
        public void Track(RbxPlayer player)
        {
            if (player == null || _states.ContainsKey(player))
            {
                return;
            }

            _players.Add(player);
            _states.Add(player, new PlayerState());
        }

        /// <summary>Stops watching a player (it left).</summary>
        public void Untrack(RbxPlayer player)
        {
            if (player != null && _states.Remove(player))
            {
                _players.Remove(player);
            }
        }

        /// <summary>
        /// Once per frame: copies StarterGui for every watched player whose character changed to a new one, copies
        /// children added to StarterGui since the last poll to players who already have a character, and removes
        /// the copies of originals that were destroyed.
        /// </summary>
        public void Poll()
        {
            for (int i = _players.Count - 1; i >= 0; i--)
            {
                RbxPlayer player = _players[i];
                if (player.IsDestroyed)
                {
                    _states.Remove(player);
                    _players.RemoveAt(i);
                    continue;
                }

                PlayerState state = _states[player];
                RbxInstance character = player.Character;
                if (ReferenceEquals(character, state.Character))
                {
                    continue;
                }

                state.Character = character;
                if (character != null && !character.IsDestroyed)
                {
                    CopyInto(player, state);
                }
            }

            if (_sourceDestroyed)
            {
                _sourceDestroyed = false;
                RemoveOrphanedCopies();
            }

            if (_pending.Count > 0)
            {
                CopyPending();
            }
        }

        /// <summary>Copies StarterGui into <paramref name="player"/>'s PlayerGui now, as a spawn does.</summary>
        public void CopyInto(RbxPlayer player)
        {
            if (player == null)
            {
                return;
            }

            Track(player);
            CopyInto(player, _states[player]);
        }

        private void CopyInto(RbxPlayer player, PlayerState state)
        {
            RbxInstance playerGui = player.FindFirstChildOfClass("PlayerGui");
            RbxInstance starterGui = _game.FindFirstChildOfClass("StarterGui");
            if (playerGui == null || starterGui == null || starterGui.IsDestroyed)
            {
                return;
            }

            SpawnCount++;
            for (int i = state.Copies.Count - 1; i >= 0; i--)
            {
                CopyEntry entry = state.Copies[i];
                bool keep = !entry.Copy.IsDestroyed && entry.Copy is RbxLayerCollector layer && !layer.ResetOnSpawn;
                if (keep)
                {
                    continue;
                }

                state.Copies.RemoveAt(i);
                if (!entry.Copy.IsDestroyed && ReferenceEquals(entry.Copy.Parent, playerGui))
                {
                    entry.Copy.Destroy();
                }
            }

            _scratch.Clear();
            _scratch.AddRange(starterGui.GetChildren());
            for (int i = 0; i < _scratch.Count; i++)
            {
                CopyOne(_scratch[i], playerGui, state, true);
            }

            _scratch.Clear();
        }

        private static void CopyOne(RbxInstance source, RbxInstance playerGui, PlayerState state, bool spawn)
        {
            if (source.IsDestroyed || source.IsA("LuaSourceContainer"))
            {
                return;
            }

            bool once = source is RbxLayerCollector layer && !layer.ResetOnSpawn;
            if ((once || !spawn) && state.HasCopyOf(source))
            {
                return;
            }

            RbxInstance copy = source.Clone();
            if (copy == null)
            {
                return;
            }

            copy.Parent = playerGui;
            state.Copies.Add(new CopyEntry(source, copy));
        }

        private void CopyPending()
        {
            _scratch.Clear();
            _scratch.AddRange(_pending);
            _pending.Clear();
            for (int p = 0; p < _players.Count; p++)
            {
                RbxPlayer player = _players[p];
                PlayerState state = _states[player];
                RbxInstance playerGui = player.IsDestroyed ? null : player.FindFirstChildOfClass("PlayerGui");
                if (playerGui == null || state.Character == null || state.Character.IsDestroyed)
                {
                    continue;
                }

                for (int i = 0; i < _scratch.Count; i++)
                {
                    RbxInstance source = _scratch[i];
                    if (!source.IsDestroyed && source.Parent is RbxStarterGui)
                    {
                        CopyOne(source, playerGui, state, false);
                    }
                }
            }

            _scratch.Clear();
        }

        private void RemoveOrphanedCopies()
        {
            for (int p = 0; p < _players.Count; p++)
            {
                List<CopyEntry> copies = _states[_players[p]].Copies;
                for (int i = copies.Count - 1; i >= 0; i--)
                {
                    CopyEntry entry = copies[i];
                    if (!entry.Source.IsDestroyed)
                    {
                        continue;
                    }

                    copies.RemoveAt(i);
                    if (!entry.Copy.IsDestroyed)
                    {
                        entry.Copy.Destroy();
                    }
                }
            }
        }

        private void OnParentChanged(RbxInstance instance)
        {
            if (instance.Parent is RbxStarterGui && !_pending.Contains(instance))
            {
                _pending.Add(instance);
            }
        }

        private void OnUnregistered(InstanceRecord record)
        {
            RbxInstance instance = record?.Instance;
            if (instance == null || _sourceDestroyed)
            {
                return;
            }

            for (int p = 0; p < _players.Count; p++)
            {
                if (_states[_players[p]].IsSource(instance))
                {
                    _sourceDestroyed = true;
                    return;
                }
            }
        }

        private readonly struct CopyEntry
        {
            public CopyEntry(RbxInstance source, RbxInstance copy)
            {
                Source = source;
                Copy = copy;
            }

            public RbxInstance Source { get; }

            public RbxInstance Copy { get; }
        }

        private sealed class PlayerState
        {
            public RbxInstance Character;

            public readonly List<CopyEntry> Copies = new();

            public bool HasCopyOf(RbxInstance source)
            {
                for (int i = 0; i < Copies.Count; i++)
                {
                    if (ReferenceEquals(Copies[i].Source, source) && !Copies[i].Copy.IsDestroyed)
                    {
                        return true;
                    }
                }

                return false;
            }

            public bool IsSource(RbxInstance instance)
            {
                for (int i = 0; i < Copies.Count; i++)
                {
                    if (ReferenceEquals(Copies[i].Source, instance))
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }
}
