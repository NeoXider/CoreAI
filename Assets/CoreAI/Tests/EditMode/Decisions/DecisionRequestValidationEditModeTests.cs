using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Decisions;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode.Decisions
{
    /// <summary>
    /// Request validation: the protocol limits (1-255 choice options, 1-10 score levels), question and option ids,
    /// and image attachments, each refused with a named reason before anything is sent.
    /// </summary>
    [TestFixture]
    public sealed class DecisionRequestValidationEditModeTests
    {
        [TestCase(1)]
        [TestCase(255)]
        public void Choice_WithinOneTo255Options_IsValid(int count)
        {
            DecisionRequest request = new("s", DecisionQuestion.Choice("q", "pick", Options(count)));

            Assert.IsEmpty(request.Validate());
        }

        [Test]
        public void Choice_WithZeroOptions_IsRefusedAsTooFew()
        {
            DecisionRequest request = new("s", DecisionQuestion.Choice("q", "pick", Options(0)));

            AssertSingle(request, DecisionErrorReasons.TooFewOptions);
        }

        [Test]
        public void Choice_With256Options_IsRefusedAsTooMany()
        {
            DecisionRequest request = new("s", DecisionQuestion.Choice("q", "pick", Options(256)));

            AssertSingle(request, DecisionErrorReasons.TooManyOptions);
        }

        [TestCase(1)]
        [TestCase(10)]
        public void Score_WithinOneToTenLevels_IsValid(int count)
        {
            DecisionRequest request = new("s", DecisionQuestion.Score("q", "rate", Levels(count)));

            Assert.IsEmpty(request.Validate());
        }

        [TestCase(0)]
        [TestCase(11)]
        public void Score_OutsideOneToTenLevels_IsRefused(int count)
        {
            DecisionRequest request = new("s", DecisionQuestion.Score("q", "rate", Levels(count)));

            AssertSingle(request, DecisionErrorReasons.ScoreLevelsOutOfRange);
        }

        [Test]
        public void YesNo_IsAlwaysTwoOptions_YesFirst()
        {
            DecisionQuestion question = DecisionQuestion.YesNo("q", "hostile?");

            Assert.AreEqual(2, question.Options.Count);
            Assert.AreEqual(DecisionQuestion.YesOptionId, question.Options[0].Id);
            Assert.AreEqual(DecisionQuestion.NoOptionId, question.Options[1].Id);
            Assert.IsEmpty(new DecisionRequest("s", question).Validate());
        }

        [Test]
        public void Score_OptionIdsAreZeroBasedLevels()
        {
            DecisionQuestion question = DecisionQuestion.Score("q", "rate", Levels(3));

            Assert.AreEqual(new[] { "0", "1", "2" }, new[] { question.Options[0].Id, question.Options[1].Id, question.Options[2].Id });
        }

        [Test]
        public void NoQuestions_IsRefused()
        {
            AssertSingle(new DecisionRequest("s"), DecisionErrorReasons.NoQuestions);
        }

        [Test]
        public void NullRequest_IsRefused()
        {
            IReadOnlyList<DecisionValidationError> errors = DecisionRequestValidator.Validate(null);

            Assert.AreEqual(DecisionErrorReasons.RequestMissing, errors[0].Code);
        }

        [Test]
        public void DuplicateQuestionIds_AreRefused()
        {
            DecisionRequest request = new("s", DecisionQuestion.YesNo("q", "a"), DecisionQuestion.YesNo("q", "b"));

            AssertSingle(request, DecisionErrorReasons.DuplicateQuestionId);
        }

        [TestCase("")]
        [TestCase("has space")]
        [TestCase("slash/id")]
        public void InvalidQuestionId_IsRefused(string id)
        {
            AssertSingle(new DecisionRequest("s", DecisionQuestion.YesNo(id, "a")), DecisionErrorReasons.InvalidQuestionId);
        }

        [Test]
        public void QuestionId_WithLettersDigitsAndPunctuation_IsValid()
        {
            Assert.IsEmpty(new DecisionRequest("s", DecisionQuestion.YesNo("npc_1.mood-2", "a")).Validate());
        }

        [Test]
        public void DuplicateOptionIds_AreRefused()
        {
            DecisionRequest request = new("s", DecisionQuestion.Choice("q", "pick", "attack", "attack"));

            AssertSingle(request, DecisionErrorReasons.DuplicateOptionId);
        }

        [Test]
        public void EmptyOptionId_IsRefused()
        {
            DecisionRequest request = new("s", DecisionQuestion.Choice("q", "pick", "attack", " "));

            AssertSingle(request, DecisionErrorReasons.InvalidOptionId);
        }

        [Test]
        public void TextAttachment_IsRefusedAsAnImage()
        {
            DecisionRequest request = new("s", new[] { DecisionQuestion.YesNo("q", "a") },
                new[] { AiAttachment.FromText("notes.txt", "hello") });

            AssertSingle(request, DecisionErrorReasons.UnsupportedImage);
        }

        [Test]
        public void InlinePngAttachment_IsAccepted()
        {
            DecisionRequest request = new("s", new[] { DecisionQuestion.YesNo("q", "a") },
                new[] { AiAttachment.Image(new byte[] { 1, 2, 3 }, "image/png") });

            Assert.IsEmpty(request.Validate());
        }

        [Test]
        public void InvalidResult_CarriesTheFirstReasonAsInvalidRequest()
        {
            DecisionRequest request = new("s", DecisionQuestion.Choice("q", "pick", Options(256)));

            DecisionResult result = DecisionResult.Invalid(request.Validate(), "test");

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(LlmErrorCode.InvalidRequest, result.ErrorCode);
            Assert.AreEqual(DecisionErrorReasons.TooManyOptions, result.ErrorReason);
        }

        private static void AssertSingle(DecisionRequest request, string code)
        {
            IReadOnlyList<DecisionValidationError> errors = request.Validate();
            Assert.AreEqual(1, errors.Count, "errors: " + string.Join("; ", Messages(errors)));
            Assert.AreEqual(code, errors[0].Code);
        }

        private static IEnumerable<string> Messages(IReadOnlyList<DecisionValidationError> errors)
        {
            foreach (DecisionValidationError error in errors)
            {
                yield return error.Code + ": " + error.Message;
            }
        }

        internal static DecisionOption[] Options(int count)
        {
            DecisionOption[] options = new DecisionOption[count];
            for (int i = 0; i < count; i++)
            {
                options[i] = new DecisionOption("o" + i);
            }

            return options;
        }

        internal static string[] Levels(int count)
        {
            string[] levels = new string[count];
            for (int i = 0; i < count; i++)
            {
                levels[i] = "level " + i;
            }

            return levels;
        }
    }
}
