using System.Threading;
using CoreAI.Tests.EditMode.RbxApi.RuntimeUi;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Bindables
{
    /// <summary>
    /// Lua-tier conformance of <c>BindableEvent</c> and <c>BindableFunction</c> through the real mod runtime: two
    /// mods exchange tables through one, the arguments arrive as copies by Roblox's bindable rules, <c>Fire</c>
    /// never yields, <c>Invoke</c> yields for the callback's copied results, waits for a callback that is not set
    /// yet, and raises the callback's error. Every rule has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class RbxBindablesLuaEditModeTests
    {
        private SynchronizationContext _savedContext;
        private UxmlServiceTestWorld _world;

        [SetUp]
        public void SetUp()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            _world = UxmlServiceTestWorld.WithFake();
        }

        [TearDown]
        public void TearDown()
        {
            _world?.Dispose();
            _world = null;
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private void Frames(int count)
        {
            for (int i = 0; i < count; i++)
            {
                _world.Frame();
            }
        }

        [Test]
        public void Bindables_TwoMods_ExchangeATableThroughABindableEvent_AsACopy()
        {
            _world.Load("inventory", @"
local bus = Instance.new('BindableEvent')
bus.Name = 'ItemsChanged'
bus.Parent = game:GetService('ReplicatedStorage')
local calls = 0
bus.Event:Connect(function(change, count)
  calls = calls + 1
  workspace:SetAttribute('Handled', true)
  local key, value = next(change.tagged)
  store_set('gold', tostring(change.gold))
  store_set('items', #change.items .. '|' .. change.items[2])
  store_set('meta', tostring(getmetatable(change)))
  store_set('key', typeof(key) .. '|' .. tostring(key) .. '|' .. tostring(value))
  store_set('count', tostring(count))
  store_set('calls', tostring(calls))
end)");
            _world.Load("shop", @"
local bus = game:GetService('ReplicatedStorage'):WaitForChild('ItemsChanged')
local change = setmetatable({ gold = 5, items = { 'sword', 'bow' }, tagged = { [workspace] = true } },
  { __index = function() return 'from the metatable' end })
bus:Fire(change, 2)
store_set('handledBeforeReturn', tostring(workspace:GetAttribute('Handled')))
change.gold = 99
change.items[2] = 'changed after the fire'");
            Frames(3);

            Assert.AreEqual("nil", _world.Get("shop", "handledBeforeReturn"), "Fire does not yield");
            Assert.AreEqual("1", _world.Get("inventory", "calls"));
            Assert.AreEqual("5", _world.Get("inventory", "gold"), "the handler holds a copy made at the fire");
            Assert.AreEqual("2|bow", _world.Get("inventory", "items"));
            Assert.AreEqual("nil", _world.Get("inventory", "meta"), "the copy has no metatable");
            Assert.AreEqual("string|Workspace|true", _world.Get("inventory", "key"),
                "a non-string key arrives as a string");
            Assert.AreEqual("2", _world.Get("inventory", "count"));
        }

        [Test]
        public void Bindables_Negative_AFunctionArrivesAsNil_AndAMixedTableIsRefusedAtTheFire()
        {
            _world.Load("receiver", @"
local bus = Instance.new('BindableEvent')
bus.Name = 'Bus'
bus.Parent = game:GetService('ReplicatedStorage')
bus.Event:Connect(function(value, tail)
  store_set('value', typeof(value) .. '|' .. tostring(tail))
end)");
            _world.Load("sender", @"
local bus = game:GetService('ReplicatedStorage'):WaitForChild('Bus')
bus:Fire(function() end, 'tail')
local ok, err = pcall(function() bus:Fire({ 1, 2, x = 3 }) end)
store_set('mixed', tostring(ok) .. '|' .. tostring(err))");
            Frames(3);

            Assert.AreEqual("nil|tail", _world.Get("receiver", "value"), "a function does not cross");
            StringAssert.StartsWith("false|", _world.Get("sender", "mixed"));
            StringAssert.Contains("mixed numeric and non-numeric table keys", _world.Get("sender", "mixed"));
        }

        [Test]
        public void Bindables_TwoMods_ExchangeTablesThroughABindableFunction_AndTheInvokerYieldsForTheAnswer()
        {
            _world.Load("bank", @"
local ask = Instance.new('BindableFunction')
ask.Name = 'Quote'
ask.Parent = game:GetService('ReplicatedStorage')
ask.OnInvoke = function(request)
  store_set('ranAs', mod_id())
  task.wait()
  return { total = request.amount * 2, who = request.name, echo = request }, 'second'
end
local ok, err = pcall(function() return ask.OnInvoke end)
store_set('read', tostring(ok) .. '|' .. tostring(err))");
            _world.Load("client", @"
local ask = game:GetService('ReplicatedStorage'):WaitForChild('Quote')
local request = { amount = 21, name = 'shop' }
local reply, extra = ask:Invoke(request)
store_set('reply', reply.total .. '|' .. reply.who .. '|' .. tostring(extra))
store_set('copied', tostring(reply.echo ~= request) .. '|' .. reply.echo.name)");
            Frames(6);

            Assert.AreEqual("42|shop|second", _world.Get("client", "reply"));
            Assert.AreEqual("true|shop", _world.Get("client", "copied"), "the answer is a copy, not the sent table");
            Assert.AreEqual("bank", _world.Get("bank", "ranAs"), "the callback runs as the mod that assigned it");
            StringAssert.StartsWith("false|", _world.Get("bank", "read"));
            StringAssert.Contains("OnInvoke is a callback member of BindableFunction",
                _world.Get("bank", "read"));
        }

        [Test]
        public void Bindables_AnInvokeBeforeOnInvokeIsSet_WaitsForTheCallback_AndANilAssignmentKeepsItWaiting()
        {
            _world.Load("late", @"
local f = Instance.new('BindableFunction')
f.Name = 'Late'
f.Parent = game:GetService('ReplicatedStorage')");
            _world.Load("early", @"
local f = game:GetService('ReplicatedStorage'):WaitForChild('Late')
store_set('result', tostring(f:Invoke(3)))");
            Frames(4);
            Assert.IsEmpty(_world.Get("early", "result") ?? "", "no callback yet: the invoker stays suspended");

            _world.Load("clearer", @"
game:GetService('ReplicatedStorage').Late.OnInvoke = nil");
            Frames(3);
            Assert.IsEmpty(_world.Get("early", "result") ?? "", "twin: assigning nil is no callback");

            _world.Load("setter", @"
game:GetService('ReplicatedStorage').Late.OnInvoke = function(n) return n * 10 end");
            Frames(4);
            Assert.AreEqual("30", _world.Get("early", "result"), "the waiting invoke ran once a callback came");
        }

        [Test]
        public void Bindables_TheCallbacksError_IsRaisedInTheInvoker_AndTheNextInvokeStillWorks()
        {
            _world.Load("owner", @"
local f = Instance.new('BindableFunction')
f.Name = 'Strict'
f.Parent = game:GetService('ReplicatedStorage')
f.OnInvoke = function(n)
  if n < 0 then error('negative amounts are refused') end
  return n + 1
end");
            _world.Load("caller", @"
local f = game:GetService('ReplicatedStorage'):WaitForChild('Strict')
local ok, err = pcall(function() return f:Invoke(-1) end)
store_set('bad', tostring(ok) .. '|' .. tostring(err))
store_set('good', tostring(f:Invoke(1)))");
            Frames(6);

            StringAssert.StartsWith("false|", _world.Get("caller", "bad"));
            StringAssert.Contains("negative amounts are refused", _world.Get("caller", "bad"));
            Assert.AreEqual("2", _world.Get("caller", "good"), "twin: a valid call after the error answers");
        }

        [Test]
        public void Bindables_EventAndInvoke_AreNotProperties_ForGetPropertyChangedSignal()
        {
            _world.Load("probe", @"
local e = Instance.new('BindableEvent')
local f = Instance.new('BindableFunction')
local ok1 = pcall(function() return e:GetPropertyChangedSignal('Event') end)
local ok2 = pcall(function() return f:GetPropertyChangedSignal('OnInvoke') end)
local ok3 = pcall(function() return e:GetPropertyChangedSignal('Name') end)
store_set('signals', tostring(ok1) .. '|' .. tostring(ok2) .. '|' .. tostring(ok3))
store_set('isa', tostring(e:IsA('Instance')) .. '|' .. e.ClassName .. '|' .. f.ClassName)");

            Assert.AreEqual("false|false|true", _world.Get("probe", "signals"), "twin: Name is a property");
            Assert.AreEqual("true|BindableEvent|BindableFunction", _world.Get("probe", "isa"));
        }
    }
}
