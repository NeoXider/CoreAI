using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for the pluggable content-safety contract:
    /// <see cref="IContentFilter"/>, <see cref="PassthroughContentFilter"/>, and the
    /// <see cref="WordlistContentFilter"/> baseline.
    /// </summary>
    public sealed class ContentFilterEditModeTests
    {
        private static ContentFilterContext InputContext =>
            new(ContentFilterDirection.UserInput, "SmartChat");

        private static ContentFilterContext OutputContext =>
            new(ContentFilterDirection.ModelOutput, "SmartChat");

        [Test]
        public void Passthrough_AllowsEverything_BothDirections()
        {
            IContentFilter filter = PassthroughContentFilter.Instance;

            ContentFilterVerdict input = filter.Evaluate("anything at all", InputContext);
            ContentFilterVerdict output = filter.Evaluate("anything at all", OutputContext);
            ContentFilterVerdict empty = filter.Evaluate(null, InputContext);

            Assert.AreEqual(ContentFilterAction.Allow, input.Action);
            Assert.AreEqual(ContentFilterAction.Allow, output.Action);
            Assert.AreEqual(ContentFilterAction.Allow, empty.Action);
            Assert.IsNull(input.RedactedText);
        }

        [Test]
        public void Redact_ReplacesTermWithSameLengthAsterisks_PreservingSurroundingText()
        {
            WordlistContentFilter filter = new(new[] { "grog" }, ContentFilterMode.RedactTerms);

            ContentFilterVerdict verdict = filter.Evaluate("pour the grog now, grog later", OutputContext);

            Assert.AreEqual(ContentFilterAction.Redact, verdict.Action);
            Assert.AreEqual("pour the **** now, **** later", verdict.RedactedText);
            Assert.AreEqual("pour the grog now, grog later".Length, verdict.RedactedText.Length);
        }

        [Test]
        public void Block_TriggersWhenAnyTermMatches()
        {
            WordlistContentFilter filter = new(
                new[] { "alpha", "bravo", "charlie" }, ContentFilterMode.BlockMessage);

            ContentFilterVerdict blocked = filter.Evaluate("some bravo in the middle", InputContext);
            ContentFilterVerdict allowed = filter.Evaluate("nothing suspicious here", InputContext);

            Assert.AreEqual(ContentFilterAction.Block, blocked.Action);
            Assert.IsNull(blocked.RedactedText);
            Assert.IsNotNull(blocked.Reason);
            Assert.AreEqual(ContentFilterAction.Allow, allowed.Action);
        }

        [Test]
        public void Matching_IsCaseInsensitive()
        {
            WordlistContentFilter redacting = new(new[] { "grog" }, ContentFilterMode.RedactTerms);
            WordlistContentFilter blocking = new(new[] { "grog" }, ContentFilterMode.BlockMessage);

            ContentFilterVerdict redacted = redacting.Evaluate("GROG and GrOg", OutputContext);
            ContentFilterVerdict blocked = blocking.Evaluate("Some GROG here", InputContext);

            Assert.AreEqual(ContentFilterAction.Redact, redacted.Action);
            Assert.AreEqual("**** and ****", redacted.RedactedText);
            Assert.AreEqual(ContentFilterAction.Block, blocked.Action);
        }

        [Test]
        public void WholeWord_TermInsideLargerWord_DoesNotMatch()
        {
            WordlistContentFilter filter = new(new[] { "rog" }, ContentFilterMode.BlockMessage);

            ContentFilterVerdict verdict = filter.Evaluate("the program runs grogward", InputContext);

            Assert.AreEqual(ContentFilterAction.Allow, verdict.Action);
        }

        [Test]
        public void EmptyOrNullWordlist_BehavesAsPassthrough()
        {
            WordlistContentFilter empty = new(new string[0], ContentFilterMode.BlockMessage);
            WordlistContentFilter nullList = new(null, ContentFilterMode.RedactTerms);
            WordlistContentFilter blanks = new(new[] { null, "", "   " }, ContentFilterMode.BlockMessage);

            Assert.AreEqual(ContentFilterAction.Allow, empty.Evaluate("anything", InputContext).Action);
            Assert.AreEqual(ContentFilterAction.Allow, nullList.Evaluate("anything", OutputContext).Action);
            Assert.AreEqual(ContentFilterAction.Allow, blanks.Evaluate("anything", InputContext).Action);
        }

        [Test]
        public void UnicodeCyrillic_TextIsMatchedAndRedactedSafely()
        {
            // Cyrillic sample: term and casing variants must fold correctly under ordinal rules,
            // and surrounding Cyrillic letters must count as word characters (no partial matches).
            WordlistContentFilter filter = new(new[] { "грог" }, // "грог"
                ContentFilterMode.RedactTerms);

            // "Налей ГРОГ сюда" — uppercase variant inside a Cyrillic sentence.
            ContentFilterVerdict redacted = filter.Evaluate(
                "Налей ГРОГ сюда",
                OutputContext);
            // "грогатон" — term embedded in a longer Cyrillic word must not match.
            ContentFilterVerdict embedded = filter.Evaluate(
                "грогатон", InputContext);

            Assert.AreEqual(ContentFilterAction.Redact, redacted.Action);
            Assert.AreEqual(
                "Налей **** сюда",
                redacted.RedactedText);
            Assert.AreEqual(ContentFilterAction.Allow, embedded.Action);
        }

        [Test]
        public void AllowPath_ReturnsNoRedactedTextAndNoReason()
        {
            WordlistContentFilter filter = new(new[] { "grog" }, ContentFilterMode.RedactTerms);

            ContentFilterVerdict verdict = filter.Evaluate("perfectly clean sentence", InputContext);

            Assert.AreEqual(ContentFilterAction.Allow, verdict.Action);
            Assert.IsNull(verdict.RedactedText);
            Assert.IsNull(verdict.Reason);
            // Allow is the struct default, so the hot path needs no factory call at all.
            Assert.AreEqual(ContentFilterAction.Allow, default(ContentFilterVerdict).Action);
        }

        [Test]
        public void Context_NormalizesNullRoleIdAndCarriesDirection()
        {
            ContentFilterContext context = new(ContentFilterDirection.ModelOutput, null);

            Assert.AreEqual("", context.RoleId);
            Assert.AreEqual(ContentFilterDirection.ModelOutput, context.Direction);
        }
    }
}
