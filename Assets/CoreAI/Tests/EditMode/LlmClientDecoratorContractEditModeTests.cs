#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode
{
    /// <summary>
    /// Guards the <see cref="ILlmClient"/> decorator contract: every member of the interface that has a
    /// virtual DEFAULT INTERFACE implementation must be re-declared (and delegated) by every decorator.
    /// A decorator that inherits the default body compiles cleanly but silently answers for the inner
    /// client it wraps - dropping routing capability, context windows, or tool registration.
    /// </summary>
    public sealed class LlmClientDecoratorContractEditModeTests
    {
        [Test]
        public void EveryLlmClientDecorator_DeclaresEveryVirtualInterfaceMember()
        {
            Type contract = typeof(ILlmClient);
            List<MethodInfo> virtualMembers = contract
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => !m.IsAbstract)
                .ToList();

            Assert.IsNotEmpty(virtualMembers, "ILlmClient declares no virtual members - test target is stale.");

            List<Type> decorators = contract.Assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && contract.IsAssignableFrom(t))
                .Where(t => t.GetConstructors()
                    .Any(c => c.GetParameters().Any(p => p.ParameterType == contract)))
                .OrderBy(t => t.FullName, StringComparer.Ordinal)
                .ToList();

            Assert.GreaterOrEqual(
                decorators.Count, 5,
                "Decorator discovery found too few ILlmClient wrappers - the detection heuristic is stale.");

            List<string> gaps = new();
            foreach (Type decorator in decorators)
            {
                foreach (MethodInfo member in virtualMembers)
                {
                    if (!DeclaresOwnImplementation(decorator, member))
                    {
                        gaps.Add($"{decorator.FullName} does not override {Describe(member)}");
                    }
                }
            }

            Assert.IsEmpty(gaps, string.Join(Environment.NewLine, gaps));
        }

        private static bool DeclaresOwnImplementation(Type decorator, MethodInfo member)
        {
            Type[] signature = member.GetParameters().Select(p => p.ParameterType).ToArray();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Instance | BindingFlags.DeclaredOnly;

            if (decorator.GetMethod(member.Name, flags, null, signature, null) != null)
            {
                return true;
            }

            string explicitName = $"{member.DeclaringType?.FullName}.{member.Name}";
            return decorator.GetMethod(explicitName, flags, null, signature, null) != null;
        }

        private static string Describe(MethodInfo member)
        {
            return $"{member.Name}({string.Join(", ", member.GetParameters().Select(p => p.ParameterType.Name))})";
        }
    }
}
#endif
