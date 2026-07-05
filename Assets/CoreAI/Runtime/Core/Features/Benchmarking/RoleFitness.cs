using System;
using System.Collections.Generic;
using System.Linq;

namespace CoreAI.Benchmarking
{
    /// <summary>
    /// Translates the benchmark's per-dimension scores (+ speed) into a plain-language verdict: how well
    /// the model fits each game-dev ROLE, rated 0..10 with a one-line reason. This is the benchmark's
    /// headline answer — "is this model usable for my game, and for which role" — and it is what makes a
    /// tiny 2B/0.8B model read clearly as "Not suitable for agentic roles" instead of a misleading mid score.
    /// </summary>
    public static class RoleFitness
    {
        // Signal keys: the six dimensions plus a derived Speed score.
        private const string Tool = "Tool";
        private const string Intent = "Intent";
        private const string Task = "Task";
        private const string Determ = "Determ";
        private const string Reason = "Reason";
        private const string Instr = "Instr";
        private const string Speed = "Speed";

        /// <summary>tok/s that maps to a 100 speed score (local small models commonly land 10–50).</summary>
        public const double SpeedTargetTokensPerSecond = 25.0;

        public sealed class RoleScore
        {
            public string Role { get; set; } = "";
            public double Rating { get; set; } // 0..10
            public string Verdict { get; set; } = ""; // Strong fit / Usable / Limited / Not suitable / Not assessed
            public string Reason { get; set; } = "";
            public bool Agentic { get; set; }

            /// <summary>False when this run did not measure all the dimensions the role depends on (partial run).</summary>
            public bool Assessed { get; set; } = true;
        }

        public sealed class Result
        {
            public List<RoleScore> Roles { get; } = new();
            public double Overall { get; set; }
            public string BestRole { get; set; } = "";
            public bool TinyModelWarning { get; set; }
        }

        private sealed class Profile
        {
            public string Name;
            public bool Agentic;
            public Dictionary<string, double> Weights;
            public (string Key, double Min)[] Gates;
            public string Note;
        }

        private static readonly Profile[] Profiles =
        {
            new()
            {
                Name = "NPC / Dialogue", Agentic = false,
                Weights = new Dictionary<string, double> { [Tool] = .35, [Task] = .30, [Speed] = .25, [Instr] = .10 },
                Gates = new[] { (Tool, 40.0), (Task, 40.0) },
                Note = "simple in-character turns with occasional tool use"
            },
            new()
            {
                Name = "Mechanic / GameMaster", Agentic = true,
                Weights = new Dictionary<string, double>
                    { [Instr] = .25, [Tool] = .25, [Task] = .20, [Speed] = .20, [Intent] = .10 },
                Gates = new[] { (Tool, 65.0), (Instr, 60.0) },
                Note = "drives runtime gameplay — needs strict instructions, valid tools, and speed"
            },
            new()
            {
                Name = "Scene / Tool Operator", Agentic = true,
                Weights = new Dictionary<string, double>
                    { [Tool] = .35, [Intent] = .20, [Instr] = .20, [Task] = .15, [Determ] = .10 },
                Gates = new[] { (Tool, 75.0), (Instr, 65.0), (Intent, 55.0) },
                Note = "builds/edits scenes — fails fast when tool calls or ordering are unreliable"
            },
            new()
            {
                Name = "Programmer / Logic Author", Agentic = true,
                Weights = new Dictionary<string, double>
                    { [Reason] = .35, [Tool] = .25, [Task] = .20, [Instr] = .10, [Determ] = .10 },
                Gates = new[] { (Reason, 70.0), (Tool, 65.0), (Task, 60.0) },
                Note = "authors game logic — needs reasoning plus reliable tool use, not speed"
            },
            new()
            {
                Name = "Orchestrator / Director", Agentic = true,
                Weights = new Dictionary<string, double>
                    { [Reason] = .30, [Intent] = .25, [Instr] = .20, [Task] = .15, [Determ] = .10 },
                Gates = new[] { (Reason, 80.0), (Intent, 75.0), (Instr, 75.0) },
                Note =
                    "multi-step control — current suite mostly measures task-level sequencing, not sustained multi-turn orchestration; needs high reasoning, sequencing, and instruction-following"
            },
            new()
            {
                Name = "QA / Regression Judge", Agentic = true,
                Weights = new Dictionary<string, double>
                    { [Determ] = .30, [Instr] = .25, [Reason] = .20, [Task] = .15, [Tool] = .10 },
                Gates = new[] { (Determ, 70.0), (Instr, 65.0), (Reason, 55.0) },
                Note = "validation — needs stable, rule-following judgments"
            }
        };

        // Weight of each agentic role in the overall blend.
        private static readonly Dictionary<string, double> AgenticBlend = new()
        {
            ["Mechanic / GameMaster"] = .30,
            ["Scene / Tool Operator"] = .20,
            ["Programmer / Logic Author"] = .20,
            ["Orchestrator / Director"] = .20,
            ["QA / Regression Judge"] = .10
        };

