using CoreAI.Ai.LuaCs;
using CoreAI.Mods.WorldPackages;
using VContainer;

namespace CoreAI.Composition
{
    /// <summary>
    /// Follows the world a mods scope publishes. The first poll after the scope built answers its first world; a
    /// later poll answers again only after a startup restore, a confirmed load or an undo replaced it, so a play
    /// world module applies its part (the local player, the default content) once per world and never twice.
    /// </summary>
    /// <remarks>
    /// WHY polling and not an event: the session controller publishes a replacement world without one, and a
    /// reference compare per frame costs nothing.
    /// </remarks>
    public sealed class CoreAiPublishedWorldWatcher
    {
        private RbxWorldRuntimeSessionController _session;
        private LuaCsRbxApiBindings _seen;

        /// <summary>The session found by the last poll; null until a mods scope built one.</summary>
        public RbxWorldRuntimeSessionController Session => _session;

        /// <summary>
        /// Looks for <paramref name="modsScope"/>'s session and answers its published world when it is one this
        /// watcher has not seen. False while the scope has not built or nothing changed.
        /// </summary>
        public bool TryTakeNewWorld(CoreAiModsLifetimeScope modsScope, out LuaCsRbxApiBindings world)
        {
            world = null;
            if (_session == null)
            {
                IObjectResolver container = modsScope != null ? modsScope.Container : null;
                if (container == null)
                {
                    return false;
                }

                _session = container.ResolveOrDefault<RbxWorldRuntimeSessionController>();
            }

            return TryTakeNewWorld(_session, out world);
        }

        /// <summary>Answers <paramref name="session"/>'s published world when this watcher has not seen it.</summary>
        public bool TryTakeNewWorld(RbxWorldRuntimeSessionController session, out LuaCsRbxApiBindings world)
        {
            world = null;
            if (session == null)
            {
                return false;
            }

            _session = session;
            LuaCsRbxApiBindings current = session.CurrentRbxApi;
            if (current == null || ReferenceEquals(current, _seen))
            {
                return false;
            }

            _seen = current;
            world = current;
            return true;
        }
    }
}
