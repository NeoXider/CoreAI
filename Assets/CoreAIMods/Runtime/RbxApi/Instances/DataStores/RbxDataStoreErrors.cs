using System.Globalization;

namespace CoreAI.Mods.Rbx.Instances.DataStores
{
    /// <summary>
    /// The errors of <c>DataStoreService</c>. Each message starts with the Roblox error code and text from the
    /// mirror's error-code table (<c>cloud-services/data-stores/error-codes-and-limits.md</c>), so standard Roblox
    /// retry code that looks for the code or for "throttled" works; the CoreAI code and the fix around it are the
    /// structured format every Rbx error has. Messages the mirror does not give are CoreAI's and carry no number.
    /// </summary>
    public static class RbxDataStoreErrors
    {
        /// <summary>101 KeyNameEmpty.</summary>
        public static RbxError KeyEmpty()
        {
            return RbxError.BadArgument("101: Key name can't be empty.", "pass a non-empty key, e.g. \"Player_\" .. player.UserId");
        }

        /// <summary>102 KeyNameLimit.</summary>
        public static RbxError KeyTooLong(int length)
        {
            return RbxError.BadArgument(
                "102: Key name exceeds the " + RbxDataStoreOptions.MirrorNameLimit + " character limit. (" + length + " characters)",
                "use a key of at most " + RbxDataStoreOptions.MirrorNameLimit + " characters");
        }

        /// <summary>A data store name or scope that is empty or too long (the mirror gives the limit, not the text).</summary>
        public static RbxError NameInvalid(string what, int length)
        {
            if (length == 0)
            {
                return RbxError.BadArgument("DataStore " + what + " can't be empty.",
                    "pass a non-empty " + what + ", e.g. DataStoreService:GetDataStore(\"PlayerData\")");
            }

            return RbxError.BadArgument(
                "DataStore " + what + " exceeds the " + RbxDataStoreOptions.MirrorNameLimit + " character limit. ("
                + length + " characters)",
                "use a " + what + " of at most " + RbxDataStoreOptions.MirrorNameLimit + " characters");
        }

        /// <summary>A name, scope or key that is not valid text (a lone UTF-16 surrogate).</summary>
        public static RbxError NotText(string what)
        {
            return RbxError.BadArgument("DataStore " + what + " must be valid text.",
                "build the " + what + " from ordinary characters");
        }

        /// <summary>103 ValueNotAllowed: the value an update function returned has a type a store refuses.</summary>
        public static RbxError ValueNotAllowed(string typeName)
        {
            return RbxError.BadArgument("103: Can't allow " + typeName + " in DataStore.",
                "return nil, a boolean, a number, a string or a table of those from the update function");
        }

        /// <summary>104 CantStoreValue: a value that does not serialize; <paramref name="path"/> names where.</summary>
        public static RbxError CantStore(string typeName, string path)
        {
            return RbxError.BadArgument("104: Can't store " + typeName + " in DataStore. (at " + path + ")",
                "store only booleans, numbers, strings and tables that are either arrays or string-keyed dictionaries "
                + "of those; turn Instances and Vector3s into plain values first");
        }

        /// <summary>105 ValueTooLarge.</summary>
        public static RbxError ValueTooLarge(int limit)
        {
            string size = limit == RbxDataStoreOptions.MirrorValueLimit
                ? "4MB"
                : limit.ToString(CultureInfo.InvariantCulture) + " character";
            return new RbxError(RbxErrorCode.PayloadTooLarge, "105: Serialized value exceeds " + size + " limit.",
                "store less in one key: split the data over several keys, or check the size with HttpService:JSONEncode");
        }

        /// <summary>301-306: the request was dropped because its budget's queue was full.</summary>
        public static RbxError Throttled(string method)
        {
            string code;
            switch (method)
            {
                case "GetAsync":
                    code = "301";
                    break;
                case "SetAsync":
                    code = "302";
                    break;
                case "IncrementAsync":
                    code = "303";
                    break;
                case "UpdateAsync":
                    code = "304";
                    break;
                default:
                    code = "306";
                    break;
            }

            return new RbxError(RbxErrorCode.BudgetExceeded,
                code + ": " + method + " request dropped. Request was throttled but queue was full.",
                "send fewer requests: keep the player's data in a table, save it on leave and every few minutes, "
                + "and check DataStoreService:GetRequestBudgetForRequestType before a burst");
        }

        /// <summary>CoreAI's per-mod cap on waiting requests, worded like the throttle error so retry code handles it.</summary>
        public static RbxError ModThrottled(string method, string modId, int limit)
        {
            RbxError throttled = Throttled(method);
            return new RbxError(RbxErrorCode.BudgetExceeded,
                throttled.RawMessage + " (mod '" + modId + "' already has " + limit + " requests waiting)",
                throttled.Fix);
        }

