using System;
using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// No-op <see cref="ISkillStore"/>: authored skills live only in the in-memory catalog and do not
    /// survive a restart. Used by minimal containers (tests, headless tools, platforms without a file
    /// system) so the authoring path still works without persistence.
    /// </summary>
    public sealed class NullSkillStore : ISkillStore
    {
        /// <inheritdoc />
        public void Save(SkillRecord record)
        {
        }

        /// <inheritdoc />
        public bool TryLoad(string id, out SkillRecord record)
        {
            record = null;
            return false;
        }

        /// <inheritdoc />
        public IReadOnlyList<SkillRecord> List()
        {
            return Array.Empty<SkillRecord>();
        }

        /// <inheritdoc />
        public void Delete(string id)
        {
        }
    }
}
