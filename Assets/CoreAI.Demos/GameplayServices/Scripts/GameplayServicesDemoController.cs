using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Replication;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using VContainer;

namespace CoreAI.Demos.GameplayServices
{
    /// <summary>
    /// Drives the MVP8 gameplay-services tour: one Lua script, every service it touches shown live.
    /// </summary>
    /// <remarks>
    /// WHY this demo exists at all: the compatibility corpus proves these idioms run, but a passing
    /// test is not something anyone can watch. This scene is the same idioms with a body — a door
    /// that opens, a brick that kills, a coin that scores, a ray that finds the floor — so the claim
    /// "a Roblox developer's code runs here" can be checked by looking rather than by reading a
    /// test name.
    /// <para>
    /// The UI is drawn on a real Canvas. Immediate-mode UI renders nothing in a build, which is
    /// exactly where a demo has to work.
    /// </para>
    /// </remarks>
    [AddComponentMenu("CoreAI/Demos/Gameplay Services Demo Controller")]
    public sealed class GameplayServicesDemoController : MonoBehaviour
    {
        private const string ModId = "gameplay-services-tour";
        private const string StatusAttribute = "DemoStatus";

        [Header("Composition")]
        [Tooltip("The scene's CoreAI scope; found automatically when left empty.")]
        [SerializeField] private CoreAILifetimeScope _coreAiScope;

        [Tooltip("The world host whose tree the tour builds into.")]
        [SerializeField] private RbxWorldHost _worldHost;

        [Header("Content")]
        [Tooltip("The Lua tour. Ships as a .txt so Unity imports it as a TextAsset.")]
        [SerializeField] private TextAsset _tourMod;

        [Header("UI")]
        [SerializeField] private TextMeshProUGUI _statusLabel;
        [SerializeField] private TextMeshProUGUI _hintLabel;
        [SerializeField] private Button _runButton;
        [SerializeField] private Button _dropButton;
        [SerializeField] private Button _lowGravityButton;

        private ILuaModRuntime _mods;
        private ActorContext _actor;
        private readonly List<string> _log = new();
        private RbxInstance _workspace;
        private bool _ready;

        private void Start()
        {
            _coreAiScope = _coreAiScope != null
                ? _coreAiScope
                : FindFirstObjectByType<CoreAILifetimeScope>();
            _worldHost = _worldHost != null ? _worldHost : FindFirstObjectByType<RbxWorldHost>();

            if (_coreAiScope == null || _coreAiScope.Container == null || _worldHost == null)
            {
                Report("Scene is missing CoreAILifetimeScope or RbxWorldHost.");
                SetInteractable(false);
                return;
            }

            IObjectResolver mods = CoreAiDemoScope.ResolveModsContainer(_coreAiScope);
            _actor = mods.Resolve<IActorIdentityProvider>()
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            _mods = mods.Resolve<ILuaModRuntime>();
            _workspace = _worldHost.Game?.FindFirstChildOfClass("Workspace");
            _ready = _tourMod != null && _workspace != null;

            if (_runButton != null)
            {
                _runButton.onClick.AddListener(RunTour);
            }

            if (_dropButton != null)
            {
                _dropButton.onClick.AddListener(DropABlock);
            }

            if (_lowGravityButton != null)
            {
                _lowGravityButton.onClick.AddListener(ToggleGravity);
            }

            if (_hintLabel != null)
            {
                _hintLabel.text =
                    "Run the tour, then drop a block onto the red brick.\n"
                    + "Everything you see is one Lua script using Roblox APIs.";
            }

            Report(_ready
                ? "Ready. Press Run tour."
                : "Assign the tour mod on this component.");
        }

        private void Update()
        {
            // The Lua side reports through a workspace attribute, which is how a Roblox script would
            // hand a value to anything outside itself.
            if (_workspace == null)
            {
                return;
            }

            object status = _workspace.GetAttribute(StatusAttribute);
            if (status is string line && line.Length > 0 && (_log.Count == 0 || _log[^1] != line))
            {
                Report(line);
            }
        }

        private void RunTour()
        {
            if (!_ready)
            {
                return;
            }

            try
            {
                if (_mods.IsLoaded(_actor, ModId))
                {
                    _mods.UnloadMod(_actor, ModId);
                }

                _mods.LoadMod(_actor, ModId, _tourMod.text,
                    LuaCapabilities.Read | LuaCapabilities.WorldEdit);
                Report("Tour loaded: door, kill brick, coin, raycast.");
            }
            catch (Exception exception)
            {
                Report("The tour failed: " + exception.Message);
            }
        }

        private void DropABlock()
        {
            // WHY the block is dropped from C# rather than Lua: it is the physical event the tour is
            // waiting for, and dropping it by hand makes the Touched path something a visitor
            // triggers rather than something that already happened before they looked.
            if (_worldHost?.Registry == null)
            {
                return;
            }

            RbxInstance block = _worldHost.Registry.Create("Part");
            block.Name = "Dropped";
            block.Parent = _workspace;
            _worldHost.Binder.SetPosition(block.Id, new CoreAI.Mods.Rbx.Datatypes.RbxVector3(
                0f, 14f, 0f));
            _worldHost.Binder.SetAnchored(block.Id, false);
            Report("Dropped a block; watch the kill brick.");
        }

        private void ToggleGravity()
        {
            if (_coreAiScope == null)
            {
                return;
            }

            RbxWorldPhysics physics = CoreAiDemoScope.ResolveModsContainer(_coreAiScope)
                .Resolve<LuaCsModStack>()?.GameplayBindings?.RbxApi?.WorldPhysics;
            if (physics == null)
            {
                return;
            }

            bool low = Math.Abs(physics.Gravity - RbxWorldPhysics.DefaultGravity) < 0.01d;
            physics.Gravity = low ? RbxWorldPhysics.DefaultGravity / 5d
                : RbxWorldPhysics.DefaultGravity;
            Report("Gravity is now " + physics.Gravity.ToString("0.0") + " studs/s²"
                   + " — the host scene's own physics is untouched.");
        }

        private void SetInteractable(bool interactable)
        {
            if (_runButton != null)
            {
                _runButton.interactable = interactable;
            }

            if (_dropButton != null)
            {
                _dropButton.interactable = interactable;
            }

            if (_lowGravityButton != null)
            {
                _lowGravityButton.interactable = interactable;
            }
        }

        private void Report(string line)
        {
            _log.Add(line);
            if (_log.Count > 8)
            {
                _log.RemoveAt(0);
            }

            if (_statusLabel != null)
            {
                _statusLabel.text = string.Join("\n", _log);
            }
        }
    }
}
