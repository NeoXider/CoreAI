using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CoreAI.Ai
{
    /// <summary>
    /// Call-owned record of filesystem effects observed by legacy migration. Stores settle it through
    /// their own durability ports after releasing storage gates.
    /// </summary>
    internal sealed class MigrationReceipt
    {
        internal bool Mutated { get; private set; }

        internal void ObserveMutation()
        {
            Mutated = true;
        }
    }

    /// <summary>Versioned opaque disk keys and conservative legacy identity checks shared by file stores.</summary>
    internal static class AgentMemoryFileKey
    {
        private static readonly object MigrationGate = new();
        private static readonly UTF8Encoding KeyEncoding = new(false, true);

        internal static string FileStem(string roleId)
        {
            using SHA256 sha = SHA256.Create();
            byte[] digest = sha.ComputeHash(KeyEncoding.GetBytes(roleId ?? ""));
            return "key-v2-" + CoreAI.Audit.AuditHash.ByteArrayToHex(digest);
        }

        internal static string LegacyFileStem(string roleId)
        {
            string safe = string.Join("_", roleId.Split(Path.GetInvalidFileNameChars()));
            if (string.Equals(safe, roleId, StringComparison.Ordinal)) return safe;
            uint hash = 2166136261u;
            foreach (char character in roleId) hash = (hash ^ character) * 16777619u;
            return $"{safe}_{hash:x8}";
        }

        internal static bool CanAdoptUntaggedLegacy(string roleId, string path, string extension)
        {
            if (roleId == null || roleId == "." || roleId == ".." ||
                !string.Equals(LegacyFileStem(roleId), roleId, StringComparison.Ordinal) ||
                Regex.IsMatch(roleId, "_[0-9a-fA-F]{8}$") ||
                Regex.IsMatch(roleId, "^key-v2-[0-9a-fA-F]{64}$", RegexOptions.IgnoreCase)) return false;
            string expected = roleId + extension;
            string directory = Path.GetDirectoryName(path);
            if (!Directory.Exists(directory)) return false;
            foreach (string candidate in Directory.EnumerateFiles(directory))
                if (string.Equals(Path.GetFileName(candidate), expected, StringComparison.Ordinal)) return true;
            return false;
        }

        internal static string CanonicalPath(string directory, string roleId, string extension)
        {
            return Path.Combine(directory, FileStem(roleId) + extension);
        }

        internal static void MigrateLegacy(string directory, string roleId, string extension, Action<string> validate)
        {
            MigrateLegacy(directory, roleId, extension, validate, null, null);
        }

        /// <summary>
        /// Migrates one legacy file and records every observed storage effect in <paramref name="receipt"/>.
        /// <paramref name="beforeMutation"/> runs under the migration gate immediately before the first
        /// effect, so a caller that cannot confirm synchronously can refuse without changing storage.
        /// </summary>
        internal static void MigrateLegacy(string directory, string roleId, string extension, Action<string> validate,
            MigrationReceipt receipt, Action beforeMutation)
        {
            string canonical = CanonicalPath(directory, roleId, extension);
            string legacy = Path.Combine(directory, LegacyFileStem(roleId) + extension);
            lock (MigrationGate)
            {
                if (!File.Exists(legacy)) return;
                validate(legacy);
                string backup = legacy + ".legacy";
                if (File.Exists(backup))
                    throw new InvalidDataException("Legacy storage has an ambiguous migration backup; explicit identity repair is required.");
                bool canonicalExisted = File.Exists(canonical);
                if (canonicalExisted)
                {
                    byte[] oldBytes = File.ReadAllBytes(legacy);
                    byte[] currentBytes = File.ReadAllBytes(canonical);
                    if (oldBytes.Length != currentBytes.Length)
                        throw new InvalidDataException("Canonical and legacy storage disagree; both files were preserved.");
                    for (int index = 0; index < oldBytes.Length; index++)
                        if (oldBytes[index] != currentBytes[index])
                            throw new InvalidDataException("Canonical and legacy storage disagree; both files were preserved.");
                    beforeMutation?.Invoke();
                }
                else
                {
                    beforeMutation?.Invoke();
                    try
                    {
                        File.Copy(legacy, canonical, false);
                        receipt?.ObserveMutation();
                    }
                    catch (Exception)
                    {
                        // WHY: the original copy error is the caller's failure, but a destination that was
                        // absent before and is observably present afterwards is still a storage effect that
                        // needs confirmation. Never delete or overwrite it to make the operation look clean.
                        if (!canonicalExisted && File.Exists(canonical))
                            receipt?.ObserveMutation();
                        throw;
                    }
                }
                File.Move(legacy, backup);
                receipt?.ObserveMutation();
            }
        }
    }
}
