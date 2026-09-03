using System.Runtime.CompilerServices;

// WHY: the material catalog importers and their surface-profile table are internal to the
// editor assembly, and the QA tests that guard the tiling values live in CoreAI.Mods.Tests.
[assembly: InternalsVisibleTo("CoreAI.Mods.Tests")]