        /// <summary>401 DataModelNoAccess: the world is closing.</summary>
        public static RbxError ShuttingDown()
        {
            return new RbxError(RbxErrorCode.WorldDetached,
                "401: Request failed. DataModel is inaccessible while the experience is shutting down.",
                "save from game:BindToClose or Players.PlayerRemoving before the world closes");
        }

        /// <summary>501: the stored file of a data store could not be read; it was set aside and the store starts empty.</summary>
        public static RbxError Corrupted(string storeName, string detail)
        {
            return new RbxError(RbxErrorCode.NotAuthority,
                "501: Can't parse response because data might be corrupted. (data store '" + storeName + "': " + detail + ")",
                "the unreadable file was kept beside the store for the host; treat the key as new or ask the player");
        }

        /// <summary>502 RequestRejected: the host's storage did not take the write.</summary>
        public static RbxError Rejected(string reason)
        {
            return new RbxError(RbxErrorCode.NotAuthority,
                "502: API Services rejected the request with error: " + reason,
                "retry later; the data written so far stays readable in this session");
        }

        /// <summary>511 AttributeSizeTooLarge.</summary>
        public static RbxError MetadataTooLarge(int limit)
        {
            return RbxError.BadArgument("511: Metadata attribute size exceeds " + limit + " limit.",
                "keep the metadata under " + limit + " characters, its keys under "
                + RbxDataStoreOptions.MirrorMetadataKeyLimit + " and each value under "
                + RbxDataStoreOptions.MirrorMetadataValueLimit);
        }

        /// <summary>512 UserIdLimitExceeded.</summary>
        public static RbxError UserIdsTooMany(int limit)
        {
            return RbxError.BadArgument("512: UserID size exceeds " + limit + " limit.",
                "tag a key with at most " + limit + " user ids");
        }

        /// <summary>513 AttributeFormatError for user ids.</summary>
        public static RbxError UserIdFormat()
        {
            return RbxError.BadArgument("513: Attribute userId format is invalid.",
                "pass an array of integer user ids, e.g. { player.UserId }");
        }

        /// <summary>513 AttributeFormatError for metadata.</summary>
        public static RbxError MetadataFormat()
        {
            return RbxError.BadArgument("513: Attribute metadata format is invalid.",
                "return a table with string keys as the third value of the update function");
        }

        /// <summary>CoreAI: a world keeps more than the host allows.</summary>
        public static RbxError StorageFull(string what, long limit)
        {
            return new RbxError(RbxErrorCode.BudgetExceeded,
                "DataStore storage of this world is full: it may keep at most " + limit.ToString(CultureInfo.InvariantCulture)
                + " " + what + ".",
                "store less: remove keys you no longer need with RemoveAsync, or keep one table per player instead of "
                + "many small keys");
        }

        /// <summary>IncrementAsync on a value that is not an integer (the mirror says both must be integers).</summary>
        public static RbxError NotAnInteger(string key)
        {
            return RbxError.BadArgument(
                "IncrementAsync can't increment key '" + key + "': its value is not an integer.",
                "IncrementAsync works on whole numbers only; use UpdateAsync for any other value");
        }

        /// <summary>A delta that is not a finite whole number.</summary>
        public static RbxError DeltaNotAnInteger()
        {
            return RbxError.BadArgument("IncrementAsync delta must be an integer.",
                "pass a whole number, e.g. store:IncrementAsync(key, 5)");
        }

        /// <summary>The script lacks the <c>Data</c> capability.</summary>
        public static RbxError CapabilityRequired(string member, string modId)
        {
            return new RbxError(RbxErrorCode.NotAuthority,
                member + " requires the Data capability, which was not granted to this mod",
                "grant the mod the Data capability (part of All; a header that lists its tiers adds Data)", modId);
        }

        /// <summary>A yielding request made where no scheduler thread can wait.</summary>
        public static RbxError NeedsThread(string member)
        {
            return new RbxError(RbxErrorCode.ContextViolation,
                member + " yields, so it must run on a script thread",
                "call it from the mod's chunk, a task.spawn function or a signal handler, never from coroutine.create");
        }

        /// <summary>The update function of UpdateAsync yielded.</summary>
        public static RbxError TransformYielded()
        {
            return RbxError.BadArgument("UpdateAsync: the update function cannot yield.",
                "compute the new value without task.wait or other yielding calls; it may run more than once");
        }

        /// <summary>A loud stub member of the data store surface.</summary>
        public static RbxError Planned(string feature, string workaround)
        {
            return RbxError.NotImplemented(feature, RbxDataStoreClasses.PlannedRung, workaround);
        }
    }
}
