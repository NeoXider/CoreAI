using System.Runtime.CompilerServices;

// WHY the tests see internals: the bridge's receive paths take a connection id rather than a Mirror
// object precisely so its rules can be proven without standing up a transport. Keeping them internal
// means no host composition can reach past the message handlers to inject traffic.
[assembly: InternalsVisibleTo("CoreAI.Net.Mirror.Tests")]
