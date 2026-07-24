using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Instances
{
    /// <summary>
    /// Dispatch-enabled <see cref="RbxScriptSignal"/> semantics that the reused fire-snapshot must
    /// preserve: connection order, snapshot isolation (Connect/Disconnect during a fire), Once,
    /// and the two-argument fire path used by the input signals.
    /// </summary>
    [TestFixture]
    public sealed class RbxScriptSignalEditModeTests
    {
        private static RbxScriptSignal DispatchSignal()
        {
            return new RbxScriptSignal("Test.Signal", supportsDispatch: true);
        }

        [Test]
        public void TwoArgFire_DeliversBothArguments()
        {
            RbxScriptSignal signal = DispatchSignal();
            object[] received = null;
            signal.Connect((Action<object[]>)(args => received = args));

            signal.Fire("input", false);

            Assert.IsNotNull(received);
            Assert.AreEqual(2, received.Length);
            Assert.AreEqual("input", received[0]);
            Assert.AreEqual(false, received[1]);
        }

        [Test]
        public void Fire_InvokesConnectionsInOrder()
        {
            RbxScriptSignal signal = DispatchSignal();
            var order = new List<int>();
            signal.Connect((Action<object[]>)(_ => order.Add(1)));
            signal.Connect((Action<object[]>)(_ => order.Add(2)));
            signal.Connect((Action<object[]>)(_ => order.Add(3)));

            signal.Fire(null, null);

            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, order);
        }

        [Test]
        public void ConnectDuringFire_IsNotInvokedUntilTheNextFire()
        {
            RbxScriptSignal signal = DispatchSignal();
            int lateCalls = 0;
            signal.Connect((Action<object[]>)(_ =>
                signal.Connect((Action<object[]>)(__ => lateCalls++))));

            // WHY: the fire iterates a snapshot, so a connection added mid-fire is deferred.
            signal.Fire(null, null);
            Assert.AreEqual(0, lateCalls);

            signal.Fire(null, null);
            Assert.AreEqual(1, lateCalls);
        }

        [Test]
        public void DisconnectDuringFire_SkipsTheDisconnectedHandlerSameFire()
        {
            RbxScriptSignal signal = DispatchSignal();
            RbxScriptConnection second = null;
            int secondCalls = 0;
            signal.Connect((Action<object[]>)(_ => second.Disconnect()));
            second = signal.Connect((Action<object[]>)(_ => secondCalls++));

            signal.Fire(null, null);

            Assert.AreEqual(0, secondCalls, "a handler disconnected earlier in the same fire is skipped");
            Assert.IsFalse(second.Connected);
        }

        [Test]
        public void Once_FiresExactlyOnce()
        {
            RbxScriptSignal signal = DispatchSignal();
            int calls = 0;
            signal.Once((Action<object[]>)(_ => calls++));

            signal.Fire(null, null);
            signal.Fire(null, null);

            Assert.AreEqual(1, calls);
        }

        [Test]
        public void ParamsFire_StillDispatches()
        {
            RbxScriptSignal signal = DispatchSignal();
            int received = -1;
            signal.Connect((Action<object[]>)(args => received = args.Length));

            signal.Fire(new object[] { 1, 2, 3 });

            Assert.AreEqual(3, received);
        }
    }
}
