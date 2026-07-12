using CoreAI.ExampleGame.ArenaCombat.Infrastructure;
using CoreAI.ExampleGame.ArenaProgression.Domain;
using CoreAI.ExampleGame.ArenaProgression.Presenter;
using CoreAI.ExampleGame.ArenaProgression.UseCases;
using CoreAI.ExampleGame.ArenaProgression.View;
using Neo.Progression;
using UnityEngine;

namespace CoreAI.ExampleGame.ArenaProgression.Infrastructure
{
    /// <summary>Bootstraps meta progression, run stats, kill XP, upgrade drafts, and Lua bindings for one run.</summary>
    public sealed class ArenaProgressionSessionHost : MonoBehaviour
    {
        [Tooltip("Опционально: UI драфта. Без ссылки драфт доступен через Lua только если вид добавлен отдельно.")]
        [SerializeField]
        private ArenaUpgradeChoiceView draftView;

        private ArenaProgressionContent _content;
        private ArenaUnitBaselineConfig _baseline;
        private int _teamMemberCount = 1;

        private ArenaMetaProgressionState _meta;
        private ArenaTeamProgressionState _team;
        private ArenaRunCombatModel _combat;
        private ArenaUpgradeDraftPresenter _presenter;
        private ArenaMetaSaveGateway _gateway;
        private ArenaProgressionLuaBindings _luaBindings;
        private SaveMetaProgressionUseCase _saveMeta;
        private LoadMetaProgressionUseCase _loadMeta;
        private ArenaKillXpService _killXp;

        private ArenaPlayerHealth _playerHealth;
        private ArenaPlayerMelee _playerMelee;
        private ArenaCompanionBot _companion;

        public ArenaTeamProgressionState Team => _team;

        /// <summary>Session-scoped kill-XP boundary; inject into enemy spawns instead of reaching for a global.</summary>
        public IArenaKillXpService KillXp => _killXp;

        public LevelCurveDefinition SessionLevelCurve =>
            _content != null && _content.RunBalance != null ? _content.RunBalance.SessionLevelCurve : null;

        public void Configure(ArenaProgressionContent content, ArenaUnitBaselineConfig baseline,
            int aliveTeamMembersForXp)
        {
            _content = content;
            _baseline = baseline;
            _teamMemberCount = Mathf.Max(1, aliveTeamMembersForXp);
        }

        public void Init(ArenaPlayerHealth playerHealth, ArenaPlayerMelee playerMelee, ArenaCompanionBot companion)
        {
            _playerHealth = playerHealth;
            _playerMelee = playerMelee;
            _companion = companion;
        }

        public void Bootstrap()
        {
            if (_content == null || _baseline == null)
            {
                return;
            }

            ArenaRunBalanceConfig balance = _content.RunBalance;
            if (balance == null)
            {
                return;
            }

            _meta = new ArenaMetaProgressionState();
            _team = new ArenaTeamProgressionState();
            _gateway = new ArenaMetaSaveGateway(_content.Persistence);
            _loadMeta = new LoadMetaProgressionUseCase(_meta, _gateway, balance);
            _saveMeta = new SaveMetaProgressionUseCase(_meta, _gateway);
            _loadMeta.Execute();
            _team.ConfigureStart(balance.StartChoiceCount);

            _combat = new ArenaRunCombatModel(_baseline, _playerHealth, _playerMelee, _companion);
            AddSessionKillXpUseCase addSessionXp = new(_team, balance);
            _killXp = new ArenaKillXpService(addSessionXp, balance.BaseXpPerKill, _teamMemberCount);

            ArenaUpgradeRollService rollService = new(_content);
            RollUpgradeOffersUseCase roll = new(_team, rollService);
            ApplySelectedUpgradeUseCase apply = new(_team, _combat, balance, null);
            AddMetaXpUseCase addMetaXp = new(_meta, balance);

            HeuristicCompanionUpgradeBrain brain = new();
            _presenter = new ArenaUpgradeDraftPresenter(_team, roll, apply, brain, draftView);

            _luaBindings = new ArenaProgressionLuaBindings(
                _killXp,
                addMetaXp,
                _loadMeta,
                _saveMeta,
                apply,
                _content,
                balance,
                OpenDraftDebug);

            // TODO(lua-cs): host-side custom binding extensibility. The MoonSharp-era global
            // GameLuaBindingsExtensibility hook was removed with the VM. The Lua-CSharp gameplay
            // bindings are assembled inside LuaCsModRuntimeFactory; wiring a game's own
            // ILuaCsGameRuntimeBindings into that stack is a follow-up feature. The bindings object is
            // still constructed here as the reference API surface (see ArenaProgressionLuaBindings).
        }

        public void OpenDraftDebug()
        {
            _presenter?.OpenDraft();
        }

        private void OnDestroy()
        {
            _luaBindings = null;
            _killXp = null;
            _saveMeta?.Execute();
        }
    }
}
