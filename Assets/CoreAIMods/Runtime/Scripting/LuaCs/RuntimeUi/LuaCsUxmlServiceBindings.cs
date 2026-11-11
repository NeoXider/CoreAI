using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.RuntimeUi;
using Lua;
using Lua.Runtime;
using static CoreAI.Ai.LuaCs.LuaCsRbxLua;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// The Lua surface of <c>game:GetService("UxmlService")</c> (P18): the service members, and the
    /// <c>UiScreen</c> and <c>UiElement</c> userdata. This layer only marshals values and checks the
    /// <see cref="LuaCapabilities.Ui"/> capability and screen ownership; budgets, lifetimes and errors
    /// live in the engine-free <see cref="RbxUiWorld"/>. Signals are ordinary deferred script signals
    /// wrapped with the acting mod context, so their connections follow the existing lifetime rules.
    /// </summary>
    internal static class LuaCsUxmlServiceBindings
    {
        private static readonly LuaFunction CreateScreenFn = Fn("UxmlService:CreateScreen", CreateScreen);
        private static readonly LuaFunction GetScreenFn = Fn("UxmlService:GetScreen", GetScreen);
        private static readonly LuaFunction GetScreensFn = Fn("UxmlService:GetScreens", GetScreens);
        private static readonly LuaFunction SetTokenFn = Fn("UxmlService:SetToken", SetToken);
        private static readonly LuaFunction GetTokenFn = Fn("UxmlService:GetToken", GetToken);

        private static readonly LuaTable ScreenMeta = BuildScreenMeta();
        private static readonly LuaTable ElementMeta = BuildElementMeta();

        /// <summary>
        /// The <c>Instance.__index</c> hook: answers the UxmlService members of a
        /// <see cref="RbxUxmlService"/> and declines every other instance and key.
        /// </summary>
        public static bool TryRead(LuaCsRbxModContext context, RbxInstance self, string key, out LuaValue value)
        {
            value = LuaValue.Nil;
            if (!(self is RbxUxmlService service))
            {
                return false;
            }

            switch (key)
            {
                case "CreateScreen":
                    RequireUi(context, "UxmlService:CreateScreen");
                    value = CreateScreenFn;
                    return true;
                case "GetScreen":
                    RequireUi(context, "UxmlService:GetScreen");
                    value = GetScreenFn;
                    return true;
                case "GetScreens":
                    RequireUi(context, "UxmlService:GetScreens");
                    value = GetScreensFn;
                    return true;
                case "SetToken":
                    RequireUi(context, "UxmlService:SetToken");
                    value = SetTokenFn;
                    return true;
                case "GetToken":
                    RequireUi(context, "UxmlService:GetToken");
                    value = GetTokenFn;
                    return true;
                case "TextInputFocused":
                    value = RequireWorld(context, service, "UxmlService.TextInputFocused").IsTextInputFocused;
                    return true;
                case "PointerOverUi":
                    value = RequireWorld(context, service, "UxmlService.PointerOverUi").IsPointerOverUi;
                    return true;
                case "TextInputFocusChanged":
                    value = LuaCsRbxDatatypeBindings.Wrap(
                        RequireWorld(context, service, "UxmlService.TextInputFocusChanged").TextInputFocusChanged,
                        context);
                    return true;
                default:
                    return false;
            }
        }

        private static LuaValue CreateScreen(LuaFunctionExecutionContext ctx)
        {
            const string member = "UxmlService:CreateScreen";
            ServiceCall call = ReadService(ctx, member);
            string id = ReadString(ctx, 1, member, 1);
            string uxml = ReadOptionalString(ctx, 2, member, 2) ?? string.Empty;
            string uss = ReadOptionalString(ctx, 3, member, 3);
            LuaValue options = Arg(ctx, 4);
            int? sortOrder = null;
            bool? visible = null;
            if (options.Type == LuaValueType.Table)
            {
                LuaTable table = options.Read<LuaTable>();
                sortOrder = ReadOptionalInt(table["SortOrder"], member, "options.SortOrder");
                visible = ReadOptionalBool(table["Visible"], member, "options.Visible");
            }
            else if (options.Type != LuaValueType.Nil)
            {
                throw RbxUiErrors.Create(RbxUiErrors.InvalidArgument,
                    member + " expects an options table or nil at argument 4, got " + Describe(options),
                    "pass { SortOrder = 10, Visible = true } or leave argument 4 out");
            }

            RbxUiScreenHandle screen = call.World.CreateScreen(call.Caller, id, uxml, uss, sortOrder, visible);
            return WrapScreen(screen, call.Context);
        }

        private static LuaValue GetScreen(LuaFunctionExecutionContext ctx)
        {
            const string member = "UxmlService:GetScreen";
            ServiceCall call = ReadService(ctx, member);
            RbxUiScreenHandle screen = call.World.GetScreen(call.Caller, ReadString(ctx, 1, member, 1));
            return WrapScreen(screen, call.Context);
        }

        private static LuaValue GetScreens(LuaFunctionExecutionContext ctx)
        {
            ServiceCall call = ReadService(ctx, "UxmlService:GetScreens");
            List<RbxUiScreenHandle> screens = new();
            call.World.GetScreens(call.Caller, screens);
            LuaTable table = new();
            for (int i = 0; i < screens.Count; i++)
            {
                table[i + 1] = WrapScreen(screens[i], call.Context);
            }

            return new LuaValue(table);
        }

        private static LuaValue SetToken(LuaFunctionExecutionContext ctx)
        {
            const string member = "UxmlService:SetToken";
            ServiceCall call = ReadService(ctx, member);
            call.World.SetToken(call.Caller, ReadString(ctx, 1, member, 1), ReadString(ctx, 2, member, 2));
            return LuaValue.Nil;
        }

        private static LuaValue GetToken(LuaFunctionExecutionContext ctx)
        {
            const string member = "UxmlService:GetToken";
            ServiceCall call = ReadService(ctx, member);
            string value = call.World.GetToken(call.Caller, ReadString(ctx, 1, member, 1));
            return value == null ? LuaValue.Nil : new LuaValue(value);
        }

        private static LuaTable BuildScreenMeta()
        {
            Dictionary<string, LuaFunction> methods = new(StringComparer.Ordinal)
            {
                ["SetUxml"] = Fn("UiScreen:SetUxml", ctx =>
                {
                    ScreenCall call = ReadScreen(ctx, "UiScreen:SetUxml");
                    return DiagnosticsToLua(call.Screen.SetUxml(ReadOptionalString(ctx, 1, "UiScreen:SetUxml", 1)));
                }),
                ["SetUss"] = Fn("UiScreen:SetUss", ctx =>
                {
                    ScreenCall call = ReadScreen(ctx, "UiScreen:SetUss");
                    return DiagnosticsToLua(call.Screen.SetUss(ReadOptionalString(ctx, 1, "UiScreen:SetUss", 1)));
                }),
                ["Query"] = Fn("UiScreen:Query", ctx =>
                {
                    ScreenCall call = ReadScreen(ctx, "UiScreen:Query");
                    return WrapElement(call.Screen.Query(ReadString(ctx, 1, "UiScreen:Query", 1)), call.Context);
                }),
                ["QueryAll"] = Fn("UiScreen:QueryAll", ctx =>
                {
                    ScreenCall call = ReadScreen(ctx, "UiScreen:QueryAll");
                    List<RbxUiElementHandle> found = new();
                    call.Screen.QueryAll(ReadString(ctx, 1, "UiScreen:QueryAll", 1), found);
                    return ElementsToLua(found, call.Context);
                }),
                ["GetElement"] = Fn("UiScreen:GetElement", ctx =>
                {
                    ScreenCall call = ReadScreen(ctx, "UiScreen:GetElement");
                    return WrapElement(call.Screen.GetElement(ReadString(ctx, 1, "UiScreen:GetElement", 1)),
                        call.Context);
                }),
                ["CreateElement"] = Fn("UiScreen:CreateElement", ctx =>
                {
                    const string member = "UiScreen:CreateElement";
                    ScreenCall call = ReadScreen(ctx, member);
                    string kind = ReadString(ctx, 1, member, 1);
                    RbxUiElementHandle parent = ReadOptionalElement(ctx, 2, member, 2, call.Context);
                    string name = ReadOptionalString(ctx, 3, member, 3);
                    return WrapElement(call.Screen.CreateElement(kind, parent, name), call.Context);
                }),
                ["Show"] = Fn("UiScreen:Show", ctx =>
                {
                    ReadScreen(ctx, "UiScreen:Show").Screen.SetVisible(true);
                    return LuaValue.Nil;
                }),
                ["Hide"] = Fn("UiScreen:Hide", ctx =>
                {
                    ReadScreen(ctx, "UiScreen:Hide").Screen.SetVisible(false);
                    return LuaValue.Nil;
                }),
                ["Destroy"] = Fn("UiScreen:Destroy", ctx =>
                {
                    LuaCsUiScreenProxy proxy = ReadScreenProxy(ctx, "UiScreen:Destroy");
                    RequireUi(proxy.Context, "UiScreen:Destroy");
                    proxy.Screen.RequireAccessAllowDestroyed(proxy.Context.OwnerModId, "UiScreen:Destroy");
                    proxy.Screen.Destroy();
                    return LuaValue.Nil;
                })
            };

            LuaTable meta = new();
            meta[Metamethods.Index] = Fn("UiScreen.__index", ctx =>
            {
                LuaCsUiScreenProxy proxy = ReadScreenProxy(ctx, "UiScreen member access");
                string key = ReadString(ctx, 1, "UiScreen member access");
                RbxUiScreenHandle screen = proxy.Screen;
                switch (key)
                {
                    case "Id":
                        return screen.Id;
                    case "IsAlive":
                        return !screen.IsDestroyed;
                }

                RequireUi(proxy.Context, "UiScreen." + key);
                screen.RequireAccessAllowDestroyed(proxy.Context.OwnerModId, "UiScreen." + key);
                switch (key)
                {
                    case "Visible":
                        return screen.Visible;
                    case "SortOrder":
                        return screen.SortOrder;
                    case "ElementCount":
                        return screen.IsDestroyed ? 0 : screen.Port.ElementCount;
                    case "Diagnostics":
                        return DiagnosticsToLua(screen.Diagnostics);
                    case "Root":
                        return screen.IsDestroyed ? LuaValue.Nil : WrapElement(screen.Root, proxy.Context);
                    case "Destroying":
                        return LuaCsRbxDatatypeBindings.Wrap(screen.Destroying, proxy.Context);
                }

                if (methods.TryGetValue(key, out LuaFunction method))
                {
                    return method;
                }

                throw NotAMember(key, "UiScreen");
            });
            meta[Metamethods.NewIndex] = Fn("UiScreen.__newindex", ctx =>
            {
                ScreenCall call = ReadScreen(ctx, "UiScreen member assignment");
                string key = ReadString(ctx, 1, "UiScreen member assignment");
                LuaValue value = Arg(ctx, 2);
                switch (key)
                {
                    case "Visible":
                        call.Screen.SetVisible(ReadAssignedBoolean(value, "UiScreen", "Visible"));
                        return LuaValue.Nil;
                    case "SortOrder":
                        call.Screen.SetSortOrder(ReadAssignedInt(value, "UiScreen", "SortOrder"));
                        return LuaValue.Nil;
                    default:
                        throw ReadOnlyOrUnknown(key, "UiScreen", "Visible and SortOrder");
                }
            });
            meta[Metamethods.ToString] = Fn("UiScreen.__tostring",
                ctx => "UiScreen " + ReadScreenProxy(ctx, "tostring").Screen.Id);
            meta[Metamethods.Eq] = Fn("UiScreen.__eq", ctx =>
                Arg(ctx, 0).TryRead(out LuaCsUiScreenProxy a) && Arg(ctx, 1).TryRead(out LuaCsUiScreenProxy b)
                && ReferenceEquals(a.Screen, b.Screen));
            return Lock(meta);
        }

        private static LuaTable BuildElementMeta()
        {
            Dictionary<string, LuaFunction> methods = new(StringComparer.Ordinal)
            {
                ["AddClass"] = Fn("UiElement:AddClass", ctx =>
                {
                    ReadElement(ctx, "UiElement:AddClass").Element.AddClass(ReadString(ctx, 1, "UiElement:AddClass", 1));
                    return LuaValue.Nil;
                }),
                ["RemoveClass"] = Fn("UiElement:RemoveClass", ctx =>
                {
                    ReadElement(ctx, "UiElement:RemoveClass").Element
                        .RemoveClass(ReadString(ctx, 1, "UiElement:RemoveClass", 1));
                    return LuaValue.Nil;
                }),
                ["ToggleClass"] = Fn("UiElement:ToggleClass", ctx =>
                    ReadElement(ctx, "UiElement:ToggleClass").Element
                        .ToggleClass(ReadString(ctx, 1, "UiElement:ToggleClass", 1))),
                ["HasClass"] = Fn("UiElement:HasClass", ctx =>
                    ReadElement(ctx, "UiElement:HasClass").Element
                        .HasClass(ReadString(ctx, 1, "UiElement:HasClass", 1))),
                ["GetClasses"] = Fn("UiElement:GetClasses", ctx =>
                {
                    List<string> classes = new();
                    ReadElement(ctx, "UiElement:GetClasses").Element.GetClasses(classes);
                    return StringsToLua(classes);
                }),
                ["SetStyle"] = Fn("UiElement:SetStyle", ctx =>
                    ReadElement(ctx, "UiElement:SetStyle").Element.SetStyle(
                        ReadString(ctx, 1, "UiElement:SetStyle", 1), ReadString(ctx, 2, "UiElement:SetStyle", 2))),
                ["ClearStyle"] = Fn("UiElement:ClearStyle", ctx =>
                    ReadElement(ctx, "UiElement:ClearStyle").Element
                        .ClearStyle(ReadString(ctx, 1, "UiElement:ClearStyle", 1))),
                ["GetStyle"] = Fn("UiElement:GetStyle", ctx =>
                {
                    string value = ReadElement(ctx, "UiElement:GetStyle").Element
                        .GetStyle(ReadString(ctx, 1, "UiElement:GetStyle", 1));
                    return value == null ? LuaValue.Nil : new LuaValue(value);
                }),
                ["SetAttribute"] = Fn("UiElement:SetAttribute", ctx =>
                    ReadElement(ctx, "UiElement:SetAttribute").Element.SetAttribute(
                        ReadString(ctx, 1, "UiElement:SetAttribute", 1),
                        ReadString(ctx, 2, "UiElement:SetAttribute", 2))),
                ["SetItems"] = Fn("UiElement:SetItems", ctx =>
                {
                    ElementCall call = ReadElement(ctx, "UiElement:SetItems");
                    call.Element.SetItems(ReadStringList(ctx, 1, "UiElement:SetItems", 1));
                    return LuaValue.Nil;
                }),
                ["Focus"] = Fn("UiElement:Focus", ctx =>
                {
                    ReadElement(ctx, "UiElement:Focus").Element.Focus();
                    return LuaValue.Nil;
                }),
                ["Blur"] = Fn("UiElement:Blur", ctx =>
                {
                    ReadElement(ctx, "UiElement:Blur").Element.Blur();
                    return LuaValue.Nil;
                }),
                ["Query"] = Fn("UiElement:Query", ctx =>
                {
                    ElementCall call = ReadElement(ctx, "UiElement:Query");
                    return WrapElement(call.Element.Query(ReadString(ctx, 1, "UiElement:Query", 1)), call.Context);
                }),
                ["CreateChild"] = Fn("UiElement:CreateChild", ctx =>
                {
                    const string member = "UiElement:CreateChild";
                    ElementCall call = ReadElement(ctx, member);
                    return WrapElement(call.Element.CreateChild(ReadString(ctx, 1, member, 1),
                        ReadOptionalString(ctx, 2, member, 2)), call.Context);
                }),
                ["Remove"] = Fn("UiElement:Remove", ctx =>
                {
                    ReadElement(ctx, "UiElement:Remove").Element.Remove();
                    return LuaValue.Nil;
                })
            };

            LuaTable meta = new();
            meta[Metamethods.Index] = Fn("UiElement.__index", ctx =>
            {
                ElementCall call = ReadElement(ctx, "UiElement member access");
                RbxUiElementHandle element = call.Element;
                string key = ReadString(ctx, 1, "UiElement member access");
                switch (key)
                {
                    case "Name":
                        return element.Name == null ? LuaValue.Nil : new LuaValue(element.Name);
                    case "Kind":
                        return element.Kind;
                    case "IsAlive":
                        return element.IsAlive;
                    case "Parent":
                        return WrapElement(element.Parent, call.Context);
                    case "Text":
                        return element.Text == null ? LuaValue.Nil : new LuaValue(element.Text);
                    case "Value":
                        return element.TryGetValue(out RbxUiValue value) ? ValueToLua(value) : LuaValue.Nil;
                    case "Visible":
                        return element.Visible;
                    case "Enabled":
                        return element.Enabled;
                    case "ZOrder":
                        return element.ZOrder;
                    case "Clicked":
                        return LuaCsRbxDatatypeBindings.Wrap(element.Clicked, call.Context);
                    case "ValueChanged":
                        return LuaCsRbxDatatypeBindings.Wrap(element.ValueChanged, call.Context);
                    case "Submitted":
                        return LuaCsRbxDatatypeBindings.Wrap(element.Submitted, call.Context);
                }

                if (methods.TryGetValue(key, out LuaFunction method))
                {
                    return method;
                }

                throw NotAMember(key, "UiElement");
            });
            meta[Metamethods.NewIndex] = Fn("UiElement.__newindex", ctx =>
            {
                ElementCall call = ReadElement(ctx, "UiElement member assignment");
                string key = ReadString(ctx, 1, "UiElement member assignment");
                LuaValue value = Arg(ctx, 2);
                switch (key)
                {
                    case "Text":
                        call.Element.SetText(ReadAssignedString(value, "UiElement", "Text"));
                        return LuaValue.Nil;
                    case "Value":
                        call.Element.SetValue(ReadAssignedValue(value));
                        return LuaValue.Nil;
                    case "Visible":
                        call.Element.SetVisible(ReadAssignedBoolean(value, "UiElement", "Visible"));
                        return LuaValue.Nil;
                    case "Enabled":
                        call.Element.SetEnabled(ReadAssignedBoolean(value, "UiElement", "Enabled"));
                        return LuaValue.Nil;
                    case "ZOrder":
                        call.Element.SetZOrder(ReadAssignedInt(value, "UiElement", "ZOrder"));
                        return LuaValue.Nil;
                    default:
                        throw ReadOnlyOrUnknown(key, "UiElement", "Text, Value, Visible, Enabled and ZOrder");
                }
            });
            meta[Metamethods.ToString] = Fn("UiElement.__tostring",
                ctx => "UiElement " + ReadElementProxy(ctx, "tostring").Element);
            meta[Metamethods.Eq] = Fn("UiElement.__eq", ctx =>
                Arg(ctx, 0).TryRead(out LuaCsUiElementProxy a) && Arg(ctx, 1).TryRead(out LuaCsUiElementProxy b)
                && ReferenceEquals(a.Element, b.Element));
            return Lock(meta);
        }

        private static LuaValue WrapScreen(RbxUiScreenHandle screen, LuaCsRbxModContext context)
        {
            if (screen == null)
            {
                return LuaValue.Nil;
            }

            // WHY one proxy per screen and context: Lua table keys compare userdata by identity, so a
            // script that keys a table by its screen must get the same proxy back from every lookup.
            if (ReferenceEquals(screen.BindingProxyOwner, context) && screen.BindingProxy is LuaCsUiScreenProxy cached)
            {
                return new LuaValue(cached);
            }

            LuaCsUiScreenProxy proxy = new(screen, context, ScreenMeta);
            screen.BindingProxy = proxy;
            screen.BindingProxyOwner = context;
            return new LuaValue(proxy);
        }

        private static LuaValue WrapElement(RbxUiElementHandle element, LuaCsRbxModContext context)
        {
            if (element == null)
            {
                return LuaValue.Nil;
            }

            if (ReferenceEquals(element.BindingProxyOwner, context) && element.BindingProxy is LuaCsUiElementProxy cached)
            {
                return new LuaValue(cached);
            }

            LuaCsUiElementProxy proxy = new(element, context, ElementMeta);
            element.BindingProxy = proxy;
            element.BindingProxyOwner = context;
            return new LuaValue(proxy);
        }

        private static LuaValue ElementsToLua(List<RbxUiElementHandle> elements, LuaCsRbxModContext context)
        {
            LuaTable table = new();
            for (int i = 0; i < elements.Count; i++)
            {
                table[i + 1] = WrapElement(elements[i], context);
            }

            return new LuaValue(table);
        }

        private static LuaValue StringsToLua(List<string> values)
        {
            LuaTable table = new();
            for (int i = 0; i < values.Count; i++)
            {
                table[i + 1] = values[i];
            }

            return new LuaValue(table);
        }

        private static LuaValue DiagnosticsToLua(IReadOnlyList<RbxUiDiagnostic> diagnostics)
        {
            LuaTable table = new();
            if (diagnostics == null)
            {
                return new LuaValue(table);
            }

            for (int i = 0; i < diagnostics.Count; i++)
            {
                RbxUiDiagnostic report = diagnostics[i];
                LuaTable entry = new();
                entry["Code"] = report.Code;
                entry["Severity"] = report.Severity;
                entry["Source"] = report.Source;
                entry["Line"] = report.Line;
                entry["Column"] = report.Column;
                entry["Message"] = report.Message;
                table[i + 1] = new LuaValue(entry);
            }

            return new LuaValue(table);
        }

        private static LuaValue ValueToLua(RbxUiValue value)
        {
            switch (value.Kind)
            {
                case RbxUiValueKind.Boolean:
                    return value.Boolean;
                case RbxUiValueKind.Number:
                    return value.Number;
                case RbxUiValueKind.Text:
                    return value.Text ?? string.Empty;
                default:
                    return LuaValue.Nil;
            }
        }

        private static void RequireUi(LuaCsRbxModContext context, string member)
        {
            if (context == null || (context.Capabilities & LuaCapabilities.Ui) == 0)
            {
                throw RbxUiErrors.MissingCapability(member);
            }
        }

        private static RbxUiWorld RequireWorld(LuaCsRbxModContext context, RbxUxmlService service, string member)
        {
            RequireUi(context, member);
            RbxUiWorld world = service.World;
            if (world == null || !world.IsAvailable)
            {
                throw RbxUiErrors.NotAvailable(member);
            }

            return world;
        }

        private static ServiceCall ReadService(LuaFunctionExecutionContext ctx, string member)
        {
            if (!TryGetInstance(Arg(ctx, 0), out LuaCsRbxInstanceProxy proxy)
                || !(proxy.Instance is RbxUxmlService service))
            {
                throw RbxError.BadArgument(
                    member + " must be called with a colon on the service",
                    "write UxmlService:" + member.Substring(member.IndexOf(':') + 1) + "(...), not UxmlService."
                    + member.Substring(member.IndexOf(':') + 1) + "(...)");
            }

            LuaCsRbxModContext context = proxy.Context;
            RbxUiWorld world = RequireWorld(context, service, member);
            RbxUiCaller caller = world.GetCaller(context.OwnerModId, context.ConnectionGeneration,
                context.TrackConnection);
            return new ServiceCall(context, world, caller);
        }

        private static LuaCsUiScreenProxy ReadScreenProxy(LuaFunctionExecutionContext ctx, string member)
        {
            if (!Arg(ctx, 0).TryRead(out LuaCsUiScreenProxy proxy) || proxy == null)
            {
                throw RbxError.BadArgument(
                    member + " expects a UiScreen, got " + Describe(Arg(ctx, 0)),
                    "call screen methods with a colon, e.g. hud:Query(\".slot\")");
            }

            return proxy;
        }

        private static ScreenCall ReadScreen(LuaFunctionExecutionContext ctx, string member)
        {
            LuaCsUiScreenProxy proxy = ReadScreenProxy(ctx, member);
            RequireUi(proxy.Context, member);
            proxy.Screen.RequireAccess(proxy.Context.OwnerModId, member);
            return new ScreenCall(proxy.Context, proxy.Screen);
        }

        private static LuaCsUiElementProxy ReadElementProxy(LuaFunctionExecutionContext ctx, string member)
        {
            if (!Arg(ctx, 0).TryRead(out LuaCsUiElementProxy proxy) || proxy == null)
            {
                throw RbxError.BadArgument(
                    member + " expects a UiElement, got " + Describe(Arg(ctx, 0)),
                    "call element methods with a colon, e.g. slot:AddClass(\"is-selected\")");
            }

            return proxy;
        }

        private static ElementCall ReadElement(LuaFunctionExecutionContext ctx, string member)
        {
            LuaCsUiElementProxy proxy = ReadElementProxy(ctx, member);
            RequireUi(proxy.Context, member);
            proxy.Element.Screen.RequireAccessAllowDestroyed(proxy.Context.OwnerModId, member);
            return new ElementCall(proxy.Context, proxy.Element);
        }

        private static RbxUiElementHandle ReadOptionalElement(LuaFunctionExecutionContext ctx, int index,
            string member, int argumentNumber, LuaCsRbxModContext context)
        {
            LuaValue value = Arg(ctx, index);
            if (value.Type == LuaValueType.Nil)
            {
                return null;
            }

            if (!value.TryRead(out LuaCsUiElementProxy proxy) || proxy == null)
            {
                throw ExpectedArgument(member, "a UiElement or nil", value, argumentNumber);
            }

            proxy.Element.Screen.RequireAccessAllowDestroyed(context.OwnerModId, member);
            return proxy.Element;
        }

        private static string ReadOptionalString(LuaFunctionExecutionContext ctx, int index, string member,
            int argumentNumber)
        {
            LuaValue value = Arg(ctx, index);
            if (value.Type == LuaValueType.Nil)
            {
                return null;
            }

            if (!TryCoerceString(value, out string text))
            {
                throw ExpectedArgument(member, "a string or nil", value, argumentNumber);
            }

            return text;
        }

        private static IReadOnlyList<string> ReadStringList(LuaFunctionExecutionContext ctx, int index,
            string member, int argumentNumber)
        {
            LuaValue value = Arg(ctx, index);
            if (value.Type != LuaValueType.Table)
            {
                throw ExpectedArgument(member, "a table of strings", value, argumentNumber);
            }

            LuaTable table = value.Read<LuaTable>();
            List<string> items = new(table.ArrayLength);
            for (int i = 1; i <= table.ArrayLength; i++)
            {
                if (!TryCoerceString(table[i], out string text))
                {
                    throw RbxUiErrors.Create(RbxUiErrors.InvalidArgument,
                        member + " expects strings, got " + Describe(table[i]) + " at row " + i,
                        "pass rows as text, e.g. { \"Stone\", \"Dirt\" }, using tostring for numbers");
                }

                items.Add(text);
            }

            return items;
        }

        private static int? ReadOptionalInt(LuaValue value, string member, string name)
        {
            if (value.Type == LuaValueType.Nil)
            {
                return null;
            }

            if (!TryCoerceNumber(value, out double number) || double.IsNaN(number) || double.IsInfinity(number))
            {
                throw RbxUiErrors.Create(RbxUiErrors.InvalidArgument,
                    member + " expects a number for " + name + ", got " + Describe(value),
                    "pass a whole number such as 10");
            }

            return ClampToInt(number);
        }

        private static bool? ReadOptionalBool(LuaValue value, string member, string name)
        {
            if (value.Type == LuaValueType.Nil)
            {
                return null;
            }

            if (value.Type != LuaValueType.Boolean)
            {
                throw RbxUiErrors.Create(RbxUiErrors.InvalidArgument,
                    member + " expects a boolean for " + name + ", got " + Describe(value),
                    "pass true or false");
            }

            return value.Read<bool>();
        }

        private static int ReadAssignedInt(LuaValue value, string owner, string property)
        {
            double number = ReadAssignedNumber(value, owner, property);
            if (double.IsNaN(number) || double.IsInfinity(number))
            {
                throw PropertyAssignmentError(owner, property, "a finite number", value);
            }

            return ClampToInt(number);
        }

        private static RbxUiValue ReadAssignedValue(LuaValue value)
        {
            switch (value.Type)
            {
                case LuaValueType.Boolean:
                    return RbxUiValue.FromBoolean(value.Read<bool>());
                case LuaValueType.Number:
                    return RbxUiValue.FromNumber(value.Read<double>());
                case LuaValueType.String:
                    return RbxUiValue.FromText(value.Read<string>());
                default:
                    throw PropertyAssignmentError("UiElement", "Value", "a boolean, a number or a string", value);
            }
        }

        private static int ClampToInt(double number)
        {
            if (number >= int.MaxValue)
            {
                return int.MaxValue;
            }

            if (number <= int.MinValue)
            {
                return int.MinValue;
            }

            return (int)Math.Round(number);
        }

        private static RbxError NotAMember(string key, string typeName)
        {
            return RbxError.BadArgument(
                key + " is not a valid member of " + typeName,
                "check the " + typeName + " member list in the UI section of the Rbx API skill");
        }

        private static RbxError ReadOnlyOrUnknown(string key, string typeName, string writable)
        {
            return RbxError.BadArgument(
                key + " cannot be assigned on " + typeName,
                "assign only " + writable + "; call the methods for everything else");
        }

        private readonly struct ServiceCall
        {
            public ServiceCall(LuaCsRbxModContext context, RbxUiWorld world, RbxUiCaller caller)
            {
                Context = context;
                World = world;
                Caller = caller;
            }

            public LuaCsRbxModContext Context { get; }

            public RbxUiWorld World { get; }

            public RbxUiCaller Caller { get; }
        }

        private readonly struct ScreenCall
        {
            public ScreenCall(LuaCsRbxModContext context, RbxUiScreenHandle screen)
            {
                Context = context;
                Screen = screen;
            }

            public LuaCsRbxModContext Context { get; }

            public RbxUiScreenHandle Screen { get; }
        }

        private readonly struct ElementCall
        {
            public ElementCall(LuaCsRbxModContext context, RbxUiElementHandle element)
            {
                Context = context;
                Element = element;
            }

            public LuaCsRbxModContext Context { get; }

            public RbxUiElementHandle Element { get; }
        }
    }

    /// <summary>Lua userdata for one <c>UiScreen</c>: the domain handle plus the mod context that holds it.</summary>
    internal sealed class LuaCsUiScreenProxy : ILuaUserData
    {
        public LuaCsUiScreenProxy(RbxUiScreenHandle screen, LuaCsRbxModContext context, LuaTable metatable)
        {
            Screen = screen;
            Context = context;
            Metatable = metatable;
        }

        public RbxUiScreenHandle Screen { get; }

        public LuaCsRbxModContext Context { get; }

        public LuaTable Metatable { get; set; }

        public Span<LuaValue> UserValues => Span<LuaValue>.Empty;
    }

    /// <summary>Lua userdata for one <c>UiElement</c>: the domain handle plus the mod context that holds it.</summary>
    internal sealed class LuaCsUiElementProxy : ILuaUserData
    {
        public LuaCsUiElementProxy(RbxUiElementHandle element, LuaCsRbxModContext context, LuaTable metatable)
        {
            Element = element;
            Context = context;
            Metatable = metatable;
        }

        public RbxUiElementHandle Element { get; }

        public LuaCsRbxModContext Context { get; }

        public LuaTable Metatable { get; set; }

        public Span<LuaValue> UserValues => Span<LuaValue>.Empty;
    }
}
