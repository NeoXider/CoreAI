namespace CoreAI.Infrastructure.Luau
{
    /// <summary>
    /// What the rewrite parser reports while it walks a chunk, for readers that need the program's structure
    /// rather than its rewrite (the export lint). Every token is an author token, so positions are the author's;
    /// nothing the downlevel inserts is reported. A parser with no observer reports nothing and costs nothing.
    /// </summary>
    internal interface ILuauSyntaxObserver
    {
        /// <summary>A block, function or loop scope opens.</summary>
        void OpenScope();

        /// <summary>The innermost open scope closes.</summary>
        void CloseScope();

        /// <summary>A local name enters the innermost scope: a <c>local</c>, a parameter or a loop variable.</summary>
        void Declare(LuauToken name);

        /// <summary>A local name the language declares without a token: <c>self</c> in a method.</summary>
        void DeclareImplicit(string name);

        /// <summary>A name read, called or assigned as the start of an expression.</summary>
        void UseName(LuauToken name);

        /// <summary>A name assigned as a whole (<c>x = ...</c>, <c>function x() end</c>): the script defines it.</summary>
        void AssignName(LuauToken name);

        /// <summary>The first member read off a name: <c>name.member</c> or <c>name:member(...)</c>.</summary>
        void UseMember(LuauToken baseName, LuauToken member, bool method);

        /// <summary>
        /// A method call anywhere in a suffix chain, <c>x:method(...)</c>, with its first argument when that is a
        /// string literal (<c>game:GetService("Players")</c>), else null.
        /// </summary>
        void CallMethod(LuauToken method, LuauToken firstStringArgument);

        /// <summary>A Lua 5.2 <c>goto name</c> statement.</summary>
        void Goto(LuauToken keyword, LuauToken label);

        /// <summary>A Lua 5.2 <c>::name::</c> label.</summary>
        void Label(LuauToken open, LuauToken label);
    }
}
