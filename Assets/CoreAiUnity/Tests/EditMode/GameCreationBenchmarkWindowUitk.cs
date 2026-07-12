using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CoreAI.Benchmarking;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using RunEntry = CoreAI.Tests.EditMode.GameCreationBenchmarkLauncher.RunEntry;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// UI Toolkit control panel for the game-creation benchmark.
    /// </summary>
    public sealed class GameCreationBenchmarkWindowUitk : EditorWindow
    {
        private const string NonePinned = "(none = ranked descending)";

        private static readonly string[] ConnModeOptions = { "Default (config)", "On", "Off" };

        private static readonly string[] DimOrder =
        {
            "ToolCorrectness", "IntentSequence", "TaskCompletion", "Determinism", "Reasoning",
            "InstructionAdherence"
        };

        private readonly HashSet<string> _expandedRuns = new();
        private readonly HashSet<string> _collapsedModels = new();
        private readonly HashSet<string> _selectedModels = new();
        private readonly Dictionary<string, Texture2D> _thumbs = new();

        private VisualElement _runPanel;
        private VisualElement _historyPanel;
        private VisualElement _comparePanel;
        private VisualElement _modelsPanel;
        private ToolbarToggle _runTab;
        private ToolbarToggle _historyTab;
        private ToolbarToggle _compareTab;
        private ToolbarToggle _modelsTab;
        private Label _statusLabel;
        private Button _stopButton;
        private VisualElement _progressFill;
        private Label _progressText;
        private VisualElement _progressLines;
        private Label _groupWarning;
        private ScrollView _historyList;
        private ScrollView _compareList;
        private VisualElement _modelsRows;
        private DropdownField _modelSortDropdown;
        private DropdownField _pinnedDropdown;
        private Label _comparisonStatus;
        private VisualElement _comparisonPreview;
        private string _modelSort = "Suite score";
        private bool _compareSelectionInitialized;

        private bool _overrideConnection;
        private string _model = "";
        private string _baseUrl = "";
        private string _apiKey = "";
        private int _streamingMode;
        private int _nativeToolsMode;
        private bool _runG1 = true;
        private bool _runG2 = true;
        private bool _runG3 = true;
        private bool _runG4 = true;
        private bool _runG5 = true;
        private bool _runG6 = false; // castle bonus is off by default
        private bool _runG7 = false; // comprehensive integration is off by default (heavy, one-off)
        private bool _runG8 = true; // on by default: full runs always included G8 before it had a toggle
        private int _reps = 1;
        private int _retries = 1;
        private int _timeoutSeconds;

        [MenuItem("CoreAI/Benchmarks/Benchmark Window…", priority = 101)]
        public static void OpenWindow()
        {
            GameCreationBenchmarkWindowUitk window =
                GetWindow<GameCreationBenchmarkWindowUitk>(false, "CoreAI Benchmark");
            window.minSize = new Vector2(620, 640);
            window.Show();
        }

        private void OnEnable()
        {
            LoadPrefs();
        }

        public void CreateGUI()
        {
            LoadPrefs();
            rootVisualElement.Clear();
            rootVisualElement.style.flexDirection = FlexDirection.Column;
            rootVisualElement.style.backgroundColor = new Color(0.14f, 0.14f, 0.16f);

            Toolbar tabs = new();
            _runTab = MakeTab("RUN", 0);
            _historyTab = MakeTab("HISTORY", 1);
            _compareTab = MakeTab("COMPARE", 2);
            _modelsTab = MakeTab("MODELS", 3);
            tabs.Add(_runTab);
            tabs.Add(_historyTab);
            tabs.Add(_compareTab);
            tabs.Add(_modelsTab);

            ToolbarButton refresh = new(() =>
            {
                RefreshHistory();
                RefreshCompare();
                RefreshModels();
            })
            {
                text = "Refresh"
            };
            ToolbarButton openFolder = new(() =>
                EditorUtility.RevealInFinder(GameCreationBenchmarkLauncher.ResultsRoot()))
            {
                text = "Open folder",
                tooltip = "Open the TestResults/CoreAI/Benchmarks folder"
            };
            ToolbarButton openReport = new(() =>
            {
                string latest = GameCreationBenchmarkLauncher.FindLatestResult();
                if (string.IsNullOrEmpty(latest))
                {
                    EditorUtility.DisplayDialog("CoreAI Benchmark", "No report yet. Run the benchmark first.", "OK");
                    return;
                }

                GameCreationBenchmarkLauncher.OpenReport(latest);
            })
            {
                text = "Open report",
                tooltip = "Open the most recent benchmark report (.md)"
            };

            tabs.Add(new ToolbarSpacer { flex = true });
            tabs.Add(openFolder);
            tabs.Add(openReport);
            tabs.Add(refresh);
            rootVisualElement.Add(tabs);

            VisualElement content = new();
            content.style.flexGrow = 1;
            rootVisualElement.Add(content);

            _runPanel = BuildRunTab();
            _historyPanel = BuildHistoryTab();
            _comparePanel = BuildCompareTab();
            _modelsPanel = BuildModelsTab();
            content.Add(_runPanel);
            content.Add(_historyPanel);
            content.Add(_comparePanel);
            content.Add(_modelsPanel);

            SelectTab(0);
            rootVisualElement.schedule.Execute(UpdateLiveStatus).Every(250);
        }

        private ToolbarToggle MakeTab(string label, int index)
        {
            ToolbarToggle toggle = new() { text = label };
            toggle.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue)
                {
                    SelectTab(index);
                }
                else if (IsTabSelected(index))
                {
                    toggle.SetValueWithoutNotify(true);
                }
            });
            return toggle;
        }

        private bool IsTabSelected(int index)
        {
            return (index == 0 && _runPanel?.resolvedStyle.display != DisplayStyle.None)
                   || (index == 1 && _historyPanel?.resolvedStyle.display != DisplayStyle.None)
                   || (index == 2 && _comparePanel?.resolvedStyle.display != DisplayStyle.None)
                   || (index == 3 && _modelsPanel?.resolvedStyle.display != DisplayStyle.None);
        }

        private void SelectTab(int index)
        {
            _runTab?.SetValueWithoutNotify(index == 0);
            _historyTab?.SetValueWithoutNotify(index == 1);
            _compareTab?.SetValueWithoutNotify(index == 2);
            _modelsTab?.SetValueWithoutNotify(index == 3);

            SetVisible(_runPanel, index == 0);
            SetVisible(_historyPanel, index == 1);
            SetVisible(_comparePanel, index == 2);
            SetVisible(_modelsPanel, index == 3);

            if (index == 1)
            {
                RefreshHistory();
            }
            else if (index == 2)
            {
                RefreshCompare();
            }
            else if (index == 3)
            {
                RefreshModels();
            }
        }

        private VisualElement BuildRunTab()
        {
            ScrollView scroll = new();
            scroll.style.flexGrow = 1;
            scroll.Add(Header($"Game-Creation Benchmark {BenchmarkInfo.Version}",
                "The model builds a game with execute_lua + world_command; scored 0..100."));
            scroll.Add(BuildGroupsSection());
            scroll.Add(BuildRunOptionsSection());
            scroll.Add(BuildConnectionSection());
            scroll.Add(BuildRunButtonSection());
            scroll.Add(BuildStatusSection());
            return scroll;
        }

        private VisualElement BuildGroupsSection()
        {
            VisualElement section = Section("Scenarios (easy -> hard)");

            // Column header so the right-hand value is obviously the difficulty rating.
            VisualElement head = Row();
            Label hl = Muted("Scenario");
            hl.style.flexGrow = 1;
            head.Add(hl);
            Label hd = Muted("Difficulty 1–10");
            hd.style.width = 96;
            hd.style.unityTextAlign = TextAnchor.MiddleRight;
            head.Add(hd);
            section.Add(head);

            // Difficulty values come from the single source BenchmarkInfo.GroupDifficulty10, so the RUN-tab
            // rating can never disagree with the scenario/history rating.
            section.Add(GroupToggle(GameCreationBenchmarkLauncher.PrefG2,
                "G2 - runtime mechanic authoring (pure Lua)", BenchmarkInfo.DifficultyFor("G2"), _runG2,
                v => _runG2 = v));
            section.Add(GroupToggle(GameCreationBenchmarkLauncher.PrefG1,
                "G1 - build a game (world + Lua)", BenchmarkInfo.DifficultyFor("G1"), _runG1, v => _runG1 = v));
            section.Add(GroupToggle(GameCreationBenchmarkLauncher.PrefG5,
                "G5 - strict instruction-following (subtractive)", BenchmarkInfo.DifficultyFor("G5"), _runG5,
                v => _runG5 = v));
            section.Add(GroupToggle(GameCreationBenchmarkLauncher.PrefG3,
                "G3 - reasoning & design (harder, intelligence)", BenchmarkInfo.DifficultyFor("G3"), _runG3,
                v => _runG3 = v));
            section.Add(GroupToggle(GameCreationBenchmarkLauncher.PrefG4,
                "G4 - playable game (simulated playthrough)", BenchmarkInfo.DifficultyFor("G4"), _runG4,
                v => _runG4 = v));
            section.Add(GroupToggle(GameCreationBenchmarkLauncher.PrefG6,
                "G6 - free-build visual (bonus; default: castle)", BenchmarkInfo.DifficultyFor("G6"), _runG6, v =>
                {
                    _runG6 = v;
                    UpdateFreeBuildBoxVisibility();
                }));
            section.Add(BuildFreeBuildBox());
            section.Add(GroupToggle(GameCreationBenchmarkLauncher.PrefG7,
                "G7 - comprehensive integration (world + Lua consistency; one-off)",
                BenchmarkInfo.DifficultyFor("G7"), _runG7, v => _runG7 = v));
            section.Add(GroupToggle(GameCreationBenchmarkLauncher.PrefG8,
                "G8 - observe described state, then act (director-AI)",
                BenchmarkInfo.DifficultyFor("G8"), _runG8, v => _runG8 = v));
            _groupWarning = Muted("Select at least one group.");
            _groupWarning.style.color = new Color(0.95f, 0.68f, 0.32f);
            section.Add(_groupWarning);
            UpdateGroupWarning();
            return section;
        }

        private VisualElement GroupToggle(string prefKey, string label, int difficulty10, bool value,
            Action<bool> assign)
        {
            VisualElement row = Row();
            Toggle toggle = new(label) { value = value };
            toggle.style.flexGrow = 1;
            toggle.style.flexShrink = 1;
            toggle.RegisterValueChangedCallback(evt =>
            {
                assign(evt.newValue);
                EditorPrefs.SetBool(prefKey, evt.newValue);
                UpdateGroupWarning();
            });
            row.Add(toggle);

            int d = Mathf.Clamp(difficulty10, 1, 10);
            int half = Mathf.RoundToInt(d / 2f); // 5 dots scaled from the 1..10 rating
            Label dots = new($"{new string('●', half)}{new string('○', 5 - half)}  {d}/10")
            {
                tooltip = $"Difficulty {d}/10"
            };
            dots.style.unityTextAlign = TextAnchor.MiddleRight;
            dots.style.width = 96;
            // Inverted vs. score colour: low difficulty = green (easy), high difficulty = red (hard).
            dots.style.color = ScoreColor(100 - (d - 1) * 11);
            row.Add(dots);
            return row;
        }

        private VisualElement _freeBuildBox;

        // G6-only contextual controls: hidden unless G6 is ticked; the subject field appears only when the
        // "custom subject" toggle is on. Default (toggle off) = the built-in castle. Convenient + uncluttered.
        private VisualElement BuildFreeBuildBox()
        {
            const string subjectKey = "CoreAI.Benchmark.FreeBuildSubject";
            const string overrideKey = "CoreAI.Benchmark.FreeBuildOverride";
            const string visionKey = "CoreAI.Benchmark.VisionMode";
            bool overrideOn = EditorPrefs.GetBool(overrideKey, false);
            string subject = EditorPrefs.GetString(subjectKey, "");
            string visionMode = EditorPrefs.GetString(visionKey, "off");

            // Keep the env vars the harness reads in sync with the persisted state on window open.
            Environment.SetEnvironmentVariable(
                "COREAI_BENCHMARK_FREEBUILD_SUBJECT", overrideOn ? subject : "");
            Environment.SetEnvironmentVariable("COREAI_BENCHMARK_VISION_MODE", visionMode);

            _freeBuildBox = new VisualElement();
            _freeBuildBox.style.marginLeft = 16;
            _freeBuildBox.style.marginBottom = 4;

            // Vision mode: off (text-only build) / image (model sees & refines its build) / both (run both,
            // so the image-feedback result can be compared with the text-only one). Image needs a
            // vision-capable model.
            DropdownField visionField = new(
                "Vision feedback",
                new List<string> { "off", "image", "both" },
                Mathf.Max(0, new List<string> { "off", "image", "both" }.IndexOf(visionMode)))
            {
                tooltip = "off = text-only build. image = the model gets a camera tool to see and refine its " +
                          "own scene (vision-capable models only). both = run text-only AND image-feedback for " +
                          "a side-by-side comparison."
            };
            visionField.RegisterValueChangedCallback(evt =>
            {
                string v = evt.newValue ?? "off";
                EditorPrefs.SetString(visionKey, v);
                Environment.SetEnvironmentVariable("COREAI_BENCHMARK_VISION_MODE", v);
            });

            TextField subjectField = new("Build subject")
            {
                value = subject,
                multiline = true,
                tooltip = "What G6 builds — a short subject ('a futuristic city', 'a knight') or a full " +
                          "multi-line prompt. Multi-line: Shift+Enter for a new line.\n" +
                          "For a fully custom prompt you can also use the COREAI_BENCHMARK_FREEBUILD_PROMPT env var."
            };
            subjectField.style.display = overrideOn ? DisplayStyle.Flex : DisplayStyle.None;
            // Comfortable multi-line editing: wrap long prompts, grow vertically, keep the label on top.
            subjectField.style.minHeight = 72;
            subjectField.style.whiteSpace = WhiteSpace.Normal;
            subjectField.labelElement.style.minWidth = 0;
            subjectField.labelElement.style.alignSelf = Align.FlexStart;
            VisualElement subjectInput = subjectField.Q(className: "unity-base-text-field__input");
            if (subjectInput != null)
            {
                subjectInput.style.whiteSpace = WhiteSpace.Normal;
                subjectInput.style.minHeight = 72;
                subjectInput.style.unityTextAlign = TextAnchor.UpperLeft;
            }

            subjectField.RegisterValueChangedCallback(evt =>
            {
                string v = (evt.newValue ?? "").Trim();
                EditorPrefs.SetString(subjectKey, v);
                if (EditorPrefs.GetBool(overrideKey, false))
                {
                    Environment.SetEnvironmentVariable("COREAI_BENCHMARK_FREEBUILD_SUBJECT", v);
                }
            });

            Toggle overrideToggle = new("Custom subject (override default castle)") { value = overrideOn };
            overrideToggle.RegisterValueChangedCallback(evt =>
            {
                EditorPrefs.SetBool(overrideKey, evt.newValue);
                subjectField.style.display = evt.newValue ? DisplayStyle.Flex : DisplayStyle.None;
                Environment.SetEnvironmentVariable(
                    "COREAI_BENCHMARK_FREEBUILD_SUBJECT",
                    evt.newValue ? EditorPrefs.GetString(subjectKey, "") : "");
            });

            _freeBuildBox.Add(overrideToggle);
            _freeBuildBox.Add(subjectField);
            _freeBuildBox.Add(visionField);
            _freeBuildBox.style.display = _runG6 ? DisplayStyle.Flex : DisplayStyle.None;
            return _freeBuildBox;
        }

        private void UpdateFreeBuildBoxVisibility()
        {
            if (_freeBuildBox != null)
            {
                _freeBuildBox.style.display = _runG6 ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        private VisualElement BuildRunOptionsSection()
        {
            VisualElement section = Section("Run options");
            // One control: a slider with an inline number field (1..5 runs per scenario, mean kept).
            SliderInt repsSlider = new(1, 5)
            {
                label = "Repetitions",
                value = _reps,
                showInputField = true,
                tooltip = "Runs per scenario; the report keeps the per-scenario mean."
            };
            repsSlider.RegisterValueChangedCallback(evt =>
            {
                _reps = Mathf.Clamp(evt.newValue, 1, 5);
                EditorPrefs.SetInt(GameCreationBenchmarkLauncher.PrefReps, _reps);
            });
            section.Add(repsSlider);

            section.Add(IntField("Transient retries", _retries, 0, 3, v =>
            {
                _retries = Mathf.Clamp(v, 0, 3);
                EditorPrefs.SetInt(GameCreationBenchmarkLauncher.PrefRetries, _retries);
            }));
            section.Add(IntField("Timeout override (s)", _timeoutSeconds, 0, 99999, v =>
            {
                _timeoutSeconds = Mathf.Max(0, v);
                EditorPrefs.SetInt(GameCreationBenchmarkLauncher.PrefTimeout, _timeoutSeconds);
            }));
            return section;
        }

        private VisualElement BuildConnectionSection()
        {
            Foldout foldout = new()
            {
                text = "Connection override",
                value = _overrideConnection
            };
            StyleSection(foldout);

            Toggle over = new("Override connection") { value = _overrideConnection };
            VisualElement fields = new();
            fields.style.marginTop = 6;
            over.RegisterValueChangedCallback(evt =>
            {
                _overrideConnection = evt.newValue;
                EditorPrefs.SetBool(GameCreationBenchmarkLauncher.PrefOverride, _overrideConnection);
                fields.SetEnabled(_overrideConnection);
            });
            foldout.Add(over);
            foldout.Add(Muted("Off uses CoreAI Settings. Empty override fields fall back to the configured value."));

            TextField model = new("Model") { value = _model };
            model.RegisterValueChangedCallback(evt =>
            {
                _model = evt.newValue ?? "";
                EditorPrefs.SetString(GameCreationBenchmarkLauncher.PrefModel, _model);
            });
            fields.Add(model);

            TextField baseUrl = new("Base URL") { value = _baseUrl };
            baseUrl.RegisterValueChangedCallback(evt =>
            {
                _baseUrl = evt.newValue ?? "";
                EditorPrefs.SetString(GameCreationBenchmarkLauncher.PrefBaseUrl, _baseUrl);
            });
            fields.Add(baseUrl);

            TextField key = new("API key") { value = _apiKey, isPasswordField = true, maskChar = '*' };
            key.RegisterValueChangedCallback(evt => _apiKey = evt.newValue ?? "");
            fields.Add(key);

            fields.Add(ModeDropdown("Streaming", _streamingMode, v =>
            {
                _streamingMode = v;
                EditorPrefs.SetInt(GameCreationBenchmarkLauncher.PrefStreaming, v);
            }));
            fields.Add(ModeDropdown("Native tool-calling", _nativeToolsMode, v =>
            {
                _nativeToolsMode = v;
                EditorPrefs.SetInt(GameCreationBenchmarkLauncher.PrefNativeTools, v);
            }));
            fields.SetEnabled(_overrideConnection);
            foldout.Add(fields);

            Label configured = Muted(
                $"Configured: {Fallback(GameCreationBenchmarkLauncher.ConfiguredModel)}  {Fallback(GameCreationBenchmarkLauncher.ConfiguredBaseUrl)}");
            foldout.Add(configured);
            return foldout;
        }

        private VisualElement BuildRunButtonSection()
        {
            VisualElement section = Section(null);
            VisualElement row = Row();
            Button run = new(RunBenchmark) { text = "Run benchmark" };
            run.style.height = 34;
            run.style.unityFontStyleAndWeight = FontStyle.Bold;
            run.style.flexGrow = 1;
            row.Add(run);
            _stopButton = new Button(GameCreationBenchmarkLauncher.StopRunningBenchmark)
            {
                text = "Stop (save partial)"
            };
            _stopButton.style.height = 34;
            _stopButton.style.marginLeft = 6;
            _stopButton.style.display = DisplayStyle.None;
            row.Add(_stopButton);
            section.Add(row);
            return section;
        }

        private VisualElement BuildStatusSection()
        {
            VisualElement section = Section("Live status");
            _statusLabel = Muted(GameCreationBenchmarkLauncher.LastStatus);
            section.Add(_statusLabel);
            VisualElement bar = new();
            bar.style.height = 18;
            bar.style.backgroundColor = new Color(0f, 0f, 0f, 0.25f);
            bar.style.marginTop = 6;
            _progressFill = new VisualElement();
            _progressFill.style.height = Length.Percent(100);
            _progressFill.style.width = Length.Percent(0);
            _progressFill.style.backgroundColor = new Color(0.30f, 0.72f, 0.40f);
            bar.Add(_progressFill);
            _progressText = Muted("");
            _progressText.style.marginTop = 3;
            _progressLines = new VisualElement();
            _progressLines.style.marginTop = 5;
            section.Add(bar);
            section.Add(_progressText);
            section.Add(_progressLines);
            return section;
        }

        private VisualElement BuildHistoryTab()
        {
            _historyList = new ScrollView();
            _historyList.style.flexGrow = 1;
            RefreshHistory();
            return _historyList;
        }

        private VisualElement BuildCompareTab()
        {
            ScrollView root = new();
            root.style.flexGrow = 1;
            root.Add(
                Header("Model comparison", "Select newest model reports and build COMPARISON.md + COMPARISON.svg."));
            Label help = Muted("Tick models, optionally pick a Pinned first model, then Build.");
            help.style.marginLeft = 12;
            help.style.marginRight = 12;
            root.Add(help);
            VisualElement section = Section("Models");
            VisualElement toolbar = Row();
            Button selectAll = new(() =>
            {
                foreach (RunEntry run in LatestRunsByModel())
                {
                    _selectedModels.Add(run.Summary.ModelId);
                }

                RefreshCompare();
            })
            {
                text = "Select all"
            };
            Button selectNone = new(() =>
            {
                _selectedModels.Clear();
                RefreshCompare();
            })
            {
                text = "Select none"
            };
            toolbar.Add(selectAll);
            toolbar.Add(selectNone);
            section.Add(toolbar);
            _compareList = new ScrollView();
            _compareList.style.maxHeight = 260;
            _compareList.style.marginTop = 6;
            section.Add(_compareList);
            _pinnedDropdown = new DropdownField("Pinned first", new List<string> { NonePinned }, 0);
            section.Add(_pinnedDropdown);
            Button build = new(BuildSelectedComparison) { text = "Build comparison report" };
            build.style.height = 32;
            build.style.marginTop = 8;
            section.Add(build);
            _comparisonStatus = Muted("");
            section.Add(_comparisonStatus);
            root.Add(section);

            _comparisonPreview = Section("COMPARISON.svg");
            _comparisonPreview.style.marginTop = 14;
            _comparisonPreview.style.backgroundColor = new Color(0.15f, 0.16f, 0.19f);
            root.Add(_comparisonPreview);
            RefreshCompare();
            RefreshComparisonPreview();
            return root;
        }

        private VisualElement BuildModelsTab()
        {
            ScrollView root = new();
            root.style.flexGrow = 1;
            root.Add(Header("Models leaderboard",
                "Newest report per model, ranked by the selected metric."));

            VisualElement controls = Section("Sort");
            _modelSortDropdown = new DropdownField(
                "Metric",
                new List<string> { "Suite score", "Speed", "Pass-rate", "Game-fit" },
                0);
            _modelSortDropdown.RegisterValueChangedCallback(evt =>
            {
                _modelSort = evt.newValue;
                RefreshModels();
            });
            controls.Add(_modelSortDropdown);
            root.Add(controls);

            VisualElement chart = Section("All models");
            VisualElement header = Row();
            header.style.marginTop = 6;
            header.Add(FixedLabel("#", 30, true));
            header.Add(FixedLabel("Model", 230, true));
            header.Add(FixedLabel("Value", 90, true));
            header.Add(FixedLabel("Stats", 260, true));
            chart.Add(header);

            _modelsRows = new VisualElement();
            _modelsRows.style.marginTop = 4;
            chart.Add(_modelsRows);
            root.Add(chart);

            RefreshModels();
            return root;
        }

        private void RunBenchmark()
        {
            if (!_runG1 && !_runG2 && !_runG3 && !_runG4 && !_runG5 && !_runG6 && !_runG7 && !_runG8)
            {
                EditorUtility.DisplayDialog("CoreAI Benchmark", "Select at least one benchmark group.", "OK");
                return;
            }

            Persist();
            GameCreationBenchmarkLauncher.ApplySavedConfig();
            if (_overrideConnection)
            {
                GameCreationBenchmarkLauncher.Configure(
                    GameCreationBenchmarkLauncher.OrConfigured(_model,
                        GameCreationBenchmarkLauncher.ConfiguredModel),
                    GameCreationBenchmarkLauncher.OrConfigured(_baseUrl,
                        GameCreationBenchmarkLauncher.ConfiguredBaseUrl),
                    GameCreationBenchmarkLauncher.OrConfigured(_apiKey,
                        GameCreationBenchmarkLauncher.ConfiguredApiKey),
                    GameCreationBenchmarkLauncher.ConnectionMode(_streamingMode),
                    GameCreationBenchmarkLauncher.ConnectionMode(_nativeToolsMode),
                    BuildGroups(),
                    _reps,
                    _retries,
                    _timeoutSeconds > 0 ? _timeoutSeconds : (int?)null);
            }

            GameCreationBenchmarkLauncher.RunViaTestRunner(true, false);
        }

        private void UpdateLiveStatus()
        {
            if (_statusLabel == null)
            {
                return;
            }

            bool active = BenchmarkProgress.IsRunning;
            if (_stopButton != null)
            {
                _stopButton.style.display = active ? DisplayStyle.Flex : DisplayStyle.None;
                _stopButton.SetEnabled(active && !BenchmarkProgress.StopRequested);
                _stopButton.text = BenchmarkProgress.StopRequested ? "Stopping…" : "Stop (save partial)";
            }

            // A solo scenario (Total <= 1, e.g. running G6's free build alone) has a count-based Fraction
            // that sits at 0% for its whole multi-minute run and only jumps to 100% at the very end - not
            // useful to a human watching. Fall back to the scenario's own elapsed/remaining wall-clock
            // budget when it is the only thing running and a timeout was recorded for it.
            bool useScenarioClock = active && BenchmarkProgress.Total <= 1 && BenchmarkProgress.HasScenarioClock;

            _statusLabel.text = active
                ? useScenarioClock
                    ? $"{BenchmarkProgress.ModelId}  {FormatDuration(BenchmarkProgress.ScenarioRemainingSeconds * 1000.0)} left"
                    : $"{BenchmarkProgress.ModelId}  {BenchmarkProgress.Completed}/{BenchmarkProgress.Total}"
                : GameCreationBenchmarkLauncher.LastStatus;

            float pct = active
                ? Mathf.Clamp01(useScenarioClock
                    ? BenchmarkProgress.ScenarioTimeFraction
                    : BenchmarkProgress.Fraction) * 100f
                : 0f;
            _progressFill.style.width = Length.Percent(pct);
            _progressText.text = active
                ? useScenarioClock
                    ? $"{BenchmarkProgress.CurrentLabel}  —  {FormatDuration(BenchmarkProgress.ScenarioElapsedSeconds * 1000.0)} elapsed, " +
                      $"{FormatDuration(BenchmarkProgress.ScenarioRemainingSeconds * 1000.0)} left"
                    : BenchmarkProgress.CurrentLabel
                : "";
            _progressLines.Clear();
            if (!active)
            {
                return;
            }

            foreach (ProgressLine line in BenchmarkProgress.RecentLines(10))
            {
                VisualElement row = Row();
                row.Add(Muted(line.Left));
                row.Add(new VisualElement { style = { flexGrow = 1 } });
                if (!string.IsNullOrEmpty(line.Right))
                {
                    row.Add(Muted(line.Right));
                }

                _progressLines.Add(row);
            }
        }

        private void RefreshHistory()
        {
            if (_historyList == null)
            {
                return;
            }

            _historyList.Clear();
            _historyList.Add(Header("History", "Past benchmark runs grouped by model."));
            List<RunEntry> runs = GameCreationBenchmarkLauncher.ListRuns();
            if (runs.Count == 0)
            {
                _historyList.Add(Info("No runs yet. Run a benchmark to see history here."));
                return;
            }

            List<string> modelIds = runs.Select(r => r.Summary.ModelId).Distinct().ToList();
            RunEntry best = runs.OrderByDescending(r => r.Summary.SuiteBase).First();
            _historyList.Add(Info(
                $"{runs.Count} run(s), {modelIds.Count} model(s). Best: {best.Summary.ModelId} ({Inv(best.Summary.SuiteBase)})"));

            VisualElement toolbar = Row();
            toolbar.style.justifyContent = Justify.FlexEnd;
            toolbar.style.marginTop = 2;
            toolbar.style.marginBottom = 6;

            ToolbarButton collapseAll = new(() =>
            {
                foreach (string id in modelIds)
                {
                    _collapsedModels.Add(id);
                }

                RefreshHistory();
            })
            {
                text = "Collapse all",
                tooltip = "Collapse every model's run list"
            };
            collapseAll.style.width = 90;
            collapseAll.style.fontSize = 11;
            toolbar.Add(collapseAll);

            ToolbarButton expandAll = new(() =>
            {
                _collapsedModels.Clear();
                RefreshHistory();
            })
            {
                text = "Expand all",
                tooltip = "Expand every model's run list"
            };
            expandAll.style.width = 90;
            expandAll.style.fontSize = 11;
            expandAll.style.marginLeft = 4;
            toolbar.Add(expandAll);

            ToolbarButton clearAll = new(() =>
            {
                if (EditorUtility.DisplayDialog("Clear benchmark history",
                        "Delete ALL benchmark runs? This removes every saved report and cannot be undone.",
                        "Delete all", "Cancel"))
                {
                    GameCreationBenchmarkLauncher.DeleteAllRuns();
                    RefreshHistory();
                    RefreshCompare();
                }
            })
            {
                text = "Clear all",
                tooltip = "Delete every saved benchmark report"
            };
            clearAll.style.width = 90;
            clearAll.style.fontSize = 11;
            clearAll.style.marginLeft = 4;
            toolbar.Add(clearAll);

            _historyList.Add(toolbar);

            foreach (IGrouping<string, RunEntry> group in runs.GroupBy(r => r.Summary.ModelId)
                         .OrderByDescending(g => g.Max(r => r.Summary.RunId)))
            {
                bool collapsed = _collapsedModels.Contains(group.Key);
                VisualElement modelSection = Section(null);

                VisualElement header = Row();
                header.style.marginTop = 0;
                header.RegisterCallback<MouseUpEvent>(evt =>
                {
                    if (evt.button == 0)
                    {
                        ToggleModelSection(group.Key);
                    }
                });
                Button toggle = new(() => ToggleModelSection(group.Key)) { text = collapsed ? ">" : "v" };
                toggle.style.width = 22;
                toggle.RegisterCallback<MouseUpEvent>(evt => evt.StopPropagation());
                header.Add(toggle);
                header.Add(LabelBold($"{group.Key} ({group.Count()})"));
                modelSection.Add(header);

                if (!collapsed)
                {
                    foreach (RunEntry entry in group.OrderByDescending(r => r.Summary.RunId))
                    {
                        modelSection.Add(RunRow(entry));
                    }
                }

                _historyList.Add(modelSection);
            }
        }

        private void ToggleModelSection(string modelId)
        {
            if (!_collapsedModels.Remove(modelId))
            {
                _collapsedModels.Add(modelId);
            }

            RefreshHistory();
        }

        private VisualElement RunRow(RunEntry entry)
        {
            ModelSummary s = entry.Summary;
            VisualElement wrap = new();
            wrap.style.marginBottom = 6;
            wrap.style.paddingBottom = 6;
            wrap.style.borderBottomWidth = 1;
            wrap.style.borderBottomColor = new Color(1f, 1f, 1f, 0.08f);

            VisualElement row = Row();
            row.style.minHeight = 30;
            row.RegisterCallback<MouseUpEvent>(evt =>
            {
                if (evt.button == 0)
                {
                    ToggleRunDetails(entry.JsonPath);
                }
            });

            Button expand = new(() => ToggleRunDetails(entry.JsonPath))
            {
                text = _expandedRuns.Contains(entry.JsonPath) ? "v" : ">"
            };
            expand.style.width = 28;
            expand.RegisterCallback<MouseUpEvent>(evt => evt.StopPropagation());
            row.Add(expand);
            // The model name is the group header above, so the row stays compact: date, score, then ONE
            // flexible stats label that grows to fill and truncates — never overlapping the columns.
            row.Add(FixedLabel(FormatRunDate(s.RunId), 104));
            VisualElement bar = ScoreBar(s.SuiteBase, 60);
            bar.style.flexShrink = 0;
            row.Add(bar);
            string speed = s.TotalCompletionTokens > 0 ? $"{Inv(s.TokensPerSecond)} tok/s" : "tok/s n/a";
            Label stats = Ellipsis(Muted(
                $"reps {s.Repetitions}  ·  P{s.Pass}/PA{s.Partial}/F{s.Fail}  ·  {FormatTokens(s.TotalTokens)}  ·  {speed}"));
            stats.style.flexGrow = 1;
            stats.style.marginLeft = 6;
            row.Add(stats);

            Button open = new(() => GameCreationBenchmarkLauncher.OpenReport(entry.MdPath)) { text = "Open" };
            open.RegisterCallback<MouseUpEvent>(evt => evt.StopPropagation());
            row.Add(open);
            Button delete = new(() =>
            {
                if (EditorUtility.DisplayDialog("Delete run",
                        $"Delete this benchmark report?\n\n{s.ModelId} - {FormatRunDate(s.RunId)} ({Inv(s.SuiteBase)}/100)",
                        "Delete", "Cancel"))
                {
                    GameCreationBenchmarkLauncher.DeleteRun(entry);
                    RefreshHistory();
                    RefreshCompare();
                }
            })
            {
                text = "Delete"
            };
            delete.RegisterCallback<MouseUpEvent>(evt => evt.StopPropagation());
            row.Add(delete);

            wrap.Add(row);
            if (_expandedRuns.Contains(entry.JsonPath))
            {
                wrap.Add(RunDetails(entry));
            }

            return wrap;
        }

        private void ToggleRunDetails(string jsonPath)
        {
            if (_expandedRuns.Contains(jsonPath))
            {
                _expandedRuns.Remove(jsonPath);
            }
            else
            {
                _expandedRuns.Add(jsonPath);
            }

            RefreshHistory();
        }

        private VisualElement RunDetails(RunEntry entry)
        {
            ModelSummary s = entry.Summary;
            VisualElement box = new();
            box.style.marginLeft = 32;
            box.style.marginTop = 4;

            if (s.GameFitOverall > 0 || s.Roles.Count > 0)
            {
                box.Add(Ellipsis(LabelBold($"Game-fitness {Inv(s.GameFitOverall)}/10 - best: {Fallback(s.BestRole)}")));
                foreach (KeyValuePair<string, double> role in s.Roles.OrderByDescending(r => r.Value))
                {
                    box.Add(MetricBar(role.Key, role.Value, 10,
                        $"{Inv(role.Value)}/10  {RoleVerdictShort(role.Value)}"));
                }
            }

            box.Add(LabelBold("Dimensions"));
            foreach (string dim in DimOrder)
            {
                if (s.Dimensions.TryGetValue(dim, out double value))
                {
                    box.Add(MetricBar(DimShort(dim), value, 100, Inv(value)));
                }
            }

            box.Add(Ellipsis(Muted(
                $"Bonus +{Inv(s.MeanEfficiencyBonus)} (tokens +{Inv(s.MeanTokenBonus)}, time +{Inv(s.MeanTimeBonus)})  " +
                $"tool-errors {Inv(s.ToolErrorRate * 100)}%  pass-rate {Inv(s.PassRate * 100)}%")));
            box.Add(Ellipsis(Muted(
                $"{s.TotalTokens} tokens ({(s.TotalCompletionTokens > 0 ? s.TotalCompletionTokens + " gen" : "gen n/a")})  " +
                $"{FormatDuration(s.TotalLatencyMs)}  " +
                $"{(s.TotalCompletionTokens > 0 ? $"{Inv(s.TokensPerSecond)} tok/s generation" : "tok/s n/a (older report)")}")));
            box.Add(SceneThumbnails(entry));
            return box;
        }

        private VisualElement SceneThumbnails(RunEntry entry)
        {
            VisualElement root = new();
            string dir = Path.GetDirectoryName(entry.MdPath);
            string stem = Path.GetFileNameWithoutExtension(entry.MdPath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(stem) || !Directory.Exists(dir))
            {
                return root;
            }

            string[] pngs;
            try
            {
                pngs = Directory.GetFiles(dir, stem + "_*.png");
            }
            catch
            {
                return root;
            }

            if (pngs.Length == 0)
            {
                return root;
            }

            root.Add(LabelBold($"Scene screenshots ({pngs.Length})"));
            VisualElement grid = new();
            grid.style.flexDirection = FlexDirection.Row;
            grid.style.flexWrap = Wrap.Wrap;
            grid.style.marginTop = 4;
            foreach (string png in pngs)
            {
                Texture2D tex = LoadThumb(png);
                VisualElement tile = new();
                tile.style.width = 150;
                tile.style.marginRight = 8;
                tile.style.marginBottom = 8;
                Image image = new() { image = tex, scaleMode = ScaleMode.ScaleToFit };
                image.style.width = 150;
                image.style.height =
                    tex != null ? Mathf.Clamp(150f * tex.height / Mathf.Max(1, tex.width), 64f, 120f) : 84f;
                image.style.backgroundColor = new Color(0f, 0f, 0f, 0.2f);
                image.RegisterCallback<MouseUpEvent>(_ => OpenImage(png));
                tile.Add(image);
                tile.Add(Ellipsis(Muted(ScenarioFromPng(png, stem))));
                grid.Add(tile);
            }

            root.Add(grid);
            return root;
        }

        private void RefreshCompare()
        {
            if (_compareList == null)
            {
                return;
            }

            List<RunEntry> latest = LatestRunsByModel();
            if (!_compareSelectionInitialized)
            {
                foreach (RunEntry run in latest)
                {
                    _selectedModels.Add(run.Summary.ModelId);
                }

                _compareSelectionInitialized = true;
            }

            _compareList.Clear();
            foreach (RunEntry run in latest)
            {
                string model = run.Summary.ModelId;
                VisualElement row = Row();
                row.style.paddingTop = 2;
                row.style.paddingBottom = 2;

                Toggle toggle = new()
                {
                    value = _selectedModels.Contains(model)
                };
                toggle.style.flexShrink = 0;
                toggle.RegisterValueChangedCallback(evt =>
                {
                    if (evt.newValue)
                    {
                        _selectedModels.Add(model);
                    }
                    else
                    {
                        _selectedModels.Remove(model);
                    }

                    RefreshPinnedDropdown(latest);
                });

                Label name = Ellipsis(new Label(model));
                name.tooltip = model;
                name.style.flexGrow = 1;
                name.style.marginLeft = 4;
                name.style.color = new Color(0.86f, 0.87f, 0.90f);

                Label meta =
                    Ellipsis(Muted(
                        $"{Inv(run.Summary.SuiteBase)} · reps {run.Summary.Repetitions} · {FormatRunDate(run.Summary.RunId)}"));
                meta.style.flexShrink = 0;
                meta.style.width = 150;
                meta.style.unityTextAlign = TextAnchor.MiddleRight;
                meta.style.color = new Color(0.58f, 0.60f, 0.64f);

                row.Add(toggle);
                row.Add(name);
                row.Add(meta);
                _compareList.Add(row);
            }

            if (latest.Count == 0)
            {
                _compareList.Add(Info("No parseable benchmark JSON reports found."));
            }

            RefreshPinnedDropdown(latest);
        }

        private void RefreshPinnedDropdown(List<RunEntry> latest)
        {
            if (_pinnedDropdown == null)
            {
                return;
            }

            string previous = _pinnedDropdown.value;
            List<string> choices = new() { NonePinned };
            choices.AddRange(latest.Select(r => r.Summary.ModelId).Where(m => _selectedModels.Contains(m)));
            _pinnedDropdown.choices = choices;
            _pinnedDropdown.SetValueWithoutNotify(choices.Contains(previous) ? previous : NonePinned);
        }

        private void BuildSelectedComparison()
        {
            List<ModelSummary> selected = LatestRunsByModel()
                .Where(r => _selectedModels.Contains(r.Summary.ModelId))
                .Select(r => r.Summary)
                .ToList();
            if (selected.Count == 0)
            {
                _comparisonStatus.text = "Select at least one model.";
                return;
            }

            string pinned = _pinnedDropdown.value == NonePinned ? null : _pinnedDropdown.value;
            string md = GameCreationBenchmarkLauncher.WriteComparison(
                GameCreationBenchmarkLauncher.ResultsRoot(), selected, pinned);
            _comparisonStatus.text = $"Wrote {md}";
            RefreshComparisonPreview();
        }

        private void RefreshComparisonPreview()
        {
            if (_comparisonPreview == null)
            {
                return;
            }

            _comparisonPreview.Clear();
            _comparisonPreview.Add(LabelBold("COMPARISON.svg"));
            string svg = Path.Combine(GameCreationBenchmarkLauncher.ResultsRoot(), "COMPARISON.svg");
            if (!File.Exists(svg))
            {
                _comparisonPreview.Add(Muted("No COMPARISON.svg yet."));
                return;
            }

            _comparisonPreview.Add(Muted(svg));
            _comparisonPreview.Add(new Button(() => EditorUtility.RevealInFinder(svg)) { text = "Reveal SVG" });
        }

        private void RefreshModels()
        {
            if (_modelsRows == null)
            {
                return;
            }

            List<RunEntry> latest = LatestRunsByModel();
            double maxSpeed = latest.Count == 0 ? 0 : latest.Max(r => r.Summary.TokensPerSecond);
            List<RunEntry> ranked = SortModelRuns(latest).ToList();

            _modelsRows.Clear();
            if (ranked.Count == 0)
            {
                _modelsRows.Add(Info("No parseable benchmark JSON reports found."));
                return;
            }

            for (int i = 0; i < ranked.Count; i++)
            {
                _modelsRows.Add(ModelLeaderboardRow(i + 1, ranked[i], maxSpeed));
            }
        }

        private IEnumerable<RunEntry> SortModelRuns(IEnumerable<RunEntry> runs)
        {
            IOrderedEnumerable<RunEntry> ordered = _modelSort switch
            {
                "Speed" => runs.OrderByDescending(r => r.Summary.TokensPerSecond),
                "Pass-rate" => runs.OrderByDescending(r => r.Summary.PassRate),
                "Game-fit" => runs.OrderByDescending(r => r.Summary.GameFitOverall),
                _ => runs.OrderByDescending(r => r.Summary.SuiteBase)
            };

            return ordered
                .ThenByDescending(r => r.Summary.SuiteBase)
                .ThenBy(r => r.Summary.ModelId);
        }

        private VisualElement ModelLeaderboardRow(int rank, RunEntry entry, double maxSpeed)
        {
            ModelSummary s = entry.Summary;
            (double percent, string valueLabel) = ModelMetricValue(s, maxSpeed);

            VisualElement row = Row();
            row.style.minHeight = 38;
            row.style.paddingTop = 5;
            row.style.paddingBottom = 5;
            row.style.borderBottomWidth = 1;
            row.style.borderBottomColor = new Color(1f, 1f, 1f, 0.06f);

            row.Add(FixedLabel(rank.ToString(), 30, true));
            Label model = FixedLabel(s.ModelId, 230, rank == 1);
            model.style.flexShrink = 1;
            row.Add(model);

            VisualElement bar = new();
            bar.style.width = 210;
            bar.style.height = 20;
            bar.style.marginRight = 8;
            bar.style.backgroundColor = new Color(0f, 0f, 0f, 0.28f);
            VisualElement fill = new();
            fill.style.height = Length.Percent(100);
            fill.style.width = Length.Percent(Mathf.Clamp((float)percent, 0f, 100f));
            fill.style.backgroundColor = ScoreColor(percent);
            bar.Add(fill);
            row.Add(bar);

            Label value = FixedLabel(valueLabel, 90, true);
            value.style.color = ScoreColor(percent);
            row.Add(value);

            string speed = s.TotalCompletionTokens > 0 ? $"{Inv(s.TokensPerSecond)} tok/s" : "tok/s n/a";
            Label stats = FixedLabel(
                $"score {Inv(s.SuiteBase)} | {speed} | pass {Inv(s.PassRate * 100)}% | fit {Inv(s.GameFitOverall)}/10 | reps {s.Repetitions}",
                420);
            stats.style.flexGrow = 1;
            stats.style.flexShrink = 1;
            row.Add(stats);
            return row;
        }

        private (double Percent, string Label) ModelMetricValue(ModelSummary s, double maxSpeed)
        {
            return _modelSort switch
            {
                "Speed" => (maxSpeed <= 0 ? 0 : s.TokensPerSecond / maxSpeed * 100.0,
                    $"{Inv(s.TokensPerSecond)} tok/s"),
                "Pass-rate" => (s.PassRate * 100.0, $"{Inv(s.PassRate * 100)}%"),
                "Game-fit" => (s.GameFitOverall * 10.0, $"{Inv(s.GameFitOverall)}/10"),
                _ => (s.SuiteBase, Inv(s.SuiteBase))
            };
        }

        private List<RunEntry> LatestRunsByModel()
        {
            return GameCreationBenchmarkLauncher.ListRuns()
                .GroupBy(r => r.Summary.ModelId)
                .Select(g => g.OrderByDescending(r => r.Summary.RunId).First())
                .OrderByDescending(r => r.Summary.SuiteBase)
                .ThenBy(r => r.Summary.ModelId)
                .ToList();
        }

        private string BuildGroups()
        {
            return GameCreationBenchmarkLauncher.GroupsCsv(
                _runG1, _runG2, _runG3, _runG4, _runG5, _runG6, _runG7, _runG8);
        }

        private void LoadPrefs()
        {
            _overrideConnection = EditorPrefs.GetBool(GameCreationBenchmarkLauncher.PrefOverride, false);
            _model = EditorPrefs.GetString(GameCreationBenchmarkLauncher.PrefModel, "");
            _baseUrl = EditorPrefs.GetString(GameCreationBenchmarkLauncher.PrefBaseUrl, "");
            _streamingMode = EditorPrefs.GetInt(GameCreationBenchmarkLauncher.PrefStreaming, 0);
            _nativeToolsMode = EditorPrefs.GetInt(GameCreationBenchmarkLauncher.PrefNativeTools, 0);
            _runG2 = EditorPrefs.GetBool(GameCreationBenchmarkLauncher.PrefG2, true);
            _runG1 = EditorPrefs.GetBool(GameCreationBenchmarkLauncher.PrefG1, true);
            _runG3 = EditorPrefs.GetBool(GameCreationBenchmarkLauncher.PrefG3, true);
            _runG4 = EditorPrefs.GetBool(GameCreationBenchmarkLauncher.PrefG4, true);
            _runG5 = EditorPrefs.GetBool(GameCreationBenchmarkLauncher.PrefG5, true);
            _runG6 = EditorPrefs.GetBool(GameCreationBenchmarkLauncher.PrefG6, false);
            _runG7 = EditorPrefs.GetBool(GameCreationBenchmarkLauncher.PrefG7, false);
            _runG8 = EditorPrefs.GetBool(GameCreationBenchmarkLauncher.PrefG8, true);
            _reps = Mathf.Clamp(EditorPrefs.GetInt(GameCreationBenchmarkLauncher.PrefReps, 1), 1, 5);
            _retries = Mathf.Clamp(EditorPrefs.GetInt(GameCreationBenchmarkLauncher.PrefRetries, 1), 0, 3);
            _timeoutSeconds = Mathf.Max(0, EditorPrefs.GetInt(GameCreationBenchmarkLauncher.PrefTimeout, 0));
        }

        private void Persist()
        {
            EditorPrefs.SetBool(GameCreationBenchmarkLauncher.PrefOverride, _overrideConnection);
            EditorPrefs.SetString(GameCreationBenchmarkLauncher.PrefModel, _model);
            EditorPrefs.SetString(GameCreationBenchmarkLauncher.PrefBaseUrl, _baseUrl);
            EditorPrefs.SetInt(GameCreationBenchmarkLauncher.PrefStreaming, _streamingMode);
            EditorPrefs.SetInt(GameCreationBenchmarkLauncher.PrefNativeTools, _nativeToolsMode);
            EditorPrefs.SetBool(GameCreationBenchmarkLauncher.PrefG2, _runG2);
            EditorPrefs.SetBool(GameCreationBenchmarkLauncher.PrefG1, _runG1);
            EditorPrefs.SetBool(GameCreationBenchmarkLauncher.PrefG3, _runG3);
            EditorPrefs.SetBool(GameCreationBenchmarkLauncher.PrefG4, _runG4);
            EditorPrefs.SetBool(GameCreationBenchmarkLauncher.PrefG5, _runG5);
            EditorPrefs.SetBool(GameCreationBenchmarkLauncher.PrefG6, _runG6);
            EditorPrefs.SetBool(GameCreationBenchmarkLauncher.PrefG7, _runG7);
            EditorPrefs.SetBool(GameCreationBenchmarkLauncher.PrefG8, _runG8);
            EditorPrefs.SetInt(GameCreationBenchmarkLauncher.PrefReps, _reps);
            EditorPrefs.SetInt(GameCreationBenchmarkLauncher.PrefRetries, _retries);
            EditorPrefs.SetInt(GameCreationBenchmarkLauncher.PrefTimeout, _timeoutSeconds);
        }

        private void UpdateGroupWarning()
        {
            if (_groupWarning != null)
            {
                _groupWarning.style.display =
                    _runG1 || _runG2 || _runG3 || _runG4 || _runG5 || _runG6 || _runG7 || _runG8
                        ? DisplayStyle.None
                        : DisplayStyle.Flex;
            }
        }

        private static IntegerField IntField(string label, int value, int min, int max, Action<int> changed)
        {
            IntegerField field = new(label) { value = value };
            field.RegisterValueChangedCallback(evt =>
            {
                int next = Mathf.Clamp(evt.newValue, min, max);
                if (next != evt.newValue)
                {
                    field.SetValueWithoutNotify(next);
                }

                changed(next);
            });
            return field;
        }

        private static DropdownField ModeDropdown(string label, int value, Action<int> changed)
        {
            DropdownField field = new(label, ConnModeOptions.ToList(),
                Mathf.Clamp(value, 0, ConnModeOptions.Length - 1));
            field.RegisterValueChangedCallback(evt =>
            {
                int index = ConnModeOptions.ToList().IndexOf(evt.newValue);
                changed(index < 0 ? 0 : index);
            });
            return field;
        }

        private static VisualElement Header(string title, string subtitle)
        {
            VisualElement header = new();
            header.style.paddingLeft = 12;
            header.style.paddingRight = 12;
            header.style.paddingTop = 10;
            header.style.paddingBottom = 6;
            Label h = LabelBold(title);
            h.style.fontSize = 16;
            header.Add(h);
            header.Add(Muted(subtitle));
            return header;
        }

        private static VisualElement Section(string title)
        {
            VisualElement section = new();
            StyleSection(section);
            if (!string.IsNullOrEmpty(title))
            {
                section.Add(LabelBold(title));
            }

            return section;
        }

        private static void StyleSection(VisualElement element)
        {
            element.style.marginLeft = 10;
            element.style.marginRight = 10;
            element.style.marginTop = 8;
            element.style.paddingLeft = 10;
            element.style.paddingRight = 10;
            element.style.paddingTop = 8;
            element.style.paddingBottom = 8;
            element.style.backgroundColor = new Color(0.19f, 0.19f, 0.22f);
            element.style.borderTopWidth = 1;
            element.style.borderBottomWidth = 1;
            element.style.borderLeftWidth = 1;
            element.style.borderRightWidth = 1;
            element.style.borderTopColor = new Color(1f, 1f, 1f, 0.08f);
            element.style.borderBottomColor = new Color(0f, 0f, 0f, 0.35f);
            element.style.borderLeftColor = new Color(1f, 1f, 1f, 0.08f);
            element.style.borderRightColor = new Color(0f, 0f, 0f, 0.35f);
        }

        private static VisualElement Row()
        {
            VisualElement row = new();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginTop = 3;
            row.style.marginBottom = 3;
            return row;
        }

        private static Label LabelBold(string text)
        {
            Label label = new(text);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.color = new Color(0.88f, 0.89f, 0.91f);
            return label;
        }

        private static Label Muted(string text)
        {
            Label label = new(text);
            label.style.color = new Color(0.66f, 0.68f, 0.72f);
            label.style.whiteSpace = WhiteSpace.Normal;
            return label;
        }

        private static Label Ellipsis(Label l)
        {
            l.style.whiteSpace = WhiteSpace.NoWrap;
            l.style.overflow = Overflow.Hidden;
            l.style.textOverflow = TextOverflow.Ellipsis;
            l.style.flexShrink = 1;
            return l;
        }

        private static Label Info(string text)
        {
            Label label = Muted(text);
            label.style.marginLeft = 10;
            label.style.marginRight = 10;
            label.style.marginTop = 8;
            return label;
        }

        private static Label FixedLabel(string text, float width, bool bold = false)
        {
            Label label = Ellipsis(bold ? LabelBold(text) : Muted(text));
            label.style.width = width;
            label.style.flexShrink = 0;
            label.tooltip = text;
            return label;
        }

        private static VisualElement ScoreBar(double score, float width)
        {
            VisualElement box = new();
            box.style.width = width;
            box.style.height = 18;
            box.style.marginRight = 6;
            box.style.backgroundColor = new Color(0f, 0f, 0f, 0.25f);
            VisualElement fill = new();
            fill.style.height = Length.Percent(100);
            fill.style.width = Length.Percent(Mathf.Clamp((float)score, 0f, 100f));
            fill.style.backgroundColor = ScoreColor(score);
            box.Add(fill);
            Label text = new(Inv(score));
            text.style.position = Position.Absolute;
            text.style.left = 5;
            text.style.top = 1;
            text.style.unityFontStyleAndWeight = FontStyle.Bold;
            text.style.color = Color.white;
            box.Add(text);
            return box;
        }

        private static VisualElement MetricBar(string label, double value, double max, string valueLabel)
        {
            VisualElement row = Row();
            row.Add(FixedLabel(label, 150));
            double pct = max <= 0 ? 0 : value / max * 100.0;
            row.Add(ScoreBar(pct, 150));
            Label valueText = Ellipsis(Muted(valueLabel));
            valueText.style.flexGrow = 1;
            row.Add(valueText);
            return row;
        }

        private Texture2D LoadThumb(string path)
        {
            if (_thumbs.TryGetValue(path, out Texture2D cached))
            {
                return cached;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                Texture2D tex = new(2, 2);
                if (tex.LoadImage(bytes))
                {
                    _thumbs[path] = tex;
                    return tex;
                }

                DestroyImmediate(tex);
            }
            catch
            {
                // Ignore missing or invalid thumbnails.
            }

            _thumbs[path] = null;
            return null;
        }

        private void OnDisable()
        {
            foreach (Texture2D tex in _thumbs.Values)
            {
                if (tex != null)
                {
                    DestroyImmediate(tex);
                }
            }

            _thumbs.Clear();
        }

        private static void SetVisible(VisualElement element, bool visible)
        {
            if (element != null)
            {
                element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        private static void OpenImage(string path)
        {
            try
            {
                Application.OpenURL("file:///" + path.Replace("\\", "/"));
            }
            catch
            {
                EditorUtility.RevealInFinder(path);
            }
        }

        private static string ScenarioFromPng(string png, string stem)
        {
            string fn = Path.GetFileNameWithoutExtension(png);
            return fn.Length > stem.Length + 1 && fn.StartsWith(stem + "_", StringComparison.Ordinal)
                ? fn.Substring(stem.Length + 1)
                : fn;
        }

        private static string DimShort(string dim)
        {
            return dim switch
            {
                "ToolCorrectness" => "Tools",
                "IntentSequence" => "Intent",
                "TaskCompletion" => "Task",
                "Determinism" => "Determ",
                "Reasoning" => "Reason",
                "InstructionAdherence" => "Instr",
                _ => dim
            };
        }

        private static string FormatTokens(long tokens)
        {
            return tokens >= 1000 ? $"{Inv(tokens / 1000.0)}k tok" : $"{tokens} tok";
        }

        private static string Inv(double v, string fmt = "0.#")
        {
            return v.ToString(fmt, CultureInfo.InvariantCulture);
        }

        private static string FormatDuration(double ms)
        {
            double sec = ms / 1000.0;
            if (sec < 60)
            {
                return $"{Inv(sec, "0")}s";
            }

            int m = (int)(sec / 60);
            int s = (int)(sec % 60);
            return $"{m}m{s:00}s";
        }

        private static readonly string[] MonthAbbr =
            { "", "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

        // Compact, no-year run timestamp (e.g. "30 Jun · 14:57"). RunIds carry yyyyMMdd_HHmm; the year is
        // dropped so the date fits the narrow columns without truncation, staying easy to read.
        private static string FormatRunDate(string runId)
        {
            if (!string.IsNullOrEmpty(runId) && runId.Length == 15 && runId[8] == '_')
            {
                string day = runId.Substring(6, 2);
                int mi = int.TryParse(runId.Substring(4, 2), out int mm) && mm >= 1 && mm <= 12 ? mm : 0;
                return $"{day} {MonthAbbr[mi]} · {runId.Substring(9, 2)}:{runId.Substring(11, 2)}";
            }

            return runId ?? "";
        }

        private static string RoleVerdictShort(double rating)
        {
            if (rating >= 8.0)
            {
                return "Strong fit";
            }

            if (rating >= 6.5)
            {
                return "Usable";
            }

            return rating >= 4.0 ? "Limited" : "Not suitable";
        }

        private static string Fallback(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
        }

        private static Color ScoreColor(double score)
        {
            if (score >= 75)
            {
                return new Color(0.30f, 0.72f, 0.40f);
            }

            return score >= 50 ? new Color(0.92f, 0.74f, 0.25f) : new Color(0.86f, 0.36f, 0.34f);
        }
    }
}
