namespace CoreAI.Mods.Rbx.Instances.Networking
{
    /// <summary>
    /// Engine-free port for Player identity: maps a durable <c>UserId</c> to the username and
    /// display name Lua reads as <c>Player.Name</c> and <c>Player.DisplayName</c>. A host supplies
    /// a real backend (verified auth, durable ids) by assigning
    /// <see cref="RbxPlayers.ProfileProvider"/>; the default keeps the synthetic profile so solo
    /// and loopback keep working with no host wiring.
    /// </summary>
    public interface IRbxPlayerProfileProvider
    {
        /// <summary>
        /// Resolves the profile for <paramref name="userId"/>. Returns false when the backend
        /// knows no such user; the caller then falls back to the synthetic profile so identity
        /// stays stable instead of failing the join.
        /// </summary>
        bool TryGetProfile(long userId, out string username, out string displayName);
    }

    /// <summary>
    /// Default profile provider: the synthetic identity already in use (<c>Player{userId}</c> for
    /// both username and display name). The mirror states no DisplayName fallback, so defaulting
    /// the display name to the username is OURS, matching live Roblox behaviour.
    /// </summary>
    public sealed class SyntheticPlayerProfileProvider : IRbxPlayerProfileProvider
    {
        /// <summary>Shared default assigned to every new RbxPlayers service.</summary>
        public static readonly SyntheticPlayerProfileProvider Instance = new();

        private SyntheticPlayerProfileProvider()
        {
        }

        /// <summary>Resolves the profile for <paramref name="userId"/> (see interface).</summary>
        public bool TryGetProfile(long userId, out string username, out string displayName)
        {
            username = "Player" + userId;
            displayName = username;
            return true;
        }
    }
}
