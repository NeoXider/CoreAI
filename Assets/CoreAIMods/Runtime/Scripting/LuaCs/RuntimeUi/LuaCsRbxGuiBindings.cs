using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Adornments;
using CoreAI.Mods.Rbx.Instances.RuntimeUi;
using CoreAI.Mods.Rbx.Instances.TextChat;
using Lua;
using static CoreAI.Ai.LuaCs.LuaCsRbxLua;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// The Lua members of the Roblox GUI classes (MVP15, plan decision D14): <c>ScreenGui</c>, <c>Frame</c>,
    /// <c>TextLabel</c>, <c>TextButton</c>, <c>TextBox</c>, <c>ImageLabel</c>, <c>ImageButton</c>,
    /// <c>ScrollingFrame</c>, the <c>UI*</c> modifiers, <c>StarterGui</c> and <c>PlayerGui</c>. This layer only
    /// marshals values through one data-driven member table; the property rules live in the engine-free GUI
    /// classes and the drawing in <see cref="RbxGuiPresenter"/>. Creating a GUI object needs the <c>Ui</c>
    /// capability, like a <c>UxmlService</c> screen. The same table answers the small classes outside the classic
    /// dispatch: <c>TextChatService</c>, <c>TextChatCommand</c> and <c>TextSource</c>.
    /// </summary>
    internal static class LuaCsRbxGuiBindings
    {
        private enum Kind
        {
            Bool,
            Int,
            Number,
            String,
            Color3,
            UDim,
            UDim2,
            Vector2,
            Enum,
            Instance
        }

        private sealed class Property
        {
            public Property(string declaringClass, string name, Kind kind, string enumName, bool readOnly)
            {
                DeclaringClass = declaringClass;
                Name = name;
                Kind = kind;
                EnumName = enumName;
                ReadOnly = readOnly;
            }

            public string DeclaringClass { get; }

            public string Name { get; }

            public Kind Kind { get; }

            public string EnumName { get; }

            public bool ReadOnly { get; }

            public Func<RbxInstance, bool> GetBool;
            public Action<RbxInstance, bool> SetBool;
            public Func<RbxInstance, int> GetInt;
            public Action<RbxInstance, int> SetInt;
            public Func<RbxInstance, double> GetNumber;
            public Action<RbxInstance, double> SetNumber;
            public Func<RbxInstance, string> GetString;
            public Action<RbxInstance, string> SetString;
            public Func<RbxInstance, RbxColor3> GetColor;
            public Action<RbxInstance, RbxColor3> SetColor;
            public Func<RbxInstance, RbxUDim> GetUDim;
            public Action<RbxInstance, RbxUDim> SetUDim;
            public Func<RbxInstance, RbxUDim2> GetUDim2;
            public Action<RbxInstance, RbxUDim2> SetUDim2;
            public Func<RbxInstance, RbxVector2> GetVector2;
            public Action<RbxInstance, RbxVector2> SetVector2;
            public Func<RbxInstance, RbxInstance> GetInstance;
            public Action<RbxInstance, RbxInstance> SetInstance;
        }

        private static readonly Dictionary<string, Dictionary<string, Property>> Declared =
            new(StringComparer.Ordinal);

        private static readonly Dictionary<string, Dictionary<string, Property>> Flattened =
            new(StringComparer.Ordinal);

        /// <summary>The events the GUI classes answer, by declaring class.</summary>
        private static readonly (string ClassName, string Event)[] Events =
        {
            ("GuiObject", "MouseEnter"), ("GuiObject", "MouseLeave"), ("GuiButton", "MouseButton1Click"),
            ("GuiButton", "Activated"), ("TextBox", "Focused"), ("TextBox", "FocusLost"),
            ("TextChatCommand", "Triggered")
        };

        /// <summary>The methods the GUI classes answer, by declaring class.</summary>
        private static readonly (string ClassName, string Method)[] Methods =
        {
            ("TextBox", "CaptureFocus"), ("TextBox", "ReleaseFocus"), ("TextBox", "IsFocused"),
            ("StarterGui", "SetCoreGuiEnabled"), ("StarterGui", "GetCoreGuiEnabled")
        };

        private static readonly LuaFunction CaptureFocusFn = Fn("TextBox:CaptureFocus", ctx =>
        {
            RequireTextBox(ctx, "CaptureFocus").CaptureFocus();
            return LuaValue.Nil;
        });

        private static readonly LuaFunction ReleaseFocusFn = Fn("TextBox:ReleaseFocus", ctx =>
        {
            RbxTextBox box = RequireTextBox(ctx, "ReleaseFocus");
            LuaValue submitted = Arg(ctx, 1);
            box.ReleaseFocus(submitted.Type == LuaValueType.Boolean && submitted.Read<bool>());
            return LuaValue.Nil;
        });

        private static readonly LuaFunction IsFocusedFn = Fn("TextBox:IsFocused",
            ctx => RequireTextBox(ctx, "IsFocused").IsFocused);

        private static readonly LuaFunction SetCoreGuiEnabledFn = Fn("StarterGui:SetCoreGuiEnabled", ctx =>
        {
            RbxStarterGui starterGui = RequireStarterGui(ctx, "SetCoreGuiEnabled", out LuaCsRbxModContext context);
            RbxEnumItem type = ReadEnumArgument(ctx, 1, context.Bindings.Enums, "CoreGuiType",
                "StarterGui:SetCoreGuiEnabled", 1);
            LuaValue enabled = Arg(ctx, 2);
            if (enabled.Type != LuaValueType.Boolean)
            {
                throw ExpectedArgument("StarterGui:SetCoreGuiEnabled", "a boolean", enabled, 2);
            }

            starterGui.SetCoreGuiEnabled(type.Value, enabled.Read<bool>());
            return LuaValue.Nil;
        });

        private static readonly LuaFunction GetCoreGuiEnabledFn = Fn("StarterGui:GetCoreGuiEnabled", ctx =>
        {
            RbxStarterGui starterGui = RequireStarterGui(ctx, "GetCoreGuiEnabled", out LuaCsRbxModContext context);
            RbxEnumItem type = ReadEnumArgument(ctx, 1, context.Bindings.Enums, "CoreGuiType",
                "StarterGui:GetCoreGuiEnabled", 1);
            return starterGui.GetCoreGuiEnabled(type.Value);
        });

        static LuaCsRbxGuiBindings()
        {
            DeclareBase();
            DeclareObjects();
            DeclareText("TextLabel");
            DeclareText("TextButton");
            DeclareText("TextBox");
            DeclareImage("ImageLabel");
            DeclareImage("ImageButton");
            DeclareScrolling();
            DeclareComponents();
            DeclareContainers();
            DeclareTextChat();
            DeclareAdornments();
        }

        /// <summary>True for the instances whose members this table answers.</summary>
        public static bool IsGuiInstance(RbxInstance instance)
        {
            return instance is RbxGuiBase2d || instance is RbxUiBase || instance is RbxBasePlayerGui
                   || instance is RbxTextChatCommand || instance is RbxTextChatService || instance is RbxTextSource
                   || instance is RbxGuiBase3d;
        }

        /// <summary>
        /// The <c>Instance.__index</c> hook: answers the GUI members of a GUI instance (properties, events and
        /// methods) and declines everything else, which the instance dispatch then resolves as before.
        /// </summary>
        public static bool TryRead(LuaCsRbxModContext context, RbxInstance self, string key, out LuaValue value)
        {
            value = LuaValue.Nil;
            if (!IsGuiInstance(self))
            {
                return false;
            }

            if (TryFind(context.Bindings.Registry.Catalog, self.ClassName, key, out Property property))
            {
                value = Read(context, self, property);
                return true;
            }

            switch (key)
            {
                case "MouseEnter" when self is RbxGuiObject entered:
                    entered.NotePointerInterest();
                    value = LuaCsRbxDatatypeBindings.Wrap(entered.MouseEnter, context);
                    return true;
                case "MouseLeave" when self is RbxGuiObject left:
                    left.NotePointerInterest();
                    value = LuaCsRbxDatatypeBindings.Wrap(left.MouseLeave, context);
                    return true;
                case "MouseButton1Click" when self is RbxGuiButton clicked:
                    value = LuaCsRbxDatatypeBindings.Wrap(clicked.MouseButton1Click, context);
                    return true;
                case "Activated" when self is RbxGuiButton activated:
                    value = LuaCsRbxDatatypeBindings.Wrap(activated.Activated, context);
                    return true;
                case "Focused" when self is RbxTextBox focused:
                    value = LuaCsRbxDatatypeBindings.Wrap(focused.Focused, context);
                    return true;
                case "FocusLost" when self is RbxTextBox lost:
                    value = LuaCsRbxDatatypeBindings.Wrap(lost.FocusLost, context);
                    return true;
                case "Triggered" when self is RbxTextChatCommand command:
                    value = LuaCsRbxDatatypeBindings.Wrap(command.Triggered, context);
                    return true;
                case "CaptureFocus" when self is RbxTextBox:
                    value = CaptureFocusFn;
                    return true;
                case "ReleaseFocus" when self is RbxTextBox:
                    value = ReleaseFocusFn;
                    return true;
                case "IsFocused" when self is RbxTextBox:
                    value = IsFocusedFn;
                    return true;
                case "SetCoreGuiEnabled" when self is RbxStarterGui:
                    value = SetCoreGuiEnabledFn;
                    return true;
                case "GetCoreGuiEnabled" when self is RbxStarterGui:
                    value = GetCoreGuiEnabledFn;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The <c>Instance.__newindex</c> hook: writes a GUI property through its engine-free setter (WorldEdit
        /// and the world ACL apply as for every instance property); a read-only one raises Roblox's error.
        /// </summary>
        public static bool TryWrite(LuaCsRbxModContext context, RbxInstance self, string key, LuaValue value)
        {
            if (!IsGuiInstance(self)
                || !TryFind(context.Bindings.Registry.Catalog, self.ClassName, key, out Property property))
            {
                return false;
            }

            if (property.ReadOnly)
            {
                throw RbxError.BadArgument(
                    "Unable to assign property " + key + ". Property is read only",
                    "read " + self.ClassName + "." + key + " instead; "
                    + (self is RbxGuiBase2d ? "the layout sets it" : "scripts cannot set it"));
            }

            context.RequireWorldEditForWrite(self, key);
            Write(context, self, property, value);
            context.RecordMutation(self);
            return true;
        }

        /// <summary>Refuses <c>Instance.new</c> of a GUI class to a script without the <c>Ui</c> capability.</summary>
        public static void RequireUiForCreate(LuaCsRbxModContext context, string className)
        {
            ClassCatalog catalog = context.Bindings.Registry.Catalog;
            if (className == null || !catalog.TryGet(className, out _))
            {
                return;
            }

            // WHY GuiBase2d and not GuiBase: a 3D adornment (SelectionBox) is drawn in the world, not on the UI layer.
            if ((catalog.IsA(className, "GuiBase2d") || catalog.IsA(className, "UIBase"))
                && (context.Capabilities & LuaCapabilities.Ui) == 0)
            {
                throw RbxUiErrors.MissingCapability("Instance.new(\"" + className + "\")");
            }
        }

        /// <summary>
        /// Refuses a Parent write that would show more ScreenGuis or GUI objects than the owning mod's budget
        /// holds, before the move happens, so the error names the line.
        /// </summary>
        public static void RequireRoomForParent(LuaCsRbxModContext context, RbxInstance self, RbxInstance destination)
        {
            if (!(self is RbxGuiBase2d) || destination == null)
            {
                return;
            }

            RbxGuiPresenter presenter = FindPresenter(context);
            presenter?.RequireRoomForMove(self, destination);
        }

        /// <summary>The tween sample of a GUI property: numbers, Color3 and UDim2 interpolate; other types do not.</summary>
        public static bool TrySampleTween(ClassCatalog catalog, RbxInstance target, string name,
            out TweenPropertySample sample)
        {
            sample = default;
            if (!IsGuiInstance(target) || !TryFind(catalog, target.ClassName, name, out Property property))
            {
                return false;
            }

            if (property.ReadOnly)
            {
                sample = TweenPropertySample.Unsupported(TypeName(property.Kind));
                return true;
            }

            switch (property.Kind)
            {
                case Kind.Number:
                    sample = TweenPropertySample.SupportedValue(property.GetNumber(target), "number");
                    return true;
                case Kind.Color3:
                    sample = TweenPropertySample.SupportedValue(property.GetColor(target), "Color3");
                    return true;
                case Kind.UDim2:
                    sample = TweenPropertySample.SupportedValue(property.GetUDim2(target), "UDim2");
                    return true;
                default:
                    sample = TweenPropertySample.Unsupported(TypeName(property.Kind));
                    return true;
            }
        }

        /// <summary>Writes an interpolated tween value through the property's setter; true when it was a GUI property.</summary>
        public static bool TryWriteTween(InstanceRegistry registry, RbxInstance target, string name, object value)
        {
            if (!IsGuiInstance(target) || !TryFind(registry.Catalog, target.ClassName, name, out Property property)
                                       || property.ReadOnly)
            {
                return false;
            }

            switch (property.Kind)
            {
                case Kind.Number when value is double number:
                    if (double.IsNaN(number) || double.IsInfinity(number))
                    {
                        return true;
                    }

                    double before = property.GetNumber(target);
                    property.SetNumber(target, number);
                    if (!before.Equals(property.GetNumber(target)))
                    {
                        registry.AdvanceRevision(target.Id);
                    }

                    return true;
                case Kind.Color3 when value is RbxColor3 color:
                    RbxColor3 colorBefore = property.GetColor(target);
                    property.SetColor(target, color);
                    if (colorBefore != property.GetColor(target))
                    {
                        registry.AdvanceRevision(target.Id);
                    }

                    return true;
                case Kind.UDim2 when value is RbxUDim2 udim:
                    RbxUDim2 udimBefore = property.GetUDim2(target);
                    property.SetUDim2(target, udim);
                    if (udimBefore != property.GetUDim2(target))
                    {
                        registry.AdvanceRevision(target.Id);
                    }

                    return true;
                default:
                    return true;
            }
        }

        /// <summary>True when <paramref name="name"/> is a GUI property of the instance's class (GetPropertyChangedSignal accepts it).</summary>
        public static bool IsProperty(ClassCatalog catalog, RbxInstance instance, string name)
        {
            return IsGuiInstance(instance) && TryFind(catalog, instance.ClassName, name, out _);
        }

        /// <summary>True when <paramref name="name"/> is a GUI event or method of the instance's class (never a property).</summary>
        public static bool IsEventOrMethod(ClassCatalog catalog, RbxInstance instance, string name)
        {
            if (!IsGuiInstance(instance) || name == null)
            {
                return false;
            }

            for (int i = 0; i < Events.Length; i++)
            {
                if (Events[i].Event == name && catalog.IsA(instance.ClassName, Events[i].ClassName))
                {
                    return true;
                }
            }

            for (int i = 0; i < Methods.Length; i++)
            {
                if (Methods[i].Method == name && catalog.IsA(instance.ClassName, Methods[i].ClassName))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The GUI property of the instance's class that <paramref name="typed"/> is one edit away from, or null.</summary>
        public static string FindNearMissProperty(ClassCatalog catalog, RbxInstance instance, string typed)
        {
            if (!IsGuiInstance(instance) || typed == null)
            {
                return null;
            }

            TryFind(catalog, instance.ClassName, typed, out _);
            if (!Flattened.TryGetValue(instance.ClassName, out Dictionary<string, Property> members))
            {
                return null;
            }

            foreach (string name in members.Keys)
            {
                if (LuaCsRbxInstanceBindings.IsOneEditAway(typed, name))
                {
                    return name;
                }
            }

            return null;
        }

        /// <summary>Every (declaring class, property) pair this table answers, for the bound-property list.</summary>
        public static IEnumerable<(string ClassName, string Property)> EnumerateProperties()
        {
            foreach (KeyValuePair<string, Dictionary<string, Property>> declared in Declared)
            {
                foreach (string name in declared.Value.Keys)
                {
                    yield return (declared.Key, name);
                }
            }
        }

        /// <summary>The event and method names this table answers (never properties).</summary>
        public static IEnumerable<string> EnumerateNonProperties()
        {
            for (int i = 0; i < Events.Length; i++)
            {
                yield return Events[i].Event;
            }

            for (int i = 0; i < Methods.Length; i++)
            {
                yield return Methods[i].Method;
            }
        }

        /// <summary>The (declaring class, member) pairs of the events and methods (the catalog drift guard reads them).</summary>
        public static IEnumerable<(string ClassName, string Member)> EnumerateEventsAndMethods()
        {
            for (int i = 0; i < Events.Length; i++)
            {
                yield return Events[i];
            }

            for (int i = 0; i < Methods.Length; i++)
            {
                yield return Methods[i];
            }
        }

        // ---- reads and writes --------------------------------------------------------------------

        private static LuaValue Read(LuaCsRbxModContext context, RbxInstance self, Property property)
        {
            switch (property.Kind)
            {
                case Kind.Bool:
                    return property.GetBool(self);
                case Kind.Int:
                    return property.GetInt(self);
                case Kind.Number:
                    return property.GetNumber(self);
                case Kind.String:
                    return property.GetString(self) ?? string.Empty;
                case Kind.Color3:
                    return LuaCsRbxDatatypeBindings.Wrap(property.GetColor(self));
                case Kind.UDim:
                    return LuaCsRbxDatatypeBindings.Wrap(property.GetUDim(self));
                case Kind.UDim2:
                    return LuaCsRbxDatatypeBindings.Wrap(property.GetUDim2(self));
                case Kind.Vector2:
                    return LuaCsRbxDatatypeBindings.Wrap(property.GetVector2(self));
                case Kind.Instance:
                    return context.WrapInstance(property.GetInstance(self));
                case Kind.Enum:
                    RbxEnumRegistry enums = context.Bindings.Enums;
                    if (enums.TryGet(property.EnumName, out RbxEnum enumType)
                        && enumType.TryGetItemByValue(property.GetInt(self), out RbxEnumItem item))
                    {
                        return LuaCsRbxDatatypeBindings.Wrap(item);
                    }

                    return LuaValue.Nil;
                default:
                    return LuaValue.Nil;
            }
        }

        private static void Write(LuaCsRbxModContext context, RbxInstance self, Property property, LuaValue value)
        {
            string owner = self.ClassName;
            switch (property.Kind)
            {
                case Kind.Bool:
                    property.SetBool(self, ReadAssignedBoolean(value, owner, property.Name));
                    return;
                case Kind.Int:
                    property.SetInt(self, ToInt(ReadAssignedNumber(value, owner, property.Name), owner, property.Name));
                    return;
                case Kind.Number:
                    property.SetNumber(self, ReadAssignedNumber(value, owner, property.Name));
                    return;
                case Kind.String:
                    string text = ReadAssignedString(value, owner, property.Name);
                    if (text.Length > MaxTextLength)
                    {
                        throw RbxUiErrors.Create(RbxUiErrors.TextTooLarge,
                            owner + "." + property.Name + " refused " + text.Length + " characters, at most "
                            + MaxTextLength,
                            "shorten the text, or split it over several labels");
                    }

                    property.SetString(self, text);
                    return;
                case Kind.Color3:
                    property.SetColor(self, ReadBoxed<RbxColor3>(value, owner, property.Name, "a Color3"));
                    return;
                case Kind.UDim:
                    property.SetUDim(self, ReadBoxed<RbxUDim>(value, owner, property.Name, "a UDim"));
                    return;
                case Kind.UDim2:
                    property.SetUDim2(self, ReadBoxed<RbxUDim2>(value, owner, property.Name, "a UDim2"));
                    return;
                case Kind.Vector2:
                    property.SetVector2(self, ReadBoxed<RbxVector2>(value, owner, property.Name, "a Vector2"));
                    return;
                case Kind.Enum:
                    RbxEnumItem item = ReadAssignedEnumItem(value, context.Bindings.Enums, property.EnumName, owner,
                        property.Name);
                    property.SetInt(self, item.Value);
                    return;
                case Kind.Instance:
                    if (value.Type == LuaValueType.Nil)
                    {
                        property.SetInstance(self, null);
                        return;
                    }

                    if (!TryGetInstance(value, out LuaCsRbxInstanceProxy proxy))
                    {
                        throw PropertyAssignmentError(owner, property.Name, "an Instance or nil", value);
                    }

                    property.SetInstance(self, proxy.Instance);
                    return;
            }
        }

        /// <summary>The longest text one GUI property takes (the same per-call cap as UxmlService text).</summary>
        private const int MaxTextLength = 65536;

        private static T ReadBoxed<T>(LuaValue value, string owner, string property, string expected)
        {
            if (TryUnbox(value, out T result))
            {
                return result;
            }

            throw PropertyAssignmentError(owner, property, expected, value);
        }

        private static int ToInt(double number, string owner, string property)
        {
            if (double.IsNaN(number) || double.IsInfinity(number))
            {
                throw RbxError.BadArgument(owner + "." + property + " needs a finite number",
                    "check the arithmetic that produced it (0/0, math.huge) before assigning");
            }

            if (number >= int.MaxValue)
            {
                return int.MaxValue;
            }

            if (number <= int.MinValue)
            {
                return int.MinValue;
            }

            return (int)number;
        }

        private static bool TryFind(ClassCatalog catalog, string className, string member, out Property property)
        {
            property = null;
            if (member == null)
            {
                return false;
            }

            if (!Flattened.TryGetValue(className, out Dictionary<string, Property> members))
            {
                members = Flatten(catalog, className);
                Flattened[className] = members;
            }

            return members.TryGetValue(member, out property);
        }

        private static Dictionary<string, Property> Flatten(ClassCatalog catalog, string className)
        {
            Dictionary<string, Property> members = new(StringComparer.Ordinal);
            string current = className;
            while (current != null)
            {
                if (Declared.TryGetValue(current, out Dictionary<string, Property> declared))
                {
                    foreach (KeyValuePair<string, Property> pair in declared)
                    {
                        members.TryAdd(pair.Key, pair.Value);
                    }
                }

                current = catalog.TryGet(current, out ClassDescriptor descriptor) ? descriptor.BaseClassName : null;
            }

            return members;
        }

        private static RbxGuiPresenter FindPresenter(LuaCsRbxModContext context)
        {
            RbxDataModel game = context.Bindings.Game;
            foreach (string name in game.Services.ServiceNames)
            {
                if (string.Equals(name, RbxUxmlService.ServiceClassName, StringComparison.Ordinal))
                {
                    return (game.Services.FindService(name) as RbxUxmlService)?.World?.GuiPresenter;
                }
            }

            return null;
        }

        private static RbxTextBox RequireTextBox(LuaFunctionExecutionContext ctx, string method)
        {
            if (!TryGetInstance(Arg(ctx, 0), out LuaCsRbxInstanceProxy proxy) || !(proxy.Instance is RbxTextBox box))
            {
                throw RbxError.BadArgument(
                    "TextBox:" + method + " must be called with a colon on a TextBox",
                    "write textBox:" + method + "(), not textBox." + method + "()");
            }

            return box;
        }

        private static RbxStarterGui RequireStarterGui(LuaFunctionExecutionContext ctx, string method,
            out LuaCsRbxModContext context)
        {
            if (!TryGetInstance(Arg(ctx, 0), out LuaCsRbxInstanceProxy proxy)
                || !(proxy.Instance is RbxStarterGui starterGui))
            {
                throw RbxError.BadArgument(
                    "StarterGui:" + method + " must be called with a colon on the service",
                    "write game:GetService(\"StarterGui\"):" + method + "(...)");
            }

            context = proxy.Context;
            return starterGui;
        }

        private static string TypeName(Kind kind)
        {
            switch (kind)
            {
                case Kind.Bool:
                    return "boolean";
                case Kind.Int:
                case Kind.Number:
                    return "number";
                case Kind.String:
                    return "string";
                case Kind.Color3:
                    return "Color3";
                case Kind.UDim:
                    return "UDim";
                case Kind.UDim2:
                    return "UDim2";
                case Kind.Vector2:
                    return "Vector2";
                case Kind.Instance:
                    return "Instance";
                default:
                    return "EnumItem";
            }
        }

        // ---- the member table --------------------------------------------------------------------

        private static Property Add(string className, string name, Kind kind, string enumName = null,
            bool readOnly = false)
        {
            if (!Declared.TryGetValue(className, out Dictionary<string, Property> members))
            {
                members = new Dictionary<string, Property>(StringComparer.Ordinal);
                Declared.Add(className, members);
            }

            Property property = new(className, name, kind, enumName, readOnly);
            members.Add(name, property);
            return property;
        }

        private static void Bool(string className, string name, Func<RbxInstance, bool> get,
            Action<RbxInstance, bool> set)
        {
            Property property = Add(className, name, Kind.Bool, readOnly: set == null);
            property.GetBool = get;
            property.SetBool = set;
        }

        private static void Int(string className, string name, Func<RbxInstance, int> get,
            Action<RbxInstance, int> set)
        {
            Property property = Add(className, name, Kind.Int, readOnly: set == null);
            property.GetInt = get;
            property.SetInt = set;
        }

        private static void Number(string className, string name, Func<RbxInstance, double> get,
            Action<RbxInstance, double> set)
        {
            Property property = Add(className, name, Kind.Number, readOnly: set == null);
            property.GetNumber = get;
            property.SetNumber = set;
        }

        private static void Text(string className, string name, Func<RbxInstance, string> get,
            Action<RbxInstance, string> set)
        {
            Property property = Add(className, name, Kind.String, readOnly: set == null);
            property.GetString = get;
            property.SetString = set;
        }

        private static void Color(string className, string name, Func<RbxInstance, RbxColor3> get,
            Action<RbxInstance, RbxColor3> set)
        {
            Property property = Add(className, name, Kind.Color3, readOnly: set == null);
            property.GetColor = get;
            property.SetColor = set;
        }

        private static void UDim(string className, string name, Func<RbxInstance, RbxUDim> get,
            Action<RbxInstance, RbxUDim> set)
        {
            Property property = Add(className, name, Kind.UDim, readOnly: set == null);
            property.GetUDim = get;
            property.SetUDim = set;
        }

        private static void UDim2(string className, string name, Func<RbxInstance, RbxUDim2> get,
            Action<RbxInstance, RbxUDim2> set)
        {
            Property property = Add(className, name, Kind.UDim2, readOnly: set == null);
            property.GetUDim2 = get;
            property.SetUDim2 = set;
        }

        private static void Vector2(string className, string name, Func<RbxInstance, RbxVector2> get,
            Action<RbxInstance, RbxVector2> set)
        {
            Property property = Add(className, name, Kind.Vector2, readOnly: set == null);
            property.GetVector2 = get;
            property.SetVector2 = set;
        }

        private static void Reference(string className, string name, Func<RbxInstance, RbxInstance> get,
            Action<RbxInstance, RbxInstance> set)
        {
            Property property = Add(className, name, Kind.Instance, readOnly: set == null);
            property.GetInstance = get;
            property.SetInstance = set;
        }

        private static void EnumItem(string className, string name, string enumName, Func<RbxInstance, int> get,
            Action<RbxInstance, int> set)
        {
            Property property = Add(className, name, Kind.Enum, enumName, set == null);
            property.GetInt = get;
            property.SetInt = set;
        }

        private static void DeclareBase()
        {
            Vector2("GuiBase2d", "AbsolutePosition", s => ((RbxGuiBase2d)s).AbsolutePosition, null);
            Vector2("GuiBase2d", "AbsoluteSize", s => ((RbxGuiBase2d)s).AbsoluteSize, null);
            Number("GuiBase2d", "AbsoluteRotation", s => ((RbxGuiBase2d)s).AbsoluteRotation, null);
            Bool("GuiBase2d", "AutoLocalize", s => ((RbxGuiBase2d)s).AutoLocalize,
                (s, v) => ((RbxGuiBase2d)s).SetAutoLocalize(v));
            Bool("LayerCollector", "Enabled", s => ((RbxLayerCollector)s).Enabled,
                (s, v) => ((RbxLayerCollector)s).SetEnabled(v));
            Bool("LayerCollector", "ResetOnSpawn", s => ((RbxLayerCollector)s).ResetOnSpawn,
                (s, v) => ((RbxLayerCollector)s).SetResetOnSpawn(v));
            EnumItem("LayerCollector", "ZIndexBehavior", "ZIndexBehavior", s => (int)((RbxLayerCollector)s).ZIndexBehavior,
                (s, v) => ((RbxLayerCollector)s).SetZIndexBehavior((RbxZIndexBehavior)v));
            Int("ScreenGui", "DisplayOrder", s => ((RbxScreenGui)s).DisplayOrder,
                (s, v) => ((RbxScreenGui)s).SetDisplayOrder(v));
            Bool("ScreenGui", "IgnoreGuiInset", s => ((RbxScreenGui)s).IgnoreGuiInset,
                (s, v) => ((RbxScreenGui)s).SetIgnoreGuiInset(v));
            Bool("ScreenGui", "ClipToDeviceSafeArea", s => ((RbxScreenGui)s).ClipToDeviceSafeArea,
                (s, v) => ((RbxScreenGui)s).SetClipToDeviceSafeArea(v));
            EnumItem("ScreenGui", "SafeAreaCompatibility", "SafeAreaCompatibility",
                s => ((RbxScreenGui)s).SafeAreaCompatibility, (s, v) => ((RbxScreenGui)s).SetSafeAreaCompatibility(v));
            EnumItem("ScreenGui", "ScreenInsets", "ScreenInsets", s => ((RbxScreenGui)s).ScreenInsets,
                (s, v) => ((RbxScreenGui)s).SetScreenInsets(v));
        }

        private static void DeclareObjects()
        {
            const string g = "GuiObject";
            Bool(g, "Active", s => ((RbxGuiObject)s).Active, (s, v) => ((RbxGuiObject)s).SetActive(v));
            Vector2(g, "AnchorPoint", s => ((RbxGuiObject)s).AnchorPoint, (s, v) => ((RbxGuiObject)s).SetAnchorPoint(v));
            EnumItem(g, "AutomaticSize", "AutomaticSize", s => (int)((RbxGuiObject)s).AutomaticSize,
                (s, v) => ((RbxGuiObject)s).SetAutomaticSize((RbxAutomaticSize)v));
            Color(g, "BackgroundColor3", s => ((RbxGuiObject)s).BackgroundColor3,
                (s, v) => ((RbxGuiObject)s).SetBackgroundColor3(v));
            Number(g, "BackgroundTransparency", s => ((RbxGuiObject)s).BackgroundTransparency,
                (s, v) => ((RbxGuiObject)s).SetBackgroundTransparency(v));
            Color(g, "BorderColor3", s => ((RbxGuiObject)s).BorderColor3, (s, v) => ((RbxGuiObject)s).SetBorderColor3(v));
            EnumItem(g, "BorderMode", "BorderMode", s => (int)((RbxGuiObject)s).BorderMode,
                (s, v) => ((RbxGuiObject)s).SetBorderMode((RbxBorderMode)v));
            Int(g, "BorderSizePixel", s => ((RbxGuiObject)s).BorderSizePixel,
                (s, v) => ((RbxGuiObject)s).SetBorderSizePixel(v));
            Bool(g, "ClipsDescendants", s => ((RbxGuiObject)s).ClipsDescendants,
                (s, v) => ((RbxGuiObject)s).SetClipsDescendants(v));
            Bool(g, "Interactable", s => ((RbxGuiObject)s).Interactable, (s, v) => ((RbxGuiObject)s).SetInteractable(v));
            Int(g, "LayoutOrder", s => ((RbxGuiObject)s).LayoutOrder, (s, v) => ((RbxGuiObject)s).SetLayoutOrder(v));
            UDim2(g, "Position", s => ((RbxGuiObject)s).Position, (s, v) => ((RbxGuiObject)s).SetPosition(v));
            Number(g, "Rotation", s => ((RbxGuiObject)s).Rotation, (s, v) => ((RbxGuiObject)s).SetRotation(v));
            Bool(g, "Selectable", s => ((RbxGuiObject)s).Selectable, (s, v) => ((RbxGuiObject)s).SetSelectable(v));
            UDim2(g, "Size", s => ((RbxGuiObject)s).Size, (s, v) => ((RbxGuiObject)s).SetSize(v));
            EnumItem(g, "SizeConstraint", "SizeConstraint", s => (int)((RbxGuiObject)s).SizeConstraint,
                (s, v) => ((RbxGuiObject)s).SetSizeConstraint((RbxSizeConstraint)v));
            Bool(g, "Visible", s => ((RbxGuiObject)s).Visible, (s, v) => ((RbxGuiObject)s).SetVisible(v));
            Int(g, "ZIndex", s => ((RbxGuiObject)s).ZIndex, (s, v) => ((RbxGuiObject)s).SetZIndex(v));
            EnumItem("Frame", "Style", "FrameStyle", s => ((RbxFrame)s).Style, (s, v) => ((RbxFrame)s).SetStyle(v));
            const string b = "GuiButton";
            Bool(b, "AutoButtonColor", s => ((RbxGuiButton)s).AutoButtonColor,
                (s, v) => ((RbxGuiButton)s).SetAutoButtonColor(v));
            Bool(b, "Modal", s => ((RbxGuiButton)s).Modal, (s, v) => ((RbxGuiButton)s).SetModal(v));
            Bool(b, "Selected", s => ((RbxGuiButton)s).Selected, (s, v) => ((RbxGuiButton)s).SetSelected(v));
            EnumItem(b, "Style", "ButtonStyle", s => ((RbxGuiButton)s).Style, (s, v) => ((RbxGuiButton)s).SetStyle(v));
        }

        private static RbxGuiText T(RbxInstance instance)
        {
            return ((IRbxGuiTextObject)instance).TextState;
        }

        private static void DeclareText(string c)
        {
            Text(c, "Text", s => T(s).Text, (s, v) => T(s).SetText(v));
            Text(c, "ContentText", s => T(s).ContentText, null);
            Color(c, "TextColor3", s => T(s).TextColor3, (s, v) => T(s).SetTextColor3(v));
            Number(c, "TextSize", s => T(s).TextSize, (s, v) => T(s).SetTextSize(v));
            Number(c, "TextTransparency", s => T(s).TextTransparency, (s, v) => T(s).SetTextTransparency(v));
            Bool(c, "TextScaled", s => T(s).TextScaled, (s, v) => T(s).SetTextScaled(v));
            Bool(c, "TextWrapped", s => T(s).TextWrapped, (s, v) => T(s).SetTextWrapped(v));
            EnumItem(c, "TextXAlignment", "TextXAlignment", s => (int)T(s).TextXAlignment,
                (s, v) => T(s).SetTextXAlignment((RbxTextXAlignment)v));
            EnumItem(c, "TextYAlignment", "TextYAlignment", s => (int)T(s).TextYAlignment,
                (s, v) => T(s).SetTextYAlignment((RbxTextYAlignment)v));
            EnumItem(c, "TextTruncate", "TextTruncate", s => (int)T(s).TextTruncate,
                (s, v) => T(s).SetTextTruncate((RbxTextTruncate)v));
            EnumItem(c, "Font", "Font", s => T(s).Font, (s, v) => T(s).SetFont(v));
            Bool(c, "RichText", s => T(s).RichText, (s, v) => T(s).SetRichText(v));
            Color(c, "TextStrokeColor3", s => T(s).TextStrokeColor3, (s, v) => T(s).SetTextStrokeColor3(v));
            Number(c, "TextStrokeTransparency", s => T(s).TextStrokeTransparency,
                (s, v) => T(s).SetTextStrokeTransparency(v));
            Number(c, "LineHeight", s => T(s).LineHeight, (s, v) => T(s).SetLineHeight(v));
            Int(c, "MaxVisibleGraphemes", s => T(s).MaxVisibleGraphemes, (s, v) => T(s).SetMaxVisibleGraphemes(v));
            EnumItem(c, "TextDirection", "TextDirection", s => T(s).TextDirection, (s, v) => T(s).SetTextDirection(v));
            if (c != "TextBox")
            {
                return;
            }

            Bool(c, "ClearTextOnFocus", s => ((RbxTextBox)s).ClearTextOnFocus,
                (s, v) => ((RbxTextBox)s).SetClearTextOnFocus(v));
            Bool(c, "MultiLine", s => ((RbxTextBox)s).MultiLine, (s, v) => ((RbxTextBox)s).SetMultiLine(v));
            Text(c, "PlaceholderText", s => ((RbxTextBox)s).PlaceholderText,
                (s, v) => ((RbxTextBox)s).SetPlaceholderText(v));
            Color(c, "PlaceholderColor3", s => ((RbxTextBox)s).PlaceholderColor3,
                (s, v) => ((RbxTextBox)s).SetPlaceholderColor3(v));
            Bool(c, "TextEditable", s => ((RbxTextBox)s).TextEditable, (s, v) => ((RbxTextBox)s).SetTextEditable(v));
            Int(c, "CursorPosition", s => ((RbxTextBox)s).CursorPosition,
                (s, v) => ((RbxTextBox)s).SetCursorPosition(v));
            Bool(c, "ShowNativeInput", s => ((RbxTextBox)s).ShowNativeInput,
                (s, v) => ((RbxTextBox)s).SetShowNativeInput(v));
        }

        private static RbxGuiImage I(RbxInstance instance)
        {
            return ((IRbxGuiImageObject)instance).ImageState;
        }

        private static void DeclareImage(string c)
        {
            Text(c, "Image", s => I(s).Image, (s, v) => I(s).SetImage(v));
            Color(c, "ImageColor3", s => I(s).ImageColor3, (s, v) => I(s).SetImageColor3(v));
            Number(c, "ImageTransparency", s => I(s).ImageTransparency, (s, v) => I(s).SetImageTransparency(v));
            EnumItem(c, "ScaleType", "ScaleType", s => (int)I(s).ScaleType, (s, v) => I(s).SetScaleType((RbxScaleType)v));
            Vector2(c, "ImageRectOffset", s => I(s).ImageRectOffset, (s, v) => I(s).SetImageRectOffset(v));
            Vector2(c, "ImageRectSize", s => I(s).ImageRectSize, (s, v) => I(s).SetImageRectSize(v));
            Number(c, "SliceScale", s => I(s).SliceScale, (s, v) => I(s).SetSliceScale(v));
            UDim2(c, "TileSize", s => I(s).TileSize, (s, v) => I(s).SetTileSize(v));
            EnumItem(c, "ResampleMode", "ResamplerMode", s => I(s).ResampleMode, (s, v) => I(s).SetResampleMode(v));
            Bool(c, "IsLoaded", s => I(s).IsDrawable, null);
            if (c != "ImageButton")
            {
                return;
            }

            Text(c, "HoverImage", s => ((RbxImageButton)s).HoverImage, (s, v) => ((RbxImageButton)s).SetHoverImage(v));
            Text(c, "PressedImage", s => ((RbxImageButton)s).PressedImage,
                (s, v) => ((RbxImageButton)s).SetPressedImage(v));
        }

        private static RbxScrollingFrame S(RbxInstance instance)
        {
            return (RbxScrollingFrame)instance;
        }

        private static void DeclareScrolling()
        {
            const string c = "ScrollingFrame";
            UDim2(c, "CanvasSize", s => S(s).CanvasSize, (s, v) => S(s).SetCanvasSize(v));
            Vector2(c, "CanvasPosition", s => S(s).CanvasPosition, (s, v) => S(s).SetCanvasPosition(v));
            Int(c, "ScrollBarThickness", s => S(s).ScrollBarThickness, (s, v) => S(s).SetScrollBarThickness(v));
            EnumItem(c, "ScrollingDirection", "ScrollingDirection", s => (int)S(s).ScrollingDirection,
                (s, v) => S(s).SetScrollingDirection((RbxScrollingDirection)v));
            Bool(c, "ScrollingEnabled", s => S(s).ScrollingEnabled, (s, v) => S(s).SetScrollingEnabled(v));
            Color(c, "ScrollBarImageColor3", s => S(s).ScrollBarImageColor3, (s, v) => S(s).SetScrollBarImageColor3(v));
            Number(c, "ScrollBarImageTransparency", s => S(s).ScrollBarImageTransparency,
                (s, v) => S(s).SetScrollBarImageTransparency(v));
            EnumItem(c, "AutomaticCanvasSize", "AutomaticSize", s => (int)S(s).AutomaticCanvasSize,
                (s, v) => S(s).SetAutomaticCanvasSize((RbxAutomaticSize)v));
            EnumItem(c, "ElasticBehavior", "ElasticBehavior", s => S(s).ElasticBehavior,
                (s, v) => S(s).SetElasticBehavior(v));
            EnumItem(c, "VerticalScrollBarPosition", "VerticalScrollBarPosition", s => S(s).VerticalScrollBarPosition,
                (s, v) => S(s).SetVerticalScrollBarPosition(v));
            EnumItem(c, "HorizontalScrollBarInset", "ScrollBarInset", s => S(s).HorizontalScrollBarInset,
                (s, v) => S(s).SetHorizontalScrollBarInset(v));
            EnumItem(c, "VerticalScrollBarInset", "ScrollBarInset", s => S(s).VerticalScrollBarInset,
                (s, v) => S(s).SetVerticalScrollBarInset(v));
            Vector2(c, "AbsoluteCanvasSize", s => S(s).AbsoluteCanvasSize, null);
            Vector2(c, "AbsoluteWindowSize", s => S(s).AbsoluteWindowSize, null);
        }

        private static void DeclareComponents()
        {
            UDim("UICorner", "CornerRadius", s => ((RbxUiCorner)s).CornerRadius,
                (s, v) => ((RbxUiCorner)s).SetCornerRadius(v));
            UDim("UICorner", "TopLeftRadius", s => ((RbxUiCorner)s).TopLeftRadius,
                (s, v) => ((RbxUiCorner)s).SetTopLeftRadius(v));
            UDim("UICorner", "TopRightRadius", s => ((RbxUiCorner)s).TopRightRadius,
                (s, v) => ((RbxUiCorner)s).SetTopRightRadius(v));
            UDim("UICorner", "BottomRightRadius", s => ((RbxUiCorner)s).BottomRightRadius,
                (s, v) => ((RbxUiCorner)s).SetBottomRightRadius(v));
            UDim("UICorner", "BottomLeftRadius", s => ((RbxUiCorner)s).BottomLeftRadius,
                (s, v) => ((RbxUiCorner)s).SetBottomLeftRadius(v));
            UDim("UIPadding", "PaddingLeft", s => ((RbxUiPadding)s).PaddingLeft,
                (s, v) => ((RbxUiPadding)s).SetPaddingLeft(v));
            UDim("UIPadding", "PaddingTop", s => ((RbxUiPadding)s).PaddingTop, (s, v) => ((RbxUiPadding)s).SetPaddingTop(v));
            UDim("UIPadding", "PaddingRight", s => ((RbxUiPadding)s).PaddingRight,
                (s, v) => ((RbxUiPadding)s).SetPaddingRight(v));
            UDim("UIPadding", "PaddingBottom", s => ((RbxUiPadding)s).PaddingBottom,
                (s, v) => ((RbxUiPadding)s).SetPaddingBottom(v));
            const string k = "UIStroke";
            EnumItem(k, "ApplyStrokeMode", "ApplyStrokeMode", s => (int)((RbxUiStroke)s).ApplyStrokeMode,
                (s, v) => ((RbxUiStroke)s).SetApplyStrokeMode((RbxApplyStrokeMode)v));
            Color(k, "Color", s => ((RbxUiStroke)s).Color, (s, v) => ((RbxUiStroke)s).SetColor(v));
            Bool(k, "Enabled", s => ((RbxUiStroke)s).Enabled, (s, v) => ((RbxUiStroke)s).SetEnabled(v));
            EnumItem(k, "LineJoinMode", "LineJoinMode", s => ((RbxUiStroke)s).LineJoinMode,
                (s, v) => ((RbxUiStroke)s).SetLineJoinMode(v));
            Number(k, "Thickness", s => ((RbxUiStroke)s).Thickness, (s, v) => ((RbxUiStroke)s).SetThickness(v));
            Number(k, "Transparency", s => ((RbxUiStroke)s).Transparency, (s, v) => ((RbxUiStroke)s).SetTransparency(v));
            Int(k, "ZIndex", s => ((RbxUiStroke)s).ZIndex, (s, v) => ((RbxUiStroke)s).SetZIndex(v));
            EnumItem(k, "BorderStrokePosition", "BorderStrokePosition", s => (int)((RbxUiStroke)s).BorderStrokePosition,
                (s, v) => ((RbxUiStroke)s).SetBorderStrokePosition((RbxBorderStrokePosition)v));
            UDim(k, "BorderOffset", s => ((RbxUiStroke)s).BorderOffset, (s, v) => ((RbxUiStroke)s).SetBorderOffset(v));
            EnumItem(k, "StrokeSizingMode", "StrokeSizingMode", s => ((RbxUiStroke)s).StrokeSizingMode,
                (s, v) => ((RbxUiStroke)s).SetStrokeSizingMode(v));
            Number("UIScale", "Scale", s => ((RbxUiScale)s).Scale, (s, v) => ((RbxUiScale)s).SetScale(v));
            const string l = "UIGridStyleLayout";
            EnumItem(l, "FillDirection", "FillDirection", s => (int)((RbxUiGridStyleLayout)s).FillDirection,
                (s, v) => ((RbxUiGridStyleLayout)s).SetFillDirection((RbxFillDirection)v));
            EnumItem(l, "HorizontalAlignment", "HorizontalAlignment",
                s => (int)((RbxUiGridStyleLayout)s).HorizontalAlignment,
                (s, v) => ((RbxUiGridStyleLayout)s).SetHorizontalAlignment((RbxHorizontalAlignment)v));
            EnumItem(l, "VerticalAlignment", "VerticalAlignment", s => (int)((RbxUiGridStyleLayout)s).VerticalAlignment,
                (s, v) => ((RbxUiGridStyleLayout)s).SetVerticalAlignment((RbxVerticalAlignment)v));
            EnumItem(l, "SortOrder", "SortOrder", s => (int)((RbxUiGridStyleLayout)s).SortOrder,
                (s, v) => ((RbxUiGridStyleLayout)s).SetSortOrder((RbxGuiSortOrder)v));
            Vector2(l, "AbsoluteContentSize", s => ((RbxUiGridStyleLayout)s).AbsoluteContentSize, null);
            UDim("UIListLayout", "Padding", s => ((RbxUiListLayout)s).Padding, (s, v) => ((RbxUiListLayout)s).SetPadding(v));
            Bool("UIListLayout", "Wraps", s => ((RbxUiListLayout)s).Wraps, (s, v) => ((RbxUiListLayout)s).SetWraps(v));
            EnumItem("UIListLayout", "HorizontalFlex", "UIFlexAlignment", s => ((RbxUiListLayout)s).HorizontalFlex,
                (s, v) => ((RbxUiListLayout)s).SetHorizontalFlex(v));
            EnumItem("UIListLayout", "VerticalFlex", "UIFlexAlignment", s => ((RbxUiListLayout)s).VerticalFlex,
                (s, v) => ((RbxUiListLayout)s).SetVerticalFlex(v));
            EnumItem("UIListLayout", "ItemLineAlignment", "ItemLineAlignment",
                s => ((RbxUiListLayout)s).ItemLineAlignment, (s, v) => ((RbxUiListLayout)s).SetItemLineAlignment(v));
            UDim2("UIGridLayout", "CellSize", s => ((RbxUiGridLayout)s).CellSize,
                (s, v) => ((RbxUiGridLayout)s).SetCellSize(v));
            UDim2("UIGridLayout", "CellPadding", s => ((RbxUiGridLayout)s).CellPadding,
                (s, v) => ((RbxUiGridLayout)s).SetCellPadding(v));
            Int("UIGridLayout", "FillDirectionMaxCells", s => ((RbxUiGridLayout)s).FillDirectionMaxCells,
                (s, v) => ((RbxUiGridLayout)s).SetFillDirectionMaxCells(v));
            EnumItem("UIGridLayout", "StartCorner", "StartCorner", s => (int)((RbxUiGridLayout)s).StartCorner,
                (s, v) => ((RbxUiGridLayout)s).SetStartCorner((RbxStartCorner)v));
            Vector2("UIGridLayout", "AbsoluteCellSize", s => ((RbxUiGridLayout)s).AbsoluteCellSize, null);
            Vector2("UIGridLayout", "AbsoluteCellCount", s => ((RbxUiGridLayout)s).AbsoluteCellCount, null);
            const string a = "UIAspectRatioConstraint";
            Number(a, "AspectRatio", s => ((RbxUiAspectRatioConstraint)s).AspectRatio,
                (s, v) => ((RbxUiAspectRatioConstraint)s).SetAspectRatio(v));
            EnumItem(a, "AspectType", "AspectType", s => (int)((RbxUiAspectRatioConstraint)s).AspectType,
                (s, v) => ((RbxUiAspectRatioConstraint)s).SetAspectType((RbxAspectType)v));
            EnumItem(a, "DominantAxis", "DominantAxis", s => (int)((RbxUiAspectRatioConstraint)s).DominantAxis,
                (s, v) => ((RbxUiAspectRatioConstraint)s).SetDominantAxis((RbxDominantAxis)v));
            Vector2("UISizeConstraint", "MinSize", s => ((RbxUiSizeConstraint)s).MinSize,
                (s, v) => ((RbxUiSizeConstraint)s).SetMinSize(v));
            Vector2("UISizeConstraint", "MaxSize", s => ((RbxUiSizeConstraint)s).MaxSize,
                (s, v) => ((RbxUiSizeConstraint)s).SetMaxSize(v));
        }

        private static void DeclareContainers()
        {
            EnumItem("PlayerGui", "ScreenOrientation", "ScreenOrientation", s => ((RbxPlayerGui)s).ScreenOrientation,
                (s, v) => ((RbxPlayerGui)s).SetScreenOrientation(v));
            EnumItem("PlayerGui", "CurrentScreenOrientation", "ScreenOrientation",
                s => ((RbxPlayerGui)s).ScreenOrientation, null);
            Bool("StarterGui", "ShowDevelopmentGui", s => ((RbxStarterGui)s).ShowDevelopmentGui,
                (s, v) => ((RbxStarterGui)s).SetShowDevelopmentGui(v));
            EnumItem("StarterGui", "ScreenOrientation", "ScreenOrientation", s => ((RbxStarterGui)s).ScreenOrientation,
                (s, v) => ((RbxStarterGui)s).SetScreenOrientation(v));
        }

        private static void DeclareAdornments()
        {
            Color("GuiBase3d", "Color3", s => ((RbxGuiBase3d)s).Color3, (s, v) => ((RbxGuiBase3d)s).Color3 = v);
            Number("GuiBase3d", "Transparency", s => ((RbxGuiBase3d)s).Transparency,
                (s, v) => ((RbxGuiBase3d)s).Transparency = (float)v);
            Bool("GuiBase3d", "Visible", s => ((RbxGuiBase3d)s).Visible, (s, v) => ((RbxGuiBase3d)s).Visible = v);
            Reference("InstanceAdornment", "Adornee", s => ((RbxInstanceAdornment)s).Adornee,
                (s, v) => ((RbxInstanceAdornment)s).Adornee = v);
            Number("SelectionBox", "LineThickness", s => ((RbxSelectionBox)s).LineThickness,
                (s, v) => ((RbxSelectionBox)s).LineThickness = (float)v);
            Color("SelectionBox", "SurfaceColor3", s => ((RbxSelectionBox)s).SurfaceColor3,
                (s, v) => ((RbxSelectionBox)s).SurfaceColor3 = v);
            Number("SelectionBox", "SurfaceTransparency", s => ((RbxSelectionBox)s).SurfaceTransparency,
                (s, v) => ((RbxSelectionBox)s).SurfaceTransparency = (float)v);
        }

        private static void DeclareTextChat()
        {
            Text("TextChatCommand", "PrimaryAlias", s => ((RbxTextChatCommand)s).PrimaryAlias,
                (s, v) => ((RbxTextChatCommand)s).PrimaryAlias = v);
            Text("TextChatCommand", "SecondaryAlias", s => ((RbxTextChatCommand)s).SecondaryAlias,
                (s, v) => ((RbxTextChatCommand)s).SecondaryAlias = v);
            Bool("TextChatCommand", "Enabled", s => ((RbxTextChatCommand)s).Enabled,
                (s, v) => ((RbxTextChatCommand)s).Enabled = v);
            Bool("TextChatCommand", "AutocompleteVisible", s => ((RbxTextChatCommand)s).AutocompleteVisible,
                (s, v) => ((RbxTextChatCommand)s).AutocompleteVisible = v);
            Number("TextSource", "UserId", s => ((RbxTextSource)s).UserId, null);
            Bool("TextSource", "CanSend", s => ((RbxTextSource)s).CanSend, (s, v) => ((RbxTextSource)s).CanSend = v);
            // WHY read-only: TextChatService.yaml gives these three RobloxScriptSecurity or PluginSecurity writes.
            EnumItem("TextChatService", "ChatVersion", "ChatVersion", s => ((RbxTextChatService)s).ChatVersion, null);
            Bool("TextChatService", "CreateDefaultCommands", s => ((RbxTextChatService)s).CreateDefaultCommands, null);
            Bool("TextChatService", "CreateDefaultTextChannels",
                s => ((RbxTextChatService)s).CreateDefaultTextChannels, null);
        }
    }
}
