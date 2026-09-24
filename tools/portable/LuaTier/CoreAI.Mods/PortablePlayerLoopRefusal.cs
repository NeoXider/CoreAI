using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace CoreAI.Portable
{
    /// <summary>
    /// Stands in for <c>UniTask.Yield(PlayerLoopTiming.Update, token)</c> in the derived copy of
    /// FileRbxWorldPackageStore.cs (see CoreAI.Mods.csproj). That overload parks the continuation on the
    /// Unity player loop, which does not exist in the portable suite, so the call refuses instead of
    /// resuming on some other scheduler.
    /// </summary>
    internal static class PortablePlayerLoopRefusal
    {
        /// <summary>Always throws <see cref="PortableEngineUnavailableException"/>.</summary>
        internal static UniTask Yield(CancellationToken cancellationToken)
        {
            throw new PortableEngineUnavailableException("PlayerLoop (UniTask.Yield(PlayerLoopTiming.Update))");
        }
    }
}
