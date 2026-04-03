using System;
using CoreAI.Ai;
using CoreAI.ExampleGame.ArenaProgression.UseCases;
using CoreAI.Infrastructure.Lua;
using CoreAI.Sandbox;
using UnityEngine;

namespace CoreAI.ExampleGame.ArenaProgression.Infrastructure
{
    /// <summary>Lua API прогрессии арены (см. <c>Docs/ARENA_PROGRESSION.md</c>).</summary>
    public sealed class ArenaProgressionLuaBindings : IGameLuaRuntimeBindings
    {
        private readonly IAddSessionKillXpUseCase _addSessionKillXp;
        private readonly IAddMetaXpUseCase _addMetaXp;
        private readonly ILoadMetaProgressionUseCase _loadMeta;
        private readonly ISaveMetaProgressionUseCase _saveMeta;
        private readonly IApplySelectedUpgradeUseCase _apply;
        private readonly ArenaProgressionContent _content;
        private readonly ArenaRunBalanceConfig _balance;
        private readonly Action _openDraftDebug;

        public ArenaProgressionLuaBindings(
            IAddSessionKillXpUseCase addSessionKillXp,
            IAddMetaXpUseCase addMetaXp,
            ILoadMetaProgressionUseCase loadMeta,
            ISaveMetaProgressionUseCase saveMeta,
            IApplySelectedUpgradeUseCase apply,
            ArenaProgressionContent content,
            ArenaRunBalanceConfig balance,
            Action openDraftDebug)
        {
            _addSessionKillXp = addSessionKillXp;
            _addMetaXp = addMetaXp;
            _loadMeta = loadMeta;
            _saveMeta = saveMeta;
            _apply = apply;
            _content = content;
            _balance = balance;
            _openDraftDebug = openDraftDebug;
        }

        public void RegisterGameplayApis(LuaApiRegistry registry)
        {
            registry.Register("arena_add_session_xp", (Action<object>)(v =>
            {
                int n = ToInt(v);
                if (n <= 0)
                    return;
                int alive = Mathf.Max(1, ArenaProgressionRuntimeHub.AliveTeamMembersForXp);
                _addSessionKillXp?.Execute(n, alive);
            }));

            registry.Register("arena_add_meta_xp", (Action<object>)(v =>
            {
                int n = ToInt(v);
                if (n > 0)
                    _addMetaXp?.Execute(n);
            }));

            registry.Register("arena_save_meta", (Action)(() => _saveMeta?.Execute()));
            registry.Register("arena_load_meta", (Action)(() => _loadMeta?.Execute()));

            registry.Register("arena_apply_upgrade_id", (Action<object>)(idObj =>
            {
                var id = idObj?.ToString();
                if (string.IsNullOrEmpty(id) || _content?.Upgrades == null || _balance == null)
                    return;
                ArenaUpgradeDefinition def = null;
                for (int i = 0; i < _content.Upgrades.Count; i++)
                {
                    var u = _content.Upgrades[i];
                    if (u != null && u.Id == id)
                    {
                        def = u;
                        break;
                    }
                }

                if (def == null)
                    return;
                var rarity = def.Rarity;
                float mult = _balance.GetStatMultiplier(rarity);
                var offer = new ArenaUpgradeOffer(def, rarity, mult);
                _apply?.Execute(offer, applyToCompanionToo: true);
            }));

            registry.Register("arena_open_draft_debug", (Action)(() => _openDraftDebug?.Invoke()));
        }

        private static int ToInt(object v)
        {
            if (v == null)
                return 0;
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
