using System;
using System.Collections.Generic;

namespace CoreAI.Infrastructure.Luau
{
    /// <summary>
    /// What the export lint checks a script against: what Roblox has (read from the offline mirror,
    /// <see cref="LuauRobloxSurfaceData"/>) and what CoreAI adds that Roblox does not have. <see cref="Default"/>
    /// knows CoreAI's surface by its name families and named members; a host that can enumerate the environment it
    /// really builds passes the names to <see cref="FromEnvironment"/>, so the list follows the code.
    /// </summary>
    public sealed class LuauExportLintSurface
    {
        /// <summary>
        /// Prefixes of CoreAI's own globals: the classic mod-core and Unity reflection functions
        /// (<c>hooks_every</c>, <c>coreai_world_spawn</c>, <c>unity_find</c>, ...) and the sandbox's internal names.
        /// </summary>
        public static readonly string[] DefaultCoreAiGlobalPrefixes =
        {
            "coreai_", "unity_", "hooks_", "mods_", "store_", "events_", "input_", "logic_", "time_", "camera_", "__"
        };

        /// <summary>CoreAI's own globals that no prefix covers.</summary>
        public static readonly string[] DefaultCoreAiGlobals = { "report", "mod_id", "add", "UserInputService" };

        /// <summary>
        /// CoreAI's own services (DEV-60 <c>AIService</c>, DEV-69 <c>UxmlService</c>); every <c>UiScreen</c>,
        /// <c>UiElement</c> and agent value comes from one of them.
        /// </summary>
        public static readonly string[] DefaultCoreAiServices = { "AIService", "UxmlService" };

        /// <summary>
        /// Members Roblox gives only to plugins and Studio (<c>security: PluginSecurity</c>) that CoreAI lets the
        /// elevated tier call at runtime (DEV-14), as <c>Class:Method</c>.
        /// </summary>
        public static readonly string[] DefaultElevatedMembers = { "ScriptContext:SetTimeout" };

        /// <summary>The marker of a CoreAI mod header comment (<c>-- @coreai capabilities: WorldEdit</c>).</summary>
        public const string HeaderMarker = "@coreai";

        private static readonly LuauExportLintSurface DefaultSurface = new(DefaultCoreAiGlobalPrefixes,
            DefaultCoreAiGlobals, DefaultCoreAiServices, DefaultElevatedMembers);

        private readonly string[] _coreAiPrefixes;
        private readonly HashSet<string> _coreAiGlobals;
        private readonly HashSet<string> _coreAiServices;
        private readonly HashSet<string> _elevatedMethods;
        private readonly Dictionary<string, string> _elevatedMembers = new(StringComparer.Ordinal);

        private static readonly HashSet<string> RobloxGlobalNames = BuildRobloxGlobals();
        private static readonly HashSet<string> RobloxServiceNames = new(LuauRobloxSurfaceData.Services, StringComparer.Ordinal);
        private static readonly Dictionary<string, HashSet<string>> RobloxLibraryMembers = BuildLibraries();

        private LuauExportLintSurface(IEnumerable<string> coreAiPrefixes, IEnumerable<string> coreAiGlobals,
            IEnumerable<string> coreAiServices, IEnumerable<string> elevatedMembers)
        {
            _coreAiPrefixes = new List<string>(coreAiPrefixes ?? Array.Empty<string>()).ToArray();
            _coreAiGlobals = new HashSet<string>(coreAiGlobals ?? Array.Empty<string>(), StringComparer.Ordinal);
            _coreAiServices = new HashSet<string>(coreAiServices ?? Array.Empty<string>(), StringComparer.Ordinal);
            _elevatedMethods = new HashSet<string>(StringComparer.Ordinal);
            foreach (string member in elevatedMembers ?? Array.Empty<string>())
            {
                int colon = member.IndexOf(':');
                string method = colon >= 0 ? member.Substring(colon + 1) : member;
                _elevatedMethods.Add(method);
                _elevatedMembers[method] = member;
            }
        }

        /// <summary>CoreAI's surface as this package ships it.</summary>
        public static LuauExportLintSurface Default => DefaultSurface;

