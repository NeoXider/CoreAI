using System;

namespace CoreAI.Mods.Rbx.Instances.DataStores
{
    /// <summary>
    /// The limits a host game sets for <c>DataStoreService</c>: Roblox's own numbers (request budgets per minute,
    /// the 30-request queue, 50-character names, the 4,194,304-character value) and CoreAI's sandbox caps on
    /// top of them (stored characters, keys and stores per world, requests waiting per mod, the write delay).
    /// One instance is shared by every world of a composition; change it before the first world starts.
    /// </summary>
    public sealed class RbxDataStoreOptions
    {
        /// <summary>Mirror limit of a data store name, a scope and a key, in characters.</summary>
        public const int MirrorNameLimit = 50;

        /// <summary>Mirror limit of a value: the characters of its JSON encoding.</summary>
        public const int MirrorValueLimit = 4_194_304;

        /// <summary>Mirror limit of the serialized user metadata of one key, in characters.</summary>
        public const int MirrorMetadataLimit = 300;

        /// <summary>Mirror limit of one metadata key name, in characters.</summary>
        public const int MirrorMetadataKeyLimit = 50;

        /// <summary>Mirror limit of one metadata value, in characters of its JSON encoding.</summary>
        public const int MirrorMetadataValueLimit = 250;

        /// <summary>The user id cap of one key (S1.22: the historical limit of 4).</summary>
        public const int MirrorUserIdLimit = 4;

        /// <summary>Mirror size of one request queue.</summary>
        public const int MirrorQueueSize = 30;

        /// <summary>Server-level read budget per minute: this plus <see cref="ReadPerPlayer"/> per player (mirror default 60).</summary>
        public int ReadBase { get; set; } = 60;

        /// <summary>Read budget per player per minute (mirror default 40).</summary>
        public int ReadPerPlayer { get; set; } = 40;

        /// <summary>Server-level write budget per minute (SetAsync, IncrementAsync, the write of UpdateAsync; mirror default 60).</summary>
        public int WriteBase { get; set; } = 60;

        /// <summary>Write budget per player per minute (mirror default 40).</summary>
        public int WritePerPlayer { get; set; } = 40;

        /// <summary>Server-level remove budget per minute (RemoveAsync; mirror default 60).</summary>
        public int RemoveBase { get; set; } = 60;

        /// <summary>Remove budget per player per minute (mirror default 40).</summary>
        public int RemovePerPlayer { get; set; } = 40;

        /// <summary>Server-level list budget per minute (mirror default 5); listing is not built yet, the budget is reported.</summary>
        public int ListBase { get; set; } = 5;

        /// <summary>List budget per player per minute (mirror default 2).</summary>
        public int ListPerPlayer { get; set; } = 2;

        /// <summary>Requests one budget may hold waiting before a new one is dropped (mirror: 30 per queue).</summary>
        public int QueueSize { get; set; } = MirrorQueueSize;

        /// <summary>Requests one mod may hold waiting at once over all queues (CoreAI: one mod cannot fill every queue).</summary>
        public int WaitingRequestsPerMod { get; set; } = MirrorQueueSize;

        /// <summary>The longest value, in characters of its JSON encoding (mirror: 4,194,304).</summary>
        public int MaxValueCharacters { get; set; } = MirrorValueLimit;

        /// <summary>Characters one world may keep over all its stores (keys, values and metadata).</summary>
        public long MaxStoredCharactersPerWorld { get; set; } = 64L * 1024L * 1024L;

        /// <summary>Keys one world may keep over all its stores.</summary>
        public int MaxKeysPerWorld { get; set; } = 16_384;

        /// <summary>Stores (name and scope pairs) one world may keep; each one is a file.</summary>
        public int MaxStoresPerWorld { get; set; } = 256;

        /// <summary>
        /// Seconds between the first unsaved change of a store and its write to storage; writes in between are
        /// coalesced into one. Every pending write is saved when the world closes.
        /// </summary>
        public double WriteDelaySeconds { get; set; } = 1d;

        /// <summary>Store files written in one frame at most, so a busy store never stalls a frame with many writes.</summary>
        public int MaxFilesWrittenPerFrame { get; set; } = 1;

        /// <summary>Seconds to wait before a failed write is tried again.</summary>
        public double RetryDelaySeconds { get; set; } = 5d;

        /// <summary>
        /// Maps a game namespace (the composition's mod store id) and a world id to the namespace the world's data
        /// lives in. Null uses <see cref="RbxDataStoreNamespaces.Compose"/>: worlds without an id share the game's data.
        /// </summary>
        public Func<string, string, string> Namespace { get; set; }

        /// <summary>The defaults for a desktop or console player.</summary>
        public static RbxDataStoreOptions Desktop()
        {
            return new RbxDataStoreOptions();
        }

        /// <summary>The defaults for a WebGL player: a browser's storage is small, so a world keeps at most 8 MiB.</summary>
        public static RbxDataStoreOptions WebGl()
        {
            return new RbxDataStoreOptions { MaxStoredCharactersPerWorld = 8L * 1024L * 1024L, MaxKeysPerWorld = 4_096 };
        }

        /// <summary>A copy, so one composition's change never reaches another's.</summary>
        public RbxDataStoreOptions Clone()
        {
            return (RbxDataStoreOptions)MemberwiseClone();
        }
    }

    /// <summary>The default namespace rule: what separates one game's and one world's data from another's.</summary>
    public static class RbxDataStoreNamespaces
    {
        /// <summary>
        /// The game namespace alone for a world without an id (the default world and every package saved from it);
        /// the game namespace and the world id for a world that has one, so such a world keeps data of its own.
        /// </summary>
        public static string Compose(string gameNamespace, string worldId)
        {
            string game = (gameNamespace ?? string.Empty).Trim();
            string world = (worldId ?? string.Empty).Trim();
            return world.Length == 0 ? game : game + "\u0000world:" + world;
        }
    }
}
