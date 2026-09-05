using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CoreAI.Ai;
using CoreAI.Authority;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode
{
    /// <summary>
    /// MVP11 gate N11.1, the half that needs no transport: what an admission decision may be.
    /// </summary>
    /// <remarks>
    /// WHY these are contract tests rather than implementation tests: admission is the one point where
    /// a remote stranger becomes an actor with a Player, mods, chat and world access. Every mistake
    /// here is a privilege escalation, and each is cheap to make — an "admitted" result carrying no
    /// context, a context minted with the host's own grants, a rejection that states no reason. The
    /// type refuses all three at construction, so these tests pin the refusal instead of trusting a
    /// comment to be read.
    /// </remarks>
    public sealed class ActorAdmissionEditModeTests
    {
        [Test]
        public void Admit_CarriesTheIdentityLuaWillRead()
        {
            ActorContext context = RestrictedContext();

            ActorAdmissionResult result = ActorAdmissionResult.Admit(context, 7331L, "neo", "Neo");

            Assert.IsTrue(result.Admitted);
            Assert.AreEqual(context.ActorId, result.Context.ActorId);
            Assert.IsTrue(result.Context.IsTrusted);
            Assert.AreEqual(7331L, result.UserId);
            Assert.AreEqual("neo", result.Name);
            Assert.AreEqual("Neo", result.DisplayName);
            Assert.AreEqual("", result.Reason, "an admission has nothing to explain");
        }

        [Test]
        public void Admit_WithoutADisplayName_FallsBackToTheAccountName()
        {
            // Roblox shows DisplayName where it has one and the username otherwise; a blank string
            // reaching Lua as Player.DisplayName renders as an empty label in every UI built on it.
            ActorAdmissionResult result =
                ActorAdmissionResult.Admit(RestrictedContext(), 12L, "builder", "   ");

            Assert.AreEqual("builder", result.DisplayName);
        }

        [Test]
        public void Negative_Admit_RefusesAnUnrestrictedContext()
        {
            // The one that matters: an unrestricted context IS the host's authority. A provider that
            // hands one to a connecting client has given away the world, and from the outside it
            // would look exactly like an ordinary successful join.
            ActorContext hostContext = HostContext();
            Assert.IsTrue(hostContext.Grants.IsUnrestricted, "the fixture must really be unrestricted");

            ArgumentException error = Assert.Throws<ArgumentException>(
                () => ActorAdmissionResult.Admit(hostContext, 1L, "host", "Host"));
            StringAssert.Contains("unrestricted", error.Message);
        }

        [Test]
        public void Negative_Admit_RefusesAContextNoIdentityProviderIssued()
        {
            // A default-constructed context is not "an actor with no grants" — it is no actor at all,
            // and the authorization paths downstream assert IsTrusted rather than inspecting grants.
            ArgumentException error = Assert.Throws<ArgumentException>(
                () => ActorAdmissionResult.Admit(default, 5L, "ghost", "Ghost"));
            StringAssert.Contains("IActorIdentityProvider", error.Message);
        }

        [Test]
        public void Negative_Admit_RefusesAbsentIdentity()
        {
            ActorContext context = RestrictedContext();

            Assert.Throws<ArgumentOutOfRangeException>(
                () => ActorAdmissionResult.Admit(context, 0L, "n", "N"),
                "UserId 0 is the absent-identity value; admitting it would give every anonymous "
                + "connection one shared Player identity");
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ActorAdmissionResult.Admit(context, -1L, "n", "N"));
            Assert.Throws<ArgumentException>(
                () => ActorAdmissionResult.Admit(context, 5L, "  ", "N"));
        }

        [Test]
        public void Reject_StatesWhy()
        {
            ActorAdmissionResult result = ActorAdmissionResult.Reject("signature mismatch");

            Assert.IsFalse(result.Admitted);
            Assert.AreEqual("signature mismatch", result.Reason);
            Assert.IsFalse(result.Context.IsTrusted,
                "a rejected connection carries no actor that could authorize anything");
            Assert.AreEqual(0L, result.UserId);
        }

        [Test]
        public void Negative_Reject_WithoutAReasonIsRefused()
        {
            // A silent rejection is indistinguishable from a transport fault in a log, and admission
            // failures are precisely what an operator has to diagnose from a log.
            Assert.Throws<ArgumentException>(() => ActorAdmissionResult.Reject(""));
            Assert.Throws<ArgumentException>(() => ActorAdmissionResult.Reject("   "));
            Assert.Throws<ArgumentException>(() => ActorAdmissionResult.Reject(null));
        }

        [Test]
        public void Credential_KeepsTheBytesOpaqueAndNeverNull()
        {
            ActorCredential unset = default;
            Assert.IsNotNull(unset.Opaque, "an unset credential reads as empty, it does not throw");
            Assert.AreEqual(0, unset.Opaque.Length);
            Assert.AreEqual("", unset.TransportAddress);

            ActorCredential credential = new(new byte[] { 1, 2, 3 }, "127.0.0.1:7777");
            Assert.AreEqual(3, credential.Opaque.Length);
            Assert.AreEqual("127.0.0.1:7777", credential.TransportAddress);
        }

        [Test]
        public void Negative_NoShippedTypeImplementsTheAdmissionPort()
        {
            // Decision 1: no anonymous fallback. A shipped implementation — whatever it were called —
            // becomes the default that every composition which forgot to configure admission ends up
            // with, which is the open door this port exists to close. Hosts implement it; CoreAI does
            // not. Test doubles live in test assemblies, which this scan excludes by name.
            List<string> shipped = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => assembly.GetName().Name.StartsWith("CoreAI", StringComparison.Ordinal)
                                   && assembly.GetName().Name.IndexOf("Test", StringComparison.Ordinal) < 0)
                .SelectMany(SafeTypes)
                .Where(type => !type.IsInterface
                               && !type.IsAbstract
                               && typeof(IActorAdmissionProvider).IsAssignableFrom(type))
                .Select(type => type.FullName)
                .ToList();

            Assert.IsEmpty(shipped,
                "CoreAI must ship no IActorAdmissionProvider; found: " + string.Join(", ", shipped));
        }

        private static ActorContext RestrictedContext(string actorId = "remote-1")
        {
            return new LocalActorIdentityProvider(
                    actorId,
                    "session-" + actorId,
                    "world-a",
                    ActorGrantSet.Create(new[] { "read" }),
                    AgentMemoryScope.Empty)
                .GetActorContext(BuiltInAgentRoleIds.SmartChat);
        }

        private static ActorContext HostContext()
        {
            // WHY reflection: the unrestricted capability is deliberately unreachable — it is guarded
            // by a proof object nested inside CoreAI.Source's installer. Reaching past that guard here
            // is what makes the fixture the REAL host context and not a lookalike, which is the only
            // version worth asserting against.
            Type composition = typeof(ActorContext).Assembly
                .GetType("CoreAI.Authority.ActorIdentityComposition", throwOnError: true);
            FieldInfo capabilityField = composition.GetField(
                "IssuanceCapability", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(capabilityField,
                "ActorIdentityComposition.IssuanceCapability moved; this fixture asserts against a "
                + "shape that no longer exists");

            MethodInfo issue = typeof(ActorContext).GetMethod(
                "IssueForComposition", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(issue);
            return (ActorContext)issue.Invoke(null, new[]
            {
                capabilityField.GetValue(null), "host", "session-host",
                BuiltInAgentRoleIds.SmartChat, "world-a", (object)AgentMemoryScope.Empty
            });
        }

        private static IEnumerable<Type> SafeTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(type => type != null);
            }
        }
    }
}
