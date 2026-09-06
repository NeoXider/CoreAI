using System;

namespace CoreAI.Mods.Rbx.Datatypes
{
    /// <summary>
    /// Announces every loud-stub raise so a harness can count them at the throw site.
    /// </summary>
    /// <remarks>
    /// WHY this exists at all: the compatibility corpus has to assert that no fixture leans on an
    /// unimplemented member — gate P8.5's "the harness asserts zero stub hits". A stub is a plain
    /// <c>throw</c>: nothing logs, so a fixture that wraps the call in <c>pcall</c> swallows the
    /// error, produces no diagnostic, and reads as a clean pass. The corpus harness used to scrape
    /// logger text for "NOT_IMPLEMENTED" and therefore proved nothing.
    /// <para>
    /// WHY not <c>AppDomain.FirstChanceException</c>, which is the framework's answer to exactly
    /// this: Unity's Mono runtime exposes the event but never raises it, so a counter built on it
    /// silently reports zero. Measured, not assumed — that build is what made this file necessary.
    /// </para>
    /// <para>
    /// Cost in production is one null check on a path that is already constructing an exception.
    /// </para>
    /// </remarks>
    public static class RbxStubRaiseObserver
    {
        /// <summary>Raised with the wire code (e.g. <c>NOT_IMPLEMENTED</c>) as a stub error is built.</summary>
        public static event Action<string> Raised;

        /// <summary>Announces a raise. Called from the error constructors, never by callers.</summary>
        public static void Note(string code)
        {
            Raised?.Invoke(code);
        }
    }
}
