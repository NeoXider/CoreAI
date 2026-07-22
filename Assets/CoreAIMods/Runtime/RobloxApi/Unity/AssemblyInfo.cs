using System.Runtime.CompilerServices;

// WHY: the dual-scale EditMode runs (ROBLOX_API_ROADMAP.md §5.1.1) need the internal
// test-only scale reset hook; production code cannot touch it.
[assembly: InternalsVisibleTo("CoreAI.Mods.Tests")]
