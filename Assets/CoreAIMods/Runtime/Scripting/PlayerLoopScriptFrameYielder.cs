using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace CoreAI.Scripting
{
    /// <summary>
    /// Production <see cref="IScriptFrameYielder"/>: resumes on the next player-loop Update, which is the
    /// only loop that runs in every player including WebGL (a timer-backed delay does not fire there).
    /// </summary>
    public sealed class PlayerLoopScriptFrameYielder : IScriptFrameYielder
    {
        /// <summary>Shared instance; the yielder is stateless.</summary>
        public static readonly IScriptFrameYielder Instance = new PlayerLoopScriptFrameYielder();

        private PlayerLoopScriptFrameYielder()
        {
        }

        /// <inheritdoc />
        public async ValueTask YieldFrameAsync(CancellationToken cancellationToken)
        {
            if (!IsHostLoopRunning())
            {
                return;
            }

            await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);
        }

        // WHY: Edit Mode has no running player loop that an Edit Mode test's synchronously blocked call
        // stack would ever reach, so yielding there would hang the test instead of releasing a frame.
        // Completing immediately reproduces the pre-yield behaviour exactly. In a player the loop always
        // runs, so this is a constant true and costs nothing.
        private static bool IsHostLoopRunning()
        {
#if UNITY_EDITOR
            try
            {
                return Application.isPlaying;
            }
            catch
            {
                // WHY: Application.isPlaying throws off the Unity main thread in the Editor. A pooled
                // thread has no frame to release either, so the safe answer is the same "do not yield".
                return false;
            }
#else
            return true;
#endif
        }
    }
}
