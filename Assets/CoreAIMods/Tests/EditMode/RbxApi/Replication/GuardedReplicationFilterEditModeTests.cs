using System;
using System.Collections.Generic;
using System.IO;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.Replication;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Replication
{
    /// <summary>
    /// Replication phase 0: the floor a game's filter cannot lift, and the default filter widened to
    /// what the Roblox class reference actually replicates.
    /// </summary>
    /// <remarks>
    /// WHY an allow-all game filter is the fixture for the floor: a floor that only held against a
    /// careful filter would not be a floor. Each negative here is a leak the game could not have
    /// caused on purpose, and must not be able to cause by accident either.
    /// </remarks>
    [TestFixture]
    public sealed class GuardedReplicationFilterEditModeTests
    {
        private InstanceRegistry _registry;
        private RbxDataModel _game;
        private RbxPlayers _players;
        private RbxPlayer _alice;
        private RbxPlayer _bob;
        private List<string> _diagnostics;

        [SetUp]
        public void CreateWorld()
        {
            _registry = new InstanceRegistry(
                binder: new InMemoryInstanceBackingBinder(),
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                worldId: "guard-world");
            _diagnostics = new List<string>();
            _registry.Diagnostics = _diagnostics.Add;
            _game = DataModelBootstrap.CreateGame(_registry);
            _players = (RbxPlayers)_game.GetService("Players");
            _players.CharacterAutoLoads = false;
            _alice = _players.EnsureActor(_registry, "alice");
            _bob = _players.EnsureActor(_registry, "bob");
        }

        [Test]
        public void Negative_AnAllowAllGameFilter_CannotSeeServerOnlyContainers()
        {
            GuardedReplicationFilter guard = new(new AllowAll(), _registry);
            RbxInstance storage = Under(_game.GetService("ServerStorage"), "Secret");
            RbxInstance logic = Under(_game.GetService("ServerScriptService"), "Logic");

            Assert.IsFalse(guard.IsVisibleTo("alice", storage));
            Assert.IsFalse(guard.IsVisibleTo("alice", logic));
            Assert.IsFalse(guard.IsVisibleTo("alice", _game.GetService("ServerStorage")));
        }

        [Test]
        public void Negative_AnAllowAllGameFilter_CannotSeeTheCamera()
        {
            GuardedReplicationFilter guard = new(new AllowAll(), _registry);
            RbxInstance camera = _registry.WorldRoot.FindFirstChildOfClass("Camera");
            RbxInstance underCamera = Under(camera, "Rig");

            Assert.IsFalse(guard.IsVisibleTo("alice", camera));
            Assert.IsFalse(guard.IsVisibleTo("alice", underCamera));
        }

        [Test]
        public void Negative_AnAllowAllGameFilter_CannotSeeAnotherPlayersContainers()
        {
            GuardedReplicationFilter guard = new(new AllowAll(), _registry);
            foreach (string container in new[] { "Backpack", "PlayerGui" })
            {
                RbxInstance alicesContainer = _alice.FindFirstChildOfClass(container);
                RbxInstance content = Under(alicesContainer, "Private");

                Assert.IsFalse(guard.IsVisibleTo("bob", alicesContainer), container + " must be hidden from bob");
                Assert.IsFalse(guard.IsVisibleTo("bob", content), container + " content must be hidden from bob");
                Assert.IsTrue(guard.IsVisibleTo("alice", content),
                    container + " is the owner's: the floor hides it from the others, not from alice");
            }
        }

        [Test]
        public void Negative_AnAllowAllGameFilter_CannotSeePlayerScripts_NotEvenTheOwners()
        {
            GuardedReplicationFilter guard = new(new AllowAll(), _registry);
            RbxInstance scripts = _alice.FindFirstChildOfClass("PlayerScripts");
            RbxInstance content = Under(scripts, "Private");

            Assert.IsFalse(guard.IsVisibleTo("bob", scripts));
            Assert.IsFalse(guard.IsVisibleTo("bob", content));
            Assert.IsFalse(guard.IsVisibleTo("alice", scripts),
                "PlayerScripts.yaml: NotReplicated — client-local, so not even its own player receives it");
            Assert.IsFalse(guard.IsVisibleTo("alice", content));
        }

        [Test]
        public void VisibilityIsAncestorClosed_AChildNeverArrivesWithoutItsParent()
        {
            HideNamed inner = new("Hidden");
            GuardedReplicationFilter guard = new(inner, _registry);
            RbxInstance hidden = Under(_registry.WorldRoot, "Hidden");
            RbxInstance inside = Under(hidden, "Inside");

            Assert.IsTrue(inner.IsVisibleTo("alice", inside), "the game filter alone would leak the child");
            Assert.IsFalse(guard.IsVisibleTo("alice", inside));
            Assert.IsTrue(guard.IsVisibleTo("alice", _registry.WorldRoot));
        }

        [Test]
        public void Negative_AFilterThatThrows_IsNotVisible_AndIsReported()
        {
            GuardedReplicationFilter guard = new(new Throws(), _registry);
            RbxInstance part = Under(_registry.WorldRoot, "Door");

            Assert.IsFalse(guard.IsVisibleTo("alice", part));
            Assert.IsFalse(guard.IsMemberVisibleTo("alice", part, ReplicationMembers.Name));

            Assert.AreEqual(2, _diagnostics.Count, "each fault is reported once");
            StringAssert.Contains("treated as not visible", _diagnostics[0]);
            StringAssert.Contains(nameof(Throws), _diagnostics[0]);
            StringAssert.Contains("member 'Name'", _diagnostics[1]);
        }

        [Test]
        public void Negative_AFilterThatThrows_WhileTheDiagnosticsSinkThrowsToo_IsStillNotVisible_AndNothingEscapes()
        {
            // WHY: "a filter that throws is not visible" is a security floor; a report of the fault
            // that threw in turn would turn the floor into an exception at the planner, one level down.
            _registry.Diagnostics = _ => throw new InvalidOperationException("the host's logger is broken too");
            GuardedReplicationFilter guard = new(new Throws(), _registry);
            RbxInstance part = Under(_registry.WorldRoot, "Door");
            bool visible = true;
            bool memberVisible = true;

            Assert.DoesNotThrow(() => visible = guard.IsVisibleTo("alice", part));
            Assert.DoesNotThrow(() => memberVisible = guard.IsMemberVisibleTo("alice", part, ReplicationMembers.Name));

            Assert.IsFalse(visible, "a filter fault still hides, whatever the sink does");
            Assert.IsFalse(memberVisible);
            Assert.AreEqual(2, _registry.DiagnosticsFaults, "each report the sink refused is counted");
        }

        [Test]
        public void TheMemberQuestion_DefaultsToVisible_ForAFilterThatOnlyThinksInInstances()
        {
            GuardedReplicationFilter guard = new(new AllowAll(), _registry);
            RbxInstance part = Under(_registry.WorldRoot, "Door");

            Assert.IsTrue(guard.IsMemberVisibleTo("alice", part, ReplicationMembers.Name));
            IReplicationFilter bare = new AllowAll();
            Assert.IsTrue(bare.IsMemberVisibleTo("alice", part, ReplicationMembers.Archivable),
                "the interface default answers for a filter that never overrode it");
        }

        [Test]
        public void TheMemberQuestion_StillRespectsTheFloor()
        {
            GuardedReplicationFilter guard = new(new AllowAll(), _registry);
            RbxInstance secret = Under(_game.GetService("ServerStorage"), "Secret");

            Assert.IsFalse(guard.IsMemberVisibleTo("alice", secret, ReplicationMembers.Name));
        }

        [Test]
        public void Wrap_GuardsOnce_AndDefaultsToTheDefaultFilter()
        {
            GuardedReplicationFilter first = GuardedReplicationFilter.Wrap(null, _registry);
            GuardedReplicationFilter again = GuardedReplicationFilter.Wrap(first, _registry);

            Assert.AreSame(DefaultReplicationFilter.Instance, first.Inner);
            Assert.AreSame(first, again, "wrapping a guard must not stack a second floor");
        }

        [Test]
        public void DefaultFilter_ReplicatesWhatTheReferenceDoesNotMarkNotReplicated()
        {
            DefaultReplicationFilter filter = DefaultReplicationFilter.Instance;

            Assert.IsTrue(filter.IsVisibleTo("bob", _game), "the DataModel root");
            Assert.IsTrue(filter.IsVisibleTo("bob", _players), "Players.yaml: a Service with no replication tag");
            Assert.IsTrue(filter.IsVisibleTo("bob", _alice), "Player.yaml: every client sees every connected Player");
            Assert.IsTrue(filter.IsVisibleTo("bob", Under(_game.GetService("StarterPlayer"), "StarterPlayerScripts")),
                "StarterPlayer.yaml: a Service with no replication tag");
            Assert.IsTrue(filter.IsVisibleTo("bob", Under(_game.GetService("MaterialService"), "Variant")),
                "MaterialService.yaml: a Service with no replication tag");
            Assert.IsTrue(filter.IsVisibleTo("bob", Under(_game.GetService("Lighting"), "Sky")));
            Assert.IsTrue(filter.IsVisibleTo("bob", Under(_game.GetService("ReplicatedStorage"), "Shared")));
            Assert.IsTrue(filter.IsVisibleTo("bob", Under(_registry.WorldRoot, "Door")));
        }

        [Test]
        public void Negative_DefaultFilter_HidesWhatTheReferenceMarksNotReplicated()
        {
            DefaultReplicationFilter filter = DefaultReplicationFilter.Instance;

            Assert.IsFalse(filter.IsVisibleTo("alice", _game.GetService("ServerStorage")));
            Assert.IsFalse(filter.IsVisibleTo("alice", _game.GetService("ServerScriptService")));
            Assert.IsFalse(filter.IsVisibleTo("alice", _game.GetService("RunService")), "RunService.yaml: NotReplicated");
            Assert.IsFalse(filter.IsVisibleTo("alice", _game.GetService("UserInputService")), "UserInputService.yaml: NotReplicated");
            Assert.IsFalse(filter.IsVisibleTo("alice", _game.GetService("ScriptContext")), "ScriptContext.yaml: NotReplicated");
            Assert.IsFalse(filter.IsVisibleTo("alice", _registry.WorldRoot.FindFirstChildOfClass("Camera")), "Camera.yaml: NotReplicated");
            Assert.IsFalse(filter.IsVisibleTo("alice", _alice.FindFirstChildOfClass("PlayerScripts")),
                "PlayerScripts.yaml: NotReplicated, even for its own player");
        }

        [Test]
        public void DefaultFilter_PlayerContainers_GoToTheirOwnerOnly()
        {
            DefaultReplicationFilter filter = DefaultReplicationFilter.Instance;
            RbxInstance sword = Under(_alice.FindFirstChildOfClass("Backpack"), "Sword");
            RbxInstance gui = Under(_alice.FindFirstChildOfClass("PlayerGui"), "Hud");

            Assert.IsTrue(filter.IsVisibleTo("alice", sword), "Backpack.yaml: LocalPlayer.Backpack is the owner's");
            Assert.IsTrue(filter.IsVisibleTo("alice", gui), "PlayerGui.yaml: PlayerReplicated");
            Assert.IsFalse(filter.IsVisibleTo("bob", sword));
            Assert.IsFalse(filter.IsVisibleTo("bob", gui));
            Assert.IsFalse(filter.IsVisibleTo("bob", _alice.FindFirstChildOfClass("Backpack")));
        }

        [Test]
        public void Negative_DefaultFilter_HidesAnythingNotUnderADataModel()
        {
            RbxInstance orphan = _registry.Create("Part");
            RbxInstance detachedChild = Under(orphan, "Child");

            Assert.IsFalse(DefaultReplicationFilter.Instance.IsVisibleTo("alice", orphan));
            Assert.IsFalse(DefaultReplicationFilter.Instance.IsVisibleTo("alice", detachedChild));
        }

        [Test]
        public void Negative_EverythingTheBootstrapPutsInTheTree_ReplicatesExactlyWhenItsYamlLacksNotReplicated()
        {
            DefaultReplicationFilter filter = DefaultReplicationFilter.Instance;
            GuardedReplicationFilter guarded = GuardedReplicationFilter.Wrap(null, _registry);
            List<RbxInstance> tree = new() { _game };
            tree.AddRange(_game.GetDescendants());
            Assert.Greater(tree.Count, 10, "the bootstrapped tree, not an empty registry, is what is walked");

            foreach (RbxInstance node in tree)
            {
                Assert.IsTrue(MirrorTagsNotReplicated.TryGetValue(node.ClassName, out bool notReplicated),
                    node.ClassName + " reached the bootstrap tree with no verdict in this test: open classes/"
                    + node.ClassName + ".yaml in the mirror, read its class-level tags and record it in "
                    + nameof(MirrorTagsNotReplicated));
                string recipient = OwnerOf(node) ?? "alice";
                Assert.AreEqual(!notReplicated, filter.IsVisibleTo(recipient, node),
                    node.ClassName + " '" + node.Name + "' must reach " + recipient
                    + " exactly when its yaml lacks NotReplicated");
                Assert.AreEqual(!notReplicated, guarded.IsVisibleTo(recipient, node),
                    "the floor must agree with the reference on " + node.ClassName + " '" + node.Name + "'");
            }
        }

        [Test]
        public void TheMirrorTranscription_MatchesTheLocalMirror_WhenItIsOnDisk()
        {
            string classes = LocalMirrorClassesDirectory();
            if (classes == null)
            {
                Assert.Ignore("no local Roblox docs mirror on this machine; set ROBLOX_DOCS_MIRROR to its checkout root");
            }

            foreach (KeyValuePair<string, bool> verdict in MirrorTagsNotReplicated)
            {
                string yaml = Path.Combine(classes, verdict.Key + ".yaml");
                Assert.IsTrue(File.Exists(yaml), yaml + " is missing from the mirror");
                Assert.AreEqual(verdict.Value, ClassLevelTags(File.ReadAllLines(yaml)).Contains("NotReplicated"),
                    verdict.Key + ": the transcription in this test disagrees with " + yaml);
            }
        }

        [Test]
        public void EnumerateReplicable_CarriesEveryMemberTheReferenceLeavesUntagged_AndOnlyTheWrittenDeviationsBeyond()
        {
            foreach (KeyValuePair<string, bool> verdict in MirrorMemberTagsNotReplicated)
            {
                (string className, string member) = SplitMember(verdict.Key);
                RbxInstance instance = _registry.Create(CreatableClassFor(className));
                instance.Parent = _registry.WorldRoot;
                List<string> members = ReplicationMembers.EnumerateReplicable(instance, includeParent: true);

                if (!verdict.Value)
                {
                    Assert.Contains(member, members, verdict.Key + " is untagged in the mirror and must be carried");
                    Assert.IsFalse(WrittenDeviations.ContainsKey(verdict.Key),
                        verdict.Key + " needs no written deviation: the mirror replicates it");
                }
                else if (WrittenDeviations.TryGetValue(verdict.Key, out string reason))
                {
                    Assert.Contains(member, members,
                        verdict.Key + " is NotReplicated in the mirror and carried by a written decision: " + reason);
                }
                else
                {
                    CollectionAssert.DoesNotContain(members, member,
                        verdict.Key + " is NotReplicated in the mirror with no deviation written for it, so it "
                        + "must not be carried; either drop it from ReplicationMembers or write the deviation "
                        + "there and record it in " + nameof(WrittenDeviations));
                }
            }
        }

        [Test]
        public void Negative_EveryMemberReplicationMembersEnumerates_HasAVerdictInTheMemberTranscription()
        {
            foreach (string className in new[]
                     {
                         "Folder", "Model", "IntValue", "NumberValue", "StringValue", "BoolValue", "ObjectValue",
                         "Vector3Value", "CFrameValue", "Color3Value", "Player"
                     })
            {
                RbxInstance instance = _registry.Create(className);
                instance.Parent = _registry.WorldRoot;
                instance.SetAttribute("Hp", 3d);
                instance.AddTag("Enemy");

                foreach (string member in ReplicationMembers.EnumerateReplicable(instance, includeParent: true))
                {
                    if (ReplicationMembers.TryGetAttribute(member, out _) || ReplicationMembers.TryGetTag(member, out _))
                    {
                        continue;
                    }

                    string qualified = MirrorClassFor(member, instance) + "." + member;
                    Assert.IsTrue(MirrorMemberTagsNotReplicated.ContainsKey(qualified),
                        qualified + " is enumerated with no verdict in this test: open classes/"
                        + MirrorClassFor(member, instance) + ".yaml in the mirror, read the member's own tags and "
                        + "record it in " + nameof(MirrorMemberTagsNotReplicated));
                }
            }
        }

        [Test]
        public void TheMemberTranscription_MatchesTheLocalMirror_WhenItIsOnDisk()
        {
            string classes = LocalMirrorClassesDirectory();
            if (classes == null)
            {
                Assert.Ignore("no local Roblox docs mirror on this machine; set ROBLOX_DOCS_MIRROR to its checkout root");
            }

            foreach (KeyValuePair<string, bool> verdict in MirrorMemberTagsNotReplicated)
            {
                (string className, string _) = SplitMember(verdict.Key);
                string yaml = Path.Combine(classes, className + ".yaml");
                Assert.IsTrue(File.Exists(yaml), yaml + " is missing from the mirror");
                HashSet<string> tags = MemberLevelTags(File.ReadAllLines(yaml), verdict.Key);
                Assert.IsNotNull(tags, verdict.Key + " is not listed in " + yaml);
                Assert.AreEqual(verdict.Value, tags.Contains("NotReplicated"),
                    verdict.Key + ": the transcription in this test disagrees with " + yaml);
            }
        }

        [Test]
        public void Negative_DefaultFilter_HidesAIService_ByCoreAIsOwnDecision()
        {
            // WHY a catalog of its own: AIService is a ServiceCatalog stub today with no class
            // descriptor, so registering one here is the only way to put it in a tree.
            ClassCatalog catalog = ClassCatalog.CreateMvp1();
            catalog.Register(new ClassDescriptor("AIService", "Instance", false, false, true));
            InstanceRegistry registry = new(catalog: catalog, binder: new InMemoryInstanceBackingBinder());
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            RbxInstance ai = registry.Create("AIService");
            ai.Parent = game;
            RbxInstance memory = registry.Create("Folder");
            memory.Name = "Memory";
            memory.Parent = ai;

            Assert.IsFalse(DefaultReplicationFilter.Instance.IsVisibleTo("alice", ai),
                "OURS: no yaml exists; the server's agent bridge is scoped like DataStoreService");
            Assert.IsFalse(DefaultReplicationFilter.Instance.IsVisibleTo("alice", memory));
        }

        // WHY a transcription rather than a live read of the mirror: the mirror is a local checkout
        // not every machine running these tests has, and the door this table closes must close
        // everywhere — a class the bootstrap puts in the tree without a verdict here fails the walk
        // until someone opens classes/<Name>.yaml and records its class-level tags. Where the mirror
        // is on disk, TheMirrorTranscription_MatchesTheLocalMirror catches a transcription that
        // drifted from it.
        private static readonly Dictionary<string, bool> MirrorTagsNotReplicated = new(StringComparer.Ordinal)
        {
            ["DataModel"] = false,
            ["Workspace"] = false,
            ["Camera"] = true,
            ["Lighting"] = false,
            ["ReplicatedStorage"] = false,
            ["ServerStorage"] = true,
            ["ServerScriptService"] = true,
            ["StarterPlayer"] = false,
            ["Players"] = false,
            ["UserInputService"] = true,
            ["RunService"] = true,
            ["HttpService"] = false,
            ["MaterialService"] = false,
            ["Debris"] = false,
            ["CollectionService"] = false,
            ["TweenService"] = false,
            ["ScriptContext"] = true,
            ["Player"] = false,
            ["Backpack"] = false,
            ["PlayerGui"] = false,
            ["PlayerScripts"] = true
        };

        // WHY a member-level transcription beside the class-level one: the mirror tags members as
        // well as classes, and ReplicationMembers carries members by name. Verdicts are the member's
        // own "tags:" block in classes/<Class>.yaml; a member enumerated without one fails the walk
        // above, and where the mirror is on disk TheMemberTranscription_MatchesTheLocalMirror catches
        // a transcription that drifted from it.
        private static readonly Dictionary<string, bool> MirrorMemberTagsNotReplicated = new(StringComparer.Ordinal)
        {
            ["Instance.Name"] = false,
            ["Instance.Archivable"] = false,
            ["Instance.Parent"] = true,
            ["Model.PrimaryPart"] = false,
            ["Model.WorldPivot"] = true,
            ["IntValue.Value"] = false,
            ["NumberValue.Value"] = false,
            ["StringValue.Value"] = false,
            ["BoolValue.Value"] = false,
            ["ObjectValue.Value"] = false,
            ["Vector3Value.Value"] = false,
            ["CFrameValue.Value"] = false,
            ["Color3Value.Value"] = false,
            ["Player.Character"] = false,
            ["Player.DisplayName"] = false
        };

        // WHY a second table: a member the mirror tags NotReplicated may still be carried, but only
        // with its reason written at ReplicationMembers — this is the list of those, so a third one
        // cannot appear by accident. The reasons are summaries of what is written there.
        private static readonly Dictionary<string, string> WrittenDeviations = new(StringComparer.Ordinal)
        {
            ["Instance.Parent"] = "the tag is about the property; the same yaml's Object Replication section "
                                  + "describes the hierarchy move that does replicate, and this member is that move",
            ["Model.WorldPivot"] = "the scriptable proxy is NotReplicated and unsaveable; its hidden backing "
                                   + "Model.WorldPivotData (Full-API-Dump 0.731: Hidden, NotScriptable, untagged) "
                                   + "replicates, and the stored pivot is that backing under the scriptable name"
        };

        private static (string ClassName, string Member) SplitMember(string qualifiedMember)
        {
            int dot = qualifiedMember.IndexOf('.');
            return (qualifiedMember.Substring(0, dot), qualifiedMember.Substring(dot + 1));
        }

        // WHY: Instance.yaml is NotCreatable; a Folder is the plainest instance that carries its members.
        private static string CreatableClassFor(string mirrorClassName)
        {
            return string.Equals(mirrorClassName, "Instance", StringComparison.Ordinal) ? "Folder" : mirrorClassName;
        }

        private static string MirrorClassFor(string member, RbxInstance instance)
        {
            switch (member)
            {
                case ReplicationMembers.Name:
                case ReplicationMembers.Archivable:
                case ReplicationMembers.Parent:
                    return "Instance";
                default:
                    return instance.ClassName;
            }
        }

        // WHY a line scan again: a member's block starts at "  - name: Class.Member", its own "tags:"
        // sits at four spaces followed by "      - Tag" lines or written as "    tags: []", and anything
        // deeper is description text.
        private static HashSet<string> MemberLevelTags(string[] lines, string qualifiedMember)
        {
            string header = "  - name: " + qualifiedMember;
            for (int index = 0; index < lines.Length; index++)
            {
                if (!string.Equals(lines[index], header, StringComparison.Ordinal))
                {
                    continue;
                }

                HashSet<string> tags = new(StringComparer.Ordinal);
                for (int next = index + 1; next < lines.Length; next++)
                {
                    string line = lines[next];
                    if (line.StartsWith("  - name: ", StringComparison.Ordinal)
                        || (line.Length > 0 && line[0] != ' '))
                    {
                        break;
                    }

                    if (!line.StartsWith("    tags:", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    for (int tag = next + 1;
                         tag < lines.Length && lines[tag].StartsWith("      - ", StringComparison.Ordinal);
                         tag++)
                    {
                        tags.Add(lines[tag].Substring(8).Trim());
                    }

                    return tags;
                }

                return tags;
            }

            return null;
        }

        private static string OwnerOf(RbxInstance node)
        {
            for (RbxInstance ancestor = node; ancestor != null; ancestor = ancestor.Parent)
            {
                if (ancestor is RbxPlayer player)
                {
                    return player.NetworkActorId;
                }
            }

            return null;
        }

        private static string LocalMirrorClassesDirectory()
        {
            string root = Environment.GetEnvironmentVariable("ROBLOX_DOCS_MIRROR");
            if (string.IsNullOrWhiteSpace(root))
            {
                root = @"D:\Git\RobloxDocs";
            }

            string classes = Path.Combine(root, "creator-docs", "content", "en-us", "reference", "engine", "classes");
            return Directory.Exists(classes) ? classes : null;
        }

        // WHY a line scan and not a yaml parser: the class-level block is the only "tags:" at column
        // zero, followed by "  - Tag" lines or written as "tags: []"; member-level tags are indented.
        private static HashSet<string> ClassLevelTags(string[] lines)
        {
            HashSet<string> tags = new(StringComparer.Ordinal);
            for (int index = 0; index < lines.Length; index++)
            {
                if (!lines[index].StartsWith("tags:", StringComparison.Ordinal))
                {
                    continue;
                }

                for (int next = index + 1; next < lines.Length; next++)
                {
                    if (!lines[next].StartsWith("  - ", StringComparison.Ordinal))
                    {
                        break;
                    }

                    tags.Add(lines[next].Substring(4).Trim());
                }

                return tags;
            }

            return tags;
        }

        private RbxInstance Under(RbxInstance parent, string name)
        {
            RbxInstance folder = _registry.Create("Folder");
            folder.Name = name;
            folder.Parent = parent;
            return folder;
        }

        private sealed class AllowAll : IReplicationFilter
        {
            public bool IsVisibleTo(string recipientActorId, RbxInstance instance)
            {
                return true;
            }
        }

        private sealed class HideNamed : IReplicationFilter
        {
            private readonly string _name;

            public HideNamed(string name)
            {
                _name = name;
            }

            public bool IsVisibleTo(string recipientActorId, RbxInstance instance)
            {
                return !string.Equals(instance.Name, _name, StringComparison.Ordinal);
            }
        }

        private sealed class Throws : IReplicationFilter
        {
            public bool IsVisibleTo(string recipientActorId, RbxInstance instance)
            {
                throw new InvalidOperationException("the game's filter has a bug");
            }

            public bool IsMemberVisibleTo(string recipientActorId, RbxInstance instance, string member)
            {
                throw new InvalidOperationException("the game's member filter has a bug");
            }
        }
    }
}
