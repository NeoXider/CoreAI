using System;
using System.Collections;
using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Live, thread-safe view of a role's skills that the <c>read_skill</c> / <c>call_skill_tool</c>
    /// meta-tools read from on every call. Unlike a plain <c>IReadOnlyList&lt;SkillSet&gt;</c> snapshot
    /// captured at registration, this catalog is mutated in place by the <see cref="SkillAuthoringCoordinator"/>
    /// when the model authors a skill through <c>manage_skills</c>, so a freshly created or updated skill
    /// becomes visible to the very same agent's <c>read_skill</c> catalog without re-registering tools.
    /// <para>
    /// It implements <see cref="IReadOnlyList{T}"/> over a defensive snapshot so existing proxy code that
    /// enumerates the list (e.g. <see cref="SkillSetToolResolver"/>) keeps working unchanged while always
    /// observing the latest set.
    /// </para>
    /// </summary>
    public sealed class MutableSkillCatalog : IReadOnlyList<SkillSet>
    {
        private readonly object _lock = new();
        private readonly List<SkillSet> _skills = new();
        private readonly Dictionary<string, int> _indexByName = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Creates an empty catalog.</summary>
        public MutableSkillCatalog()
        {
        }

        /// <summary>Creates a catalog seeded with the supplied skills (host-registered ones).</summary>
        public MutableSkillCatalog(IEnumerable<SkillSet> initial)
        {
            if (initial == null)
            {
                return;
            }

            foreach (SkillSet skill in initial)
            {
                AddOrReplace(skill);
            }
        }

        /// <summary>Adds a new skill or replaces an existing one with the same (case-insensitive) name.</summary>
        public void AddOrReplace(SkillSet skill)
        {
            if (skill == null || string.IsNullOrWhiteSpace(skill.Name))
            {
                return;
            }

            lock (_lock)
            {
                if (_indexByName.TryGetValue(skill.Name, out int existing))
                {
                    _skills[existing] = skill;
                    return;
                }

                _indexByName[skill.Name] = _skills.Count;
                _skills.Add(skill);
            }
        }

        /// <summary>Removes a skill by name. Returns true when a skill was removed.</summary>
        public bool Remove(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            lock (_lock)
            {
                if (!_indexByName.TryGetValue(name, out int index))
                {
                    return false;
                }

                _skills.RemoveAt(index);
                RebuildIndex();
                return true;
            }
        }

        /// <summary>Returns the skill registered under <paramref name="name"/> or null.</summary>
        public SkillSet Get(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            lock (_lock)
            {
                return _indexByName.TryGetValue(name, out int index) ? _skills[index] : null;
            }
        }

        private void RebuildIndex()
        {
            _indexByName.Clear();
            for (int i = 0; i < _skills.Count; i++)
            {
                SkillSet s = _skills[i];
                if (s != null && !string.IsNullOrWhiteSpace(s.Name))
                {
                    _indexByName[s.Name] = i;
                }
            }
        }

        private List<SkillSet> Snapshot()
        {
            lock (_lock)
            {
                return new List<SkillSet>(_skills);
            }
        }

        /// <inheritdoc />
        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _skills.Count;
                }
            }
        }

        /// <inheritdoc />
        public SkillSet this[int index]
        {
            get
            {
                lock (_lock)
                {
                    return _skills[index];
                }
            }
        }

        /// <inheritdoc />
        public IEnumerator<SkillSet> GetEnumerator()
        {
            return Snapshot().GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}