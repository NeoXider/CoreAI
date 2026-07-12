using System;
using CoreAI.Ai.LuaCs;
using CoreAI.ExampleGame.ArenaProgression.Domain;
using CoreAI.ExampleGame.ArenaProgression.UseCases;
using CoreAI.Sandbox.LuaCs;

namespace CoreAI.ExampleGame.ArenaProgression.Infrastructure
{
    /// <summary>Lua API surface for arena progression; see <c>Docs/ARENA_PROGRESSION.md</c>.</summary>
    public sealed class ArenaProgressionLuaBindings : ILuaCsGameRuntimeBindings
    {
        private readonly IArenaKillXpService _killXp;
        private readonly IAddMetaXpUseCase _addMetaXp;
        private readonly ILoadMetaProgressionUseCase _loadMeta;
        private readonly ISaveMetaProgressionUseCase _saveMeta;
        private readonly IApplySelectedUpgradeUseCase _apply;
        private readonly ArenaProgressionContent _content;
        private readonly ArenaRunBalanceConfig _balance;
        private readonly Action _openDraftDebug;

        public ArenaProgressionLuaBindings(
            IArenaKillXpService killXp,
            IAddMetaXpUseCase addMetaXp,
            ILoadMetaProgressionUseCase loadMeta,
            ISaveMetaProgressionUseCase saveMeta,
            IApplySelectedUpgradeUseCase apply,
            ArenaProgressionContent content,
            ArenaRunBalanceConfig balance,
            Action openDraftDebug)
        {
            _killXp = killXp;
            _addMetaXp = addMetaXp;
            _loadMeta = loadMeta;
            _saveMeta = saveMeta;
            _apply = apply;
            _content = content;
            _balance = balance;
            _openDraftDebug = openDraftDebug;
        }

        public void RegisterGameplayApis(LuaCsApiRegistry registry)
        {
            registry.Register("arena_add_session_xp", (Action<object>)(v => _killXp?.AwardXp(ToInt(v))));

            registry.Register("arena_add_meta_xp", (Action<object>)(v =>
            {
                int n = ToInt(v);
                if (n > 0)
                {
                    _addMetaXp?.Execute(n);
                }
            }));

            registry.Register("arena_save_meta", (Action)(() => _saveMeta?.Execute()));
            registry.Register("arena_load_meta", (Action)(() => _loadMeta?.Execute()));

            registry.Register("arena_apply_upgrade_id", (Action<object>)(idObj =>
            {
                string id = idObj?.ToString();
                if (string.IsNullOrEmpty(id) || _content?.Upgrades == null || _balance == null)
                {
                    return;
                }

                ArenaUpgradeDefinition def = null;
                for (int i = 0; i < _content.Upgrades.Count; i++)
                {
                    ArenaUpgradeDefinition u = _content.Upgrades[i];
                    if (u != null && u.Id == id)
                    {
                        def = u;
                        break;
                    }
                }

                if (def == null)
                {
                    return;
                }

                ArenaRarity rarity = def.Rarity;
                float mult = _balance.GetStatMultiplier(rarity);
                ArenaUpgradeOffer offer = new(def, rarity, mult);
                _apply?.Execute(offer, true);
            }));

            registry.Register("arena_open_draft_debug", (Action)(() => _openDraftDebug?.Invoke()));
        }

        private static int ToInt(object v)
        {
            if (v == null)
            {
                return 0;
            }

            try
            {
                return Convert.ToInt32(v);
            }
            catch
            {
                return 0;
            }
        }
    }
}
