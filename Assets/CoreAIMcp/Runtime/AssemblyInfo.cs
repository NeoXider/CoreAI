using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("CoreAI.Mcp.Tests")]

// WHY: the Play Mode residency test drives the REAL server path — CoreAiMcpServer.BuildRegistry plus
// the dispatcher — instead of a hand-built registry, because that is exactly the half EditMode cannot
// prove. BuildRegistry is internal, so the Play Mode assembly needs the access the EditMode one has.
[assembly: InternalsVisibleTo("CoreAI.Mcp.PlayModeTests")]
