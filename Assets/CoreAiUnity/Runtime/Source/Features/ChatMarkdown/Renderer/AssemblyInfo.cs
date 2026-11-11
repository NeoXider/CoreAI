using System.Runtime.CompilerServices;
using UnityEngine.Scripting;

// WHY: nothing references this assembly by type (it plugs in through CoreAiMarkdownRendererRegistry), so the managed
// code stripper would drop it from IL2CPP and WebGL players without this attribute.
[assembly: AlwaysLinkAssembly]
[assembly: InternalsVisibleTo("CoreAI.ChatMarkdown.Renderer.Tests")]
