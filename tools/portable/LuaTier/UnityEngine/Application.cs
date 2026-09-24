using System;
using System.Diagnostics;
using System.IO;

namespace UnityEngine
{
    /// <summary>
    /// Portable twin of the parts of <c>UnityEngine.Application</c> the Lua tier reads, answering as an
    /// editor that is not in play mode (the EditMode test situation).
    /// </summary>
    public static class Application
    {
        private static readonly object Gate = new object();
        private static string _persistentDataPath;
        private static string _dataPath;

        /// <summary>
        /// A per-process directory under the system temp folder, created on first read. Never the
        /// user's real Unity persistent data folder, so a test that forgets to inject a root cannot
        /// touch real saves. <see cref="PortableShim.DeletePersistentDataPath"/> removes it.
        /// </summary>
        public static string persistentDataPath
        {
            get
            {
                lock (Gate)
                {
                    if (_persistentDataPath == null)
                    {
                        string path = Path.Combine(Path.GetTempPath(),
                            "coreai-portable-lua-" + Process.GetCurrentProcess().Id + "-" +
                            Guid.NewGuid().ToString("N").Substring(0, 8));
                        Directory.CreateDirectory(path);
                        _persistentDataPath = path;
                    }

                    return _persistentDataPath;
                }
            }
        }

        /// <summary>
        /// The repository's <c>Assets</c> folder, which is what the editor reports for this project.
        /// Located by walking up from the test binaries to the checkout root, like the portable test
        /// setup does.
        /// </summary>
        public static string dataPath
        {
            get
            {
                lock (Gate)
                {
                    if (_dataPath == null)
                    {
                        _dataPath = Path.Combine(PortableShim.RepositoryRoot, "Assets").Replace('\\', '/');
                    }

                    return _dataPath;
                }
            }
        }

        /// <summary>False: EditMode tests run outside play mode.</summary>
        public static bool isPlaying => false;

        /// <summary>True: EditMode tests run inside the editor.</summary>
        public static bool isEditor => true;

        /// <summary>Linux editor, the platform this portable run stands in for.</summary>
        public static RuntimePlatform platform => RuntimePlatform.LinuxEditor;

        internal static string PersistentDataPathIfCreated
        {
            get
            {
                lock (Gate)
                {
                    return _persistentDataPath;
                }
            }
        }

        internal static void ForgetPersistentDataPath()
        {
            lock (Gate)
            {
                _persistentDataPath = null;
            }
        }
    }

    /// <summary>The subset of Unity's platform identifiers with Unity's numeric values.</summary>
    public enum RuntimePlatform
    {
        OSXEditor = 0,
        OSXPlayer = 1,
        WindowsPlayer = 2,
        WindowsEditor = 7,
        IPhonePlayer = 8,
        Android = 11,
        LinuxPlayer = 13,
        LinuxEditor = 16,
        WebGLPlayer = 17
    }

    /// <summary>Hooks the portable test assembly uses to manage what the shim created.</summary>
    public static class PortableShim
    {
        private static string _repositoryRoot;

        /// <summary>The checkout root (the folder holding <c>tools/portable/CoreAI.Core.csproj</c>).</summary>
        public static string RepositoryRoot
        {
            get
            {
                if (_repositoryRoot != null)
                {
                    return _repositoryRoot;
                }

                DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);
                while (directory != null &&
                       !File.Exists(Path.Combine(directory.FullName, "tools", "portable", "CoreAI.Core.csproj")))
                {
                    directory = directory.Parent;
                }

                if (directory == null)
                {
                    throw new DirectoryNotFoundException(
                        "The portable UnityEngine shim requires the CoreAI repository checkout.");
                }

                _repositoryRoot = directory.FullName;
                return _repositoryRoot;
            }
        }

        /// <summary>Deletes the per-process persistent data directory if anything created it.</summary>
        public static void DeletePersistentDataPath()
        {
            string path = Application.PersistentDataPathIfCreated;
            if (path == null)
            {
                return;
            }

            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException)
            {
                // WHY: best-effort cleanup of a temp folder; a file still held open must not fail the run.
            }
            catch (UnauthorizedAccessException)
            {
                // WHY: same as above; the folder lives under the system temp directory either way.
            }

            Application.ForgetPersistentDataPath();
        }
    }
}