        public static Result Evaluate(BenchmarkReport report)
        {
            Dictionary<string, double> sig = new();
            foreach (DimensionScore d in report.DimensionBreakdown())
            {
                sig[Key(d.Dimension)] = d.Score;
            }

            double tps = report.GenerationTokensPerSecond;
            if (tps > 0)
            {
                sig[Speed] = Clamp(tps / SpeedTargetTokensPerSecond * 100.0, 0, 100);
            }

            // A model that cannot reliably call tools cannot do agentic work at all (the tiny-model case).
            // Weak instruction-following alone is NOT this — it is handled by each role's own gate, so a
            // model that reasons well but ignores strict rules can still rate as a Programmer.
            bool tiny = sig.TryGetValue(Tool, out double tc) && tc < 40;

            Result result = new() { TinyModelWarning = tiny };

            foreach (Profile p in Profiles)
            {
                RoleScore rs = Score(p, sig);
                if (tiny && p.Agentic && rs.Assessed)
                {
                    rs.Rating = Math.Min(rs.Rating, 2.9);
                    rs.Verdict = Verdict(rs.Rating);
                    rs.Reason = "Not suitable — tool correctness below the minimum for agentic work.";
                }

                result.Roles.Add(rs);
            }

            // Headline reflects AGENTIC game work only: a chatty model that merely answers in-character
            // (high NPC) must not inflate the overall "can it build a game" number. Roles whose dimensions
            // this run did not measure are excluded so a partial (single-group) run cannot over-claim.
            List<RoleScore> agentic = result.Roles.Where(r => r.Agentic && r.Assessed).ToList();
            if (agentic.Count > 0)
            {
                double maxRole = agentic.Max(r => r.Rating);
                double blend = 0, bw = 0;
                foreach (RoleScore r in agentic)
                {
                    double w = AgenticBlend.TryGetValue(r.Role, out double ww) ? ww : 0.1;
                    blend += r.Rating * w;
                    bw += w;
                }

                double agenticAvg = bw > 0 ? blend / bw : 0;
                result.Overall = Math.Round(0.65 * maxRole + 0.35 * agenticAvg, 1);
            }
            else
            {
                result.Overall = 0; // no agentic role could be assessed from this run's groups
            }

            RoleScore best = result.Roles
                .Where(r => r.Assessed)
                .OrderByDescending(r => r.Rating)
                .FirstOrDefault();
            result.BestRole = best?.Role ?? "";
            return result;
        }

        private static RoleScore Score(Profile p, Dictionary<string, double> sig)
        {
            double sum = 0, wsum = 0;
            string weakest = null;
            double weakestVal = double.MaxValue;
            List<string> missing = new();
            foreach (KeyValuePair<string, double> kv in p.Weights)
            {
                if (sig.TryGetValue(kv.Key, out double v))
                {
                    sum += v * kv.Value;
                    wsum += kv.Value;
                    if (v < weakestVal)
                    {
                        weakestVal = v;
                        weakest = kv.Key;
                    }
                }
                else if (kv.Key != Speed)
                {
                    // A scoring dimension this run never measured (e.g. a single-group run).
                    missing.Add(Label(kv.Key));
                }
            }

            // The role cannot be honestly rated unless every dimension it depends on was measured.
            if (missing.Count > 0)
            {
                return new RoleScore
                {
                    Role = p.Name,
                    Rating = 0,
                    Verdict = "Not assessed",
                    Reason = $"partial run — {string.Join(", ", missing)} not measured. {p.Note}.",
                    Agentic = p.Agentic,
                    Assessed = false
                };
            }

            double avg = wsum > 0 ? sum / wsum : 0;
            double rating = avg / 10.0;

            // Gate: any present gating signal below its minimum caps the role at "Not suitable".
            string failGate = null;
            double failVal = 0;
            foreach ((string key, double min) in p.Gates)
            {
                if (sig.TryGetValue(key, out double v) && v < min)
                {
                    failGate = key;
                    failVal = v;
                    break;
                }
            }

            if (failGate != null)
            {
                rating = Math.Min(rating, 3.9);
            }

            string verdict = Verdict(rating);
            string reason = failGate != null
                ? $"{Label(failGate)} too low ({failVal:0}) — {p.Note}."
                : $"{p.Note}. Weakest: {Label(weakest)} {weakestVal:0}.";

            return new RoleScore
            {
                Role = p.Name,
                Rating = Math.Round(rating, 1),
                Verdict = verdict,
                Reason = reason,
                Agentic = p.Agentic
            };
        }

        private static string Verdict(double rating)
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

        private static string Key(BenchmarkDimension d)
        {
            return d switch
            {
                BenchmarkDimension.ToolCorrectness => Tool,
                BenchmarkDimension.IntentSequence => Intent,
                BenchmarkDimension.TaskCompletion => Task,
                BenchmarkDimension.Determinism => Determ,
                BenchmarkDimension.Reasoning => Reason,
                BenchmarkDimension.InstructionAdherence => Instr,
                _ => d.ToString()
            };
        }

        private static string Label(string key)
        {
            return key switch
            {
                Tool => "tool correctness",
                Intent => "intent/sequence",
                Task => "task completion",
                Determ => "determinism",
                Reason => "reasoning",
                Instr => "instruction adherence",
                Speed => "speed",
                _ => key ?? "n/a"
            };
        }

        private static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : v > hi ? hi : v;
        }
    }
}