        /// <summary>The Roblox mirror version the Roblox side was read from.</summary>
        public static string RobloxMirrorVersion => LuauRobloxSurfaceData.MirrorVersion;

        /// <summary>
        /// The surface of an environment a host built: every global name and service name in it that Roblox does not
        /// have is CoreAI's, together with <see cref="Default"/>'s families and the elevated members given.
        /// </summary>
        public static LuauExportLintSurface FromEnvironment(IEnumerable<string> globalNames,
            IEnumerable<string> serviceNames, IEnumerable<string> elevatedMembers = null)
        {
            List<string> globals = new(DefaultCoreAiGlobals);
            foreach (string name in globalNames ?? Array.Empty<string>())
            {
                if (!string.IsNullOrEmpty(name) && !RobloxGlobalNames.Contains(name))
                {
                    globals.Add(name);
                }
            }

            List<string> services = new(DefaultCoreAiServices);
            foreach (string name in serviceNames ?? Array.Empty<string>())
            {
                if (!string.IsNullOrEmpty(name) && !RobloxServiceNames.Contains(name))
                {
                    services.Add(name);
                }
            }

            List<string> elevated = new(DefaultElevatedMembers);
            elevated.AddRange(elevatedMembers ?? Array.Empty<string>());
            return new LuauExportLintSurface(DefaultCoreAiGlobalPrefixes, globals, services, elevated);
        }

        /// <summary>True when Roblox has a global, library table or datatype named <paramref name="name"/>.</summary>
        public static bool IsRobloxGlobal(string name)
        {
            return name != null && RobloxGlobalNames.Contains(name);
        }

        /// <summary>True when the API dump has a service class named <paramref name="name"/>.</summary>
        public static bool IsRobloxService(string name)
        {
            return name != null && RobloxServiceNames.Contains(name);
        }

        /// <summary>True when <paramref name="library"/> is a Luau library; <paramref name="members"/> holds its members.</summary>
        public static bool TryGetLibrary(string library, out IReadOnlyCollection<string> members)
        {
            if (library != null && RobloxLibraryMembers.TryGetValue(library, out HashSet<string> set))
            {
                members = set;
                return true;
            }

            members = null;
            return false;
        }

        /// <summary>True when <paramref name="name"/> is a CoreAI global that Roblox does not have.</summary>
        public bool IsCoreAiGlobal(string name)
        {
            if (string.IsNullOrEmpty(name) || RobloxGlobalNames.Contains(name))
            {
                return false;
            }

            if (_coreAiGlobals.Contains(name))
            {
                return true;
            }

            for (int i = 0; i < _coreAiPrefixes.Length; i++)
            {
                if (name.StartsWith(_coreAiPrefixes[i], StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>True when <paramref name="name"/> is a CoreAI service that Roblox does not have.</summary>
        public bool IsCoreAiService(string name)
        {
            return name != null && _coreAiServices.Contains(name);
        }

        /// <summary>
        /// True when <paramref name="method"/> is the method of a member only the elevated tier may call
        /// (DEV-14); <paramref name="member"/> is its <c>Class:Method</c> name.
        /// </summary>
        public bool IsElevatedMethod(string method, out string member)
        {
            member = null;
            return method != null && _elevatedMethods.Contains(method) && _elevatedMembers.TryGetValue(method, out member);
        }

        private static HashSet<string> BuildRobloxGlobals()
        {
            HashSet<string> names = new(LuauRobloxSurfaceData.Globals, StringComparer.Ordinal);
            names.UnionWith(LuauRobloxSurfaceData.Datatypes);
            return names;
        }

        private static Dictionary<string, HashSet<string>> BuildLibraries()
        {
            Dictionary<string, HashSet<string>> libraries = new(StringComparer.Ordinal);
            foreach (string[] entry in LuauRobloxSurfaceData.LibraryMembers)
            {
                HashSet<string> members = new(StringComparer.Ordinal);
                for (int i = 1; i < entry.Length; i++)
                {
                    members.Add(entry[i]);
                }

                libraries[entry[0]] = members;
            }

            return libraries;
        }
    }
}
