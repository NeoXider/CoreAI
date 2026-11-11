using System;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Scripting.LuaCs;
using Lua;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.Performance
{
    /// <summary>
    /// P19 allocation budgets on the hot paths that already allocate nothing per operation in steady
    /// state. Each budget has a negative twin: the same body plus one deliberate allocation per operation
    /// must fail the same budget with the measured bytes in its line, so a budget can never pass because the
    /// measurement saw nothing.
    /// </summary>
    /// <remarks>
    /// Paths that do not meet a zero budget today (an idle scheduler frame, a signal fire into a handler, a
    /// guarded call, a coroutine resume, the var-args seam) have no test here; their measured bytes per
    /// operation are listed in dev-docs/PERF_BASELINE_2026-11.md, so a later fix can add the budget it earns.
    /// </remarks>
    [TestFixture]
    public sealed class HotPathAllocationBudgetEditModeTests
    {
        private const int Ops = 20_000;
        private float _sink;


        [Test]
        public void Vector3Arithmetic_AllocatesNothingPerOperation()
        {
            AllocationMeter.AssertWithinBudget("Vector3 add/scale/Dot/Cross/Magnitude", Vector3Arithmetic, Ops, 0);
        }

        [Test]
        public void Vector3Arithmetic_AllocatingTwin_BreaksTheBudget()
        {
            AllocationMeter.AssertTwinBreaksBudget("Vector3 add/scale/Dot/Cross/Magnitude", Vector3Arithmetic, Ops, 0);
        }

        [Test]
        public void CFrameArithmetic_AllocatesNothingPerOperation()
        {
            AllocationMeter.AssertWithinBudget("CFrame multiply/point/Inverse/Lerp", CFrameArithmetic, Ops, 0);
        }

        [Test]
        public void CFrameArithmetic_AllocatingTwin_BreaksTheBudget()
        {
            AllocationMeter.AssertTwinBreaksBudget("CFrame multiply/point/Inverse/Lerp", CFrameArithmetic, Ops, 0);
        }

        /// <summary>
        /// The production sibling that does allocate (the LOW finding of the 2026-07-24 audit): the same
        /// budget rejects it, which is what a mod polling GetComponents every frame would hit.
        /// </summary>
        [Test]
        public void CFrameGetComponents_IsCaughtByTheSameBudget()
        {
            if (!AllocationMeter.IsSupported(out string reason))
            {
                Assert.Ignore(reason);
            }

            RbxCFrame frame = RbxCFrame.FromEulerAnglesXYZ(0.3f, 0.2f, 0.1f);
            long bytes = AllocationMeter.Measure(count =>
            {
                for (int index = 0; index < count; index++)
                {
                    _sink += frame.GetComponents()[3];
                }
            }, Ops, 256);
            TestContext.WriteLine("CFrame.GetComponents: " + bytes + " bytes over " + Ops + " operations");
            Assert.IsNotNull(AllocationMeter.Verdict("CFrame.GetComponents", bytes, Ops, 0),
                "GetComponents returns a fresh float[12]; a zero budget must reject it");
        }


        [Test]
        public void SignalFire_NoConnections_AllocatesNothing()
        {
            RbxScriptSignal signal = new("Perf.Unconnected");
            object[] arguments = { 0.016d };
            AllocationMeter.AssertWithinBudget("RbxScriptSignal.Fire with no connections", count =>
            {
                for (int index = 0; index < count; index++)
                {
                    signal.Fire(arguments);
                }
            }, Ops, 0);
        }

        [Test]
        public void SignalFire_NoConnections_AllocatingTwin_BreaksTheBudget()
        {
            RbxScriptSignal signal = new("Perf.Unconnected");
            object[] arguments = { 0.016d };
            AllocationMeter.AssertTwinBreaksBudget("RbxScriptSignal.Fire with no connections", count =>
            {
                for (int index = 0; index < count; index++)
                {
                    signal.Fire(arguments);
                }
            }, Ops, 0);
        }

        [Test]
        public void RunServiceStep_NoListeners_AllocatesNothing()
        {
            RbxRunService runService = NewRunService();
            AllocationMeter.AssertWithinBudget("RbxRunService.Step with no listener", count =>
            {
                for (int index = 0; index < count; index++)
                {
                    runService.Step(1f / 60f);
                }
            }, Ops, 0);
        }

        [Test]
        public void RunServiceStep_NoListeners_AllocatingTwin_BreaksTheBudget()
        {
            RbxRunService runService = NewRunService();
            AllocationMeter.AssertTwinBreaksBudget("RbxRunService.Step with no listener", count =>
            {
                for (int index = 0; index < count; index++)
                {
                    runService.Step(1f / 60f);
                }
            }, Ops, 0);
        }


        [Test]
        public void ProcessPreSimulation_ModelsAndParts_AllocatesNothing()
        {
            InstanceRegistry registry = NewWorldWithModels(2048, 8);
            AllocationMeter.AssertWithinBudget("InstanceRegistry.ProcessPreSimulation (2,048 Parts, 8 Models)", count =>
            {
                for (int index = 0; index < count; index++)
                {
                    registry.ProcessPreSimulation();
                }
            }, Ops, 0);
        }

        [Test]
        public void ProcessPreSimulation_AllocatingTwin_BreaksTheBudget()
        {
            InstanceRegistry registry = NewWorldWithModels(2048, 8);
            AllocationMeter.AssertTwinBreaksBudget("InstanceRegistry.ProcessPreSimulation (2,048 Parts, 8 Models)", count =>
            {
                for (int index = 0; index < count; index++)
                {
                    registry.ProcessPreSimulation();
                }
            }, Ops, 0);
        }


        [Test]
        public void UnboxHostNumber_AllocatesNothing()
        {
            object hostNumber = 42.5d;
            AllocationMeter.AssertWithinBudget("LuaCsValueMarshaller.Unbox(double)", count =>
            {
                for (int index = 0; index < count; index++)
                {
                    _sink += (float)LuaCsValueMarshaller.Unbox(hostNumber).Read<double>();
                }
            }, Ops, 0);
        }

        [Test]
        public void UnboxHostNumber_AllocatingTwin_BreaksTheBudget()
        {
            object hostNumber = 42.5d;
            AllocationMeter.AssertTwinBreaksBudget("LuaCsValueMarshaller.Unbox(double)", count =>
            {
                for (int index = 0; index < count; index++)
                {
                    _sink += (float)LuaCsValueMarshaller.Unbox(hostNumber).Read<double>();
                }
            }, Ops, 0);
        }

        /// <summary>The opposite direction boxes by design (MEDIUM finding): the same budget rejects it.</summary>
        [Test]
        public void BoxLuaNumber_IsCaughtByTheSameBudget()
        {
            if (!AllocationMeter.IsSupported(out string reason))
            {
                Assert.Ignore(reason);
            }

            LuaValue number = new(42.5d);
            long bytes = AllocationMeter.Measure(count =>
            {
                for (int index = 0; index < count; index++)
                {
                    GC.KeepAlive(LuaCsValueMarshaller.Box(number));
                }
            }, Ops, 256);
            TestContext.WriteLine("LuaCsValueMarshaller.Box(number): " + bytes + " bytes over " + Ops + " operations");
            Assert.IsNotNull(AllocationMeter.Verdict("LuaCsValueMarshaller.Box", bytes, Ops, 0),
                "Box stores a LuaValue struct in an object; a zero budget must reject it");
        }


        [Test]
        public void Verdict_WithinBudget_IsNull_AndOverBudget_NamesTheMeasuredBytes()
        {
            Assert.IsNull(AllocationMeter.Verdict("subject", 0, 10, 0));
            Assert.IsNull(AllocationMeter.Verdict("subject", 40, 10, 4));
            string verdict = AllocationMeter.Verdict("subject", 41, 10, 4);
            Assert.IsNotNull(verdict);
            StringAssert.Contains("41 bytes over 10 operations", verdict);
            StringAssert.Contains("4.1 B/op", verdict);
        }

        private void Vector3Arithmetic(int count)
        {
            RbxVector3 accumulator = new(0f, 0f, 0f);
            RbxVector3 step = new(0.5f, 1.25f, -2f);
            for (int index = 0; index < count; index++)
            {
                accumulator = (accumulator + step) * 0.999f - step * 0.5f;
                _sink += accumulator.Dot(step) + accumulator.Cross(step).Magnitude;
            }
        }

        private void CFrameArithmetic(int count)
        {
            RbxCFrame frame = RbxCFrame.Identity;
            RbxCFrame step = RbxCFrame.FromEulerAnglesXYZ(0.01f, 0.02f, 0.03f) + new RbxVector3(1f, 0f, 0f);
            RbxVector3 point = new(1f, 2f, 3f);
            for (int index = 0; index < count; index++)
            {
                frame = (frame * step).Lerp(step, 0.5f);
                point = frame.Inverse() * point * 0.5f;
                _sink += point.X + frame.LookVector.Y;
            }
        }

        private static RbxRunService NewRunService()
        {
            InstanceRegistry registry = new(binder: new InMemoryInstanceBackingBinder());
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            RbxRunService runService = game.GetService("RunService") as RbxRunService;
            Assert.IsNotNull(runService, "the bootstrapped DataModel must expose RunService");
            return runService;
        }

        private static InstanceRegistry NewWorldWithModels(int parts, int models)
        {
            InstanceRegistry registry = new(binder: new InMemoryInstanceBackingBinder());
            DataModelBootstrap.CreateGame(registry);
            RbxInstance[] modelInstances = new RbxInstance[models];
            for (int index = 0; index < models; index++)
            {
                modelInstances[index] = registry.Create("Model");
                modelInstances[index].Parent = registry.WorldRoot;
            }

            for (int index = 0; index < parts; index++)
            {
                RbxInstance part = registry.Create("Part");
                part.Parent = index < models ? modelInstances[index] : registry.WorldRoot;
            }

            return registry;
        }
    }
}
