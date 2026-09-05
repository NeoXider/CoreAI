using System.Runtime.CompilerServices;

// WHY the Mirror transport sees internals: RbxNetworkRequestResponder is deliberately not publicly
// constructible, so a mod cannot fabricate one and answer a request nobody asked. A first-party
// transport implementing INetworkBridge is exactly the caller that must be able to, and it is the
// only assembly granted this — the restriction stays where it matters.
[assembly: InternalsVisibleTo("CoreAI.Net.Mirror")]
[assembly: InternalsVisibleTo("CoreAI.Net.Mirror.Tests")]
