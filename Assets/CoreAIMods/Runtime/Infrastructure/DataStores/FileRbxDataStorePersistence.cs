using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CoreAI.Mods.Rbx.Instances.DataStores;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// The game's storage of <c>DataStoreService</c> data: one JSON file per data store (a name and a scope) under
    /// <c>CoreAI/DataStores</c> below <see cref="CoreAiStorageRoot"/>, in the namespace folder the one storage naming rule
    /// gives (<see cref="LuaModStoreId.ApplyTo"/>: none for the main game, <c>Stores/s-&lt;26&gt;</c> for a namespace),
    /// named <c>s-&lt;26&gt;.json</c> after the store (<see cref="CoreAiStorageNames.StoreFolderName"/> of the name and the
    /// scope). Writes are atomic (a temporary file swapped into place) and confirm durability through
    /// <see cref="CoreAiWebGlPersistence.Sync"/>, so on WebGL the engine's automatic <c>persistentDataPath</c>
    /// persistence keeps them in IndexedDB and a page that did not arm it is a failed write, never a silent loss.
    /// </summary>
    /// <remarks>
    /// The longest path below the storage root is 92 characters with a namespace
    /// (<c>\CoreAI\DataStores\Stores\s-&lt;26&gt;\s-&lt;26&gt;.json.tmp</c>), inside the 138 every store fits from a root
    /// of 121 characters. A file that cannot be read is renamed to <c>.json.corrupt</c> (kept for the host) and
    /// reported; its store starts empty.
    /// </remarks>
    public sealed class FileRbxDataStorePersistence : IRbxDataStorePersistence
    {
        /// <summary>The folder below <see cref="CoreAiStorageRoot.CoreAiDirectory"/>.</summary>
        public const string FolderName = "DataStores";

        /// <summary>The format tag every file carries.</summary>
        public const string FormatTag = "coreai-datastore-1";

        private const string FileExtension = ".json";
        private const string CorruptExtension = ".corrupt";
        private const int WindowsMaximumPathLength = 259;

        private static readonly UTF8Encoding Utf8 = new(false);

        private readonly string _root;

        /// <summary>Replaces the WebGL durability answer in tests; null asks <see cref="CoreAiWebGlPersistence.Sync"/>.</summary>
        internal static Func<bool> PersistenceArmingForTesting;

        /// <summary>Creates the storage.</summary>
        /// <param name="rootDirectory">An explicit folder; null uses <c>CoreAI/DataStores</c> below the storage root, resolved now.</param>
        public FileRbxDataStorePersistence(string rootDirectory = null)
        {
            _root = !string.IsNullOrWhiteSpace(rootDirectory)
                ? rootDirectory.Trim()
                : CoreAiStorageRoot.Combine(FolderName);
        }

        /// <summary>The folder the namespaces live in.</summary>
        public string RootDirectory => _root;

        /// <inheritdoc />
        public string FileIdFor(string name, string scope)
        {
            return CoreAiStorageNames.StoreFolderName((name ?? string.Empty) + "\n" + (scope ?? string.Empty));
        }

        /// <summary>The folder of <paramref name="dataNamespace"/>.</summary>
        public string DirectoryFor(string dataNamespace)
        {
            return LuaModStoreId.ApplyTo(_root, dataNamespace);
        }

        /// <summary>The file of one store.</summary>
        public string PathFor(string dataNamespace, string name, string scope)
        {
            return Path.Combine(DirectoryFor(dataNamespace), FileIdFor(name, scope) + FileExtension);
        }

        /// <inheritdoc />
        public IReadOnlyList<RbxDataStoreLoadResult> LoadNamespace(string dataNamespace)
        {
            List<RbxDataStoreLoadResult> results = new();
            string directory = DirectoryFor(dataNamespace);
            if (!Directory.Exists(directory))
            {
                return results;
            }

            string[] files = Directory.GetFiles(directory, "*" + FileExtension);
            Array.Sort(files, StringComparer.Ordinal);
            foreach (string path in files)
            {
                if (!path.EndsWith(FileExtension, StringComparison.Ordinal))
                {
                    continue;
                }

                string fileId = Path.GetFileNameWithoutExtension(path);
                RbxDataStoreFile file;
                try
                {
                    file = Parse(File.ReadAllText(path, Utf8));
                    if (!string.Equals(FileIdFor(file.Name, file.Scope), fileId, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("the file names a store other than the one its name is made from");
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is JsonException || ex is InvalidDataException
                                           || ex is UnauthorizedAccessException || ex is FormatException
                                           || ex is OverflowException || ex is ArgumentException
                                           || ex is InvalidCastException)
                {
                    results.Add(new RbxDataStoreLoadResult(fileId, RbxDataStoreLoadStatus.Corrupt, null,
                        SetAside(path, ex)));
                    continue;
                }

                results.Add(new RbxDataStoreLoadResult(fileId, RbxDataStoreLoadStatus.Loaded, file, null));
            }

            return results;
        }

        /// <inheritdoc />
        public void Save(string dataNamespace, RbxDataStoreFile file)
        {
            if (file == null)
            {
                throw new ArgumentNullException(nameof(file));
            }

            string path = PathFor(dataNamespace, file.Name, file.Scope);
            string temporary = path + ".tmp";
            EnsureWindowsPathLength(temporary);
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(temporary, Serialize(file), Utf8);
            try
            {
                if (File.Exists(path))
                {
                    File.Replace(temporary, path, null);
                }
                else
                {
                    File.Move(temporary, path);
                }
            }
            catch (Exception)
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }

                throw;
            }

            RequestPersistenceArming();
        }

        /// <inheritdoc />
        public void Delete(string dataNamespace, string name, string scope)
        {
            string path = PathFor(dataNamespace, name, scope);
            if (!File.Exists(path))
            {
                return;
            }

            File.Delete(path);
            RequestPersistenceArming();
        }

        /// <summary>The file text of <paramref name="file"/>; non-ASCII text is escaped so every UTF-16 value survives.</summary>
        internal static string Serialize(RbxDataStoreFile file)
        {
            StringBuilder builder = new(256);
            using (StringWriter writer = new(builder, CultureInfo.InvariantCulture))
            using (JsonTextWriter json = new(writer))
            {
                json.Formatting = Formatting.Indented;
                json.StringEscapeHandling = StringEscapeHandling.EscapeNonAscii;
                json.WriteStartObject();
                json.WritePropertyName("format");
                json.WriteValue(FormatTag);
                json.WritePropertyName("name");
                json.WriteValue(file.Name ?? string.Empty);
                json.WritePropertyName("scope");
                json.WriteValue(file.Scope ?? string.Empty);
                json.WritePropertyName("entries");
                json.WriteStartArray();
                foreach (RbxDataStoreEntry entry in file.Entries)
                {
                    json.WriteStartObject();
                    json.WritePropertyName("key");
                    json.WriteValue(entry.Key);
                    json.WritePropertyName("value");
                    json.WriteValue(entry.ValueJson);
                    json.WritePropertyName("created");
                    json.WriteValue(entry.CreatedMilliseconds);
                    json.WritePropertyName("updated");
                    json.WriteValue(entry.UpdatedMilliseconds);
                    json.WritePropertyName("version");
                    json.WriteValue(entry.Version);
                    json.WritePropertyName("userIds");
                    json.WriteStartArray();
                    long[] ids = entry.UserIds ?? Array.Empty<long>();
                    for (int index = 0; index < ids.Length; index++)
                    {
                        json.WriteValue(ids[index]);
                    }

                    json.WriteEndArray();
                    json.WritePropertyName("metadata");
                    json.WriteValue(entry.MetadataJson ?? "{}");
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            return builder.ToString();
        }

        /// <summary>Reads a file text back; throws when it is not exactly what <see cref="Serialize"/> writes.</summary>
        internal static RbxDataStoreFile Parse(string text)
        {
            JObject root;
            using (StringReader reader = new(text))
            using (JsonTextReader json = new(reader) { DateParseHandling = DateParseHandling.None, MaxDepth = 8 })
            {
                root = JObject.Load(json);
                if (json.Read())
                {
                    throw new InvalidDataException("text follows the document");
                }
            }

            if (!string.Equals(RequireString(root, "format"), FormatTag, StringComparison.Ordinal))
            {
                throw new InvalidDataException("the format is not " + FormatTag);
            }

            RbxDataStoreFile file = new() { Name = RequireString(root, "name"), Scope = RequireString(root, "scope") };
            if (!(root["entries"] is JArray entries))
            {
                throw new InvalidDataException("entries is not an array");
            }

            HashSet<string> keys = new(StringComparer.Ordinal);
            foreach (JToken token in entries)
            {
                if (!(token is JObject item))
                {
                    throw new InvalidDataException("an entry is not an object");
                }

                string key = RequireString(item, "key");
                if (key.Length == 0 || key.Length > RbxDataStoreOptions.MirrorNameLimit || !keys.Add(key))
                {
                    throw new InvalidDataException("an entry has an empty, too long or repeated key");
                }

                RbxDataStoreEntry entry = new()
                {
                    Key = key,
                    ValueJson = RequireString(item, "value"),
                    CreatedMilliseconds = RequireInteger(item, "created"),
                    UpdatedMilliseconds = RequireInteger(item, "updated"),
                    Version = RequireInteger(item, "version"),
                    MetadataJson = item["metadata"] == null ? "{}" : RequireString(item, "metadata")
                };

                if (item["userIds"] is JArray ids)
                {
                    long[] userIds = new long[ids.Count];
                    for (int index = 0; index < ids.Count; index++)
                    {
                        if (ids[index].Type != JTokenType.Integer)
                        {
                            throw new InvalidDataException("a user id is not an integer");
                        }

                        userIds[index] = ids[index].Value<long>();
                    }

                    entry.UserIds = userIds;
                }

                file.Entries.Add(entry);
            }

            return file;
        }

        private static string RequireString(JObject owner, string property)
        {
            JToken token = owner[property];
            if (token == null || token.Type != JTokenType.String)
            {
                throw new InvalidDataException("'" + property + "' is missing or not a string");
            }

            return token.Value<string>();
        }

        private static long RequireInteger(JObject owner, string property)
        {
            JToken token = owner[property];
            if (token == null || token.Type != JTokenType.Integer)
            {
                throw new InvalidDataException("'" + property + "' is missing or not an integer");
            }

            return token.Value<long>();
        }

        private static string SetAside(string path, Exception reason)
        {
            string detail = path + " could not be read (" + (string.IsNullOrWhiteSpace(reason.Message)
                ? reason.GetType().Name
                : reason.Message) + ")";
            try
            {
                string target = path + CorruptExtension;
                for (int attempt = 2; File.Exists(target); attempt++)
                {
                    target = path + CorruptExtension + attempt.ToString(CultureInfo.InvariantCulture);
                }

                File.Move(path, target);
                RequestPersistenceArming();
                return detail + "; it was kept as " + target;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return detail + "; it could not be set aside (" + ex.Message + ") and stays where it is";
            }
        }

        private void EnsureWindowsPathLength(string path)
        {
            if (Path.DirectorySeparatorChar != '\\')
            {
                return;
            }

            string full = Path.GetFullPath(path);
            if (full.Length > WindowsMaximumPathLength)
            {
                throw new PathTooLongException("The DataStore folder '" + _root + "' requires a Windows path of "
                                               + full.Length + " characters; at most " + WindowsMaximumPathLength
                                               + " are supported. Point CoreAiStorageRoot.Override at a shorter folder, "
                                               + "or shorten the company or product name the player's "
                                               + "persistentDataPath is built from.");
            }
        }

        private static void RequestPersistenceArming()
        {
            if (!(PersistenceArmingForTesting?.Invoke() ?? CoreAiWebGlPersistence.Sync()))
            {
                throw new IOException(
                    "the data was written locally, but this page did not arm automatic persistence, so it will not "
                    + "survive a reload");
            }
        }
    }
}
