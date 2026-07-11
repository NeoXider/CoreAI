using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Guards <see cref="LlmConversationalRolePolicy"/> heuristics for offline/stub short replies.
    /// </summary>
    public sealed class LlmConversationalRolePolicyEditModeTests
    {
        [Test]
        public void PlainChat_SmartChat_And_AiNpc_AreConversational()
        {
            Assert.IsTrue(LlmConversationalRolePolicy.IsConversationalUserFacingRole(BuiltInAgentRoleIds.PlainChat));
            Assert.IsTrue(LlmConversationalRolePolicy.IsConversationalUserFacingRole(BuiltInAgentRoleIds.SmartChat));
            Assert.IsTrue(LlmConversationalRolePolicy.IsConversationalUserFacingRole(BuiltInAgentRoleIds.AiNpc));
        }

        [Test]
        public void Teacher_Mentor_Tutor_AreConversational()
        {
            Assert.IsTrue(LlmConversationalRolePolicy.IsConversationalUserFacingRole("Teacher"));
            Assert.IsTrue(LlmConversationalRolePolicy.IsConversationalUserFacingRole("RedoSchool.LessonMentor"));
            Assert.IsTrue(LlmConversationalRolePolicy.IsConversationalUserFacingRole("Game.TutorAI"));
        }

        [Test]
        public void Creator_Programmer_Merchant_AreNotConversational()
        {
            Assert.IsFalse(LlmConversationalRolePolicy.IsConversationalUserFacingRole(BuiltInAgentRoleIds.Creator));
            Assert.IsFalse(LlmConversationalRolePolicy.IsConversationalUserFacingRole(BuiltInAgentRoleIds.Programmer));
            Assert.IsFalse(LlmConversationalRolePolicy.IsConversationalUserFacingRole(BuiltInAgentRoleIds.Merchant));
        }

        [Test]
        public void RoleEndingWithChat_IsConversational()
        {
            Assert.IsTrue(LlmConversationalRolePolicy.IsConversationalUserFacingRole("MyGame.PlayerSideChat"));
        }

        [Test]
        public void Merchant_IsNotTreatedAsChatBySubstring()
        {
            Assert.IsFalse(LlmConversationalRolePolicy.IsConversationalUserFacingRole("Merchant"));
        }
    }
}
