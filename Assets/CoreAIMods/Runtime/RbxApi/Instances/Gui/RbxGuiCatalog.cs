namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// The class rows of the Roblox GUI classes (MVP15, the solo half) and the loud stubs of the members they do
    /// not answer. The hierarchy follows <c>Full-API-Dump.json</c>: GuiBase, GuiBase2d, LayerCollector, GuiObject,
    /// GuiLabel, GuiButton, UIBase, UIComponent, UILayout, UIGridStyleLayout and UIConstraint are abstract.
    /// </summary>
    internal static class RbxGuiCatalog
    {
        private const string Phase = "MVP15";

        public static void Register(ClassCatalog catalog)
        {
            catalog.Register(new ClassDescriptor("GuiBase", "Instance", true, false, false));
            catalog.Register(new ClassDescriptor("GuiBase2d", "GuiBase", true, false, false));
            // WHY the 3D adornments here: GuiBase3d derives from GuiBase in the mirror, so they share the GUI root,
            // but they are drawn in the world by the binder rather than by the runtime UI host.
            catalog.Register(new ClassDescriptor("GuiBase3d", "GuiBase", true, false, false));
            catalog.Register(new ClassDescriptor("InstanceAdornment", "GuiBase3d", true, false, false));
            catalog.Register(new ClassDescriptor("SelectionBox", "InstanceAdornment", false, true, false,
                descriptor => new CoreAI.Mods.Rbx.Instances.Adornments.RbxSelectionBox(descriptor)));
            // WHY beside SelectionBox although Highlight derives from Instance: both are world adornments drawn by the
            // same layer, and their members are answered by the same binding table.
            catalog.Register(new ClassDescriptor("Highlight", "Instance", false, true, false,
                descriptor => new CoreAI.Mods.Rbx.Instances.Adornments.RbxHighlight(descriptor)));
            catalog.Register(new ClassDescriptor("LayerCollector", "GuiBase2d", true, false, false));
            catalog.Register(new ClassDescriptor("ScreenGui", "LayerCollector", false, true, false,
                descriptor => new RbxScreenGui(descriptor)));
            catalog.Register(new ClassDescriptor("GuiObject", "GuiBase2d", true, false, false));
            catalog.Register(new ClassDescriptor("Frame", "GuiObject", false, true, false,
                descriptor => new RbxFrame(descriptor)));
            catalog.Register(new ClassDescriptor("GuiLabel", "GuiObject", true, false, false));
            catalog.Register(new ClassDescriptor("TextLabel", "GuiLabel", false, true, false,
                descriptor => new RbxTextLabel(descriptor)));
            catalog.Register(new ClassDescriptor("ImageLabel", "GuiLabel", false, true, false,
                descriptor => new RbxImageLabel(descriptor)));
            catalog.Register(new ClassDescriptor("GuiButton", "GuiObject", true, false, false));
            catalog.Register(new ClassDescriptor("TextButton", "GuiButton", false, true, false,
                descriptor => new RbxTextButton(descriptor)));
            catalog.Register(new ClassDescriptor("ImageButton", "GuiButton", false, true, false,
                descriptor => new RbxImageButton(descriptor)));
            catalog.Register(new ClassDescriptor("TextBox", "GuiObject", false, true, false,
                descriptor => new RbxTextBox(descriptor)));
            catalog.Register(new ClassDescriptor("ScrollingFrame", "GuiObject", false, true, false,
                descriptor => new RbxScrollingFrame(descriptor)));
            catalog.Register(new ClassDescriptor("UIBase", "Instance", true, false, false));
            catalog.Register(new ClassDescriptor("UIComponent", "UIBase", true, false, false));
            catalog.Register(new ClassDescriptor("UICorner", "UIComponent", false, true, false,
                descriptor => new RbxUiCorner(descriptor)));
            catalog.Register(new ClassDescriptor("UIPadding", "UIComponent", false, true, false,
                descriptor => new RbxUiPadding(descriptor)));
            catalog.Register(new ClassDescriptor("UIStroke", "UIComponent", false, true, false,
                descriptor => new RbxUiStroke(descriptor)));
            catalog.Register(new ClassDescriptor("UIScale", "UIComponent", false, true, false,
                descriptor => new RbxUiScale(descriptor)));
            catalog.Register(new ClassDescriptor("UILayout", "UIComponent", true, false, false));
            catalog.Register(new ClassDescriptor("UIGridStyleLayout", "UILayout", true, false, false));
            catalog.Register(new ClassDescriptor("UIListLayout", "UIGridStyleLayout", false, true, false,
                descriptor => new RbxUiListLayout(descriptor)));
            catalog.Register(new ClassDescriptor("UIGridLayout", "UIGridStyleLayout", false, true, false,
                descriptor => new RbxUiGridLayout(descriptor)));
            catalog.Register(new ClassDescriptor("UIConstraint", "UIComponent", true, false, false));
            catalog.Register(new ClassDescriptor("UIAspectRatioConstraint", "UIConstraint", false, true, false,
                descriptor => new RbxUiAspectRatioConstraint(descriptor)));
            catalog.Register(new ClassDescriptor("UISizeConstraint", "UIConstraint", false, true, false,
                descriptor => new RbxUiSizeConstraint(descriptor)));
            catalog.Register(new ClassDescriptor("StarterGui", "BasePlayerGui", false, false, true,
                descriptor => new RbxStarterGui(descriptor)));
            RegisterKnownUnimplementedMembers(catalog);
        }

        private static void RegisterKnownUnimplementedMembers(ClassCatalog catalog)
        {
            const string selection =
                "gamepad selection is not modeled; drive focus with TextBox:CaptureFocus and clicks";
            const string input =
                "listen to UserInputService.InputBegan (gameProcessedEvent is true over GUI) or to MouseEnter, "
                + "MouseLeave and MouseButton1Click";
            const string touch = "touch gestures land with the touch slice; use MouseButton1Click or Activated";
            const string tween =
                "use TweenService:Create(object, TweenInfo.new(time, Enum.EasingStyle.Quad), {Position = goal}):Play()";
            const string measure = "the host measures no text for scripts yet; size the box yourself or use TextScaled";
            const string haptics = "haptics are a platform concern CoreAI does not model";
            const string content = "use the Image property with a registered image key";
            catalog.RegisterKnownUnimplementedMembers("GuiBase2d",
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("SelectionBehaviorDown", selection),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("SelectionBehaviorLeft", selection),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("SelectionBehaviorRight", selection),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("SelectionBehaviorUp", selection),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("SelectionGroup", selection),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("SelectionChanged", selection),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty("RootLocalizationTable",
                    "CoreAI does not localize GUI text; keep translated strings in a table keyed by locale"));
            catalog.RegisterKnownUnimplementedMembers("GuiObject",
                RbxKnownUnimplementedMemberDescriptor.PlannedProperty("InputBegan", Phase, input),
                RbxKnownUnimplementedMemberDescriptor.PlannedProperty("InputChanged", Phase, input),
                RbxKnownUnimplementedMemberDescriptor.PlannedProperty("InputEnded", Phase, input),
                RbxKnownUnimplementedMemberDescriptor.PlannedProperty("MouseMoved", Phase, input),
                RbxKnownUnimplementedMemberDescriptor.PlannedProperty("MouseWheelForward", Phase, input),
                RbxKnownUnimplementedMemberDescriptor.PlannedProperty("MouseWheelBackward", Phase, input),
                RbxKnownUnimplementedMemberDescriptor.PlannedProperty("TouchTap", Phase, touch),
                RbxKnownUnimplementedMemberDescriptor.PlannedProperty("TouchLongPress", Phase, touch),
                RbxKnownUnimplementedMemberDescriptor.PlannedProperty("TouchPan", Phase, touch),
                RbxKnownUnimplementedMemberDescriptor.PlannedProperty("TouchPinch", Phase, touch),
                RbxKnownUnimplementedMemberDescriptor.PlannedProperty("TouchRotate", Phase, touch),
                RbxKnownUnimplementedMemberDescriptor.PlannedProperty("TouchSwipe", Phase, touch),
                RbxKnownUnimplementedMemberDescriptor.PlannedMethod("TweenPosition", Phase, tween),
                RbxKnownUnimplementedMemberDescriptor.PlannedMethod("TweenSize", Phase, tween),
                RbxKnownUnimplementedMemberDescriptor.PlannedMethod("TweenSizeAndPosition", Phase, tween),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("GuiState",
                    "track hover yourself with MouseEnter and MouseLeave"),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("InputSink",
                    "Active = true already makes the object take the pointer"),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("NextSelectionDown", selection),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("NextSelectionLeft", selection),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("NextSelectionRight", selection),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("NextSelectionUp", selection),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("SelectionImageObject", selection),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("SelectionOrder", selection),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("SelectionGained", selection),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("SelectionLost", selection));
            catalog.RegisterKnownUnimplementedMembers("GuiButton",
                RbxKnownUnimplementedMemberDescriptor.PlannedProperty("MouseButton1Down", Phase,
                    "connect MouseButton1Click or Activated, which fire on release"),
                RbxKnownUnimplementedMemberDescriptor.PlannedProperty("MouseButton1Up", Phase,
                    "connect MouseButton1Click or Activated, which fire on release"),
                RbxKnownUnimplementedMemberDescriptor.PlannedProperty("MouseButton2Click", Phase, input),
                RbxKnownUnimplementedMemberDescriptor.PlannedProperty("MouseButton2Down", Phase, input),
                RbxKnownUnimplementedMemberDescriptor.PlannedProperty("MouseButton2Up", Phase, input),
                RbxKnownUnimplementedMemberDescriptor.PlannedProperty("SecondaryActivated", Phase, input),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty("HoverHapticEffect", haptics),
                RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty("PressHapticEffect", haptics));
            string[] textClasses = { "TextLabel", "TextButton", "TextBox" };
            for (int i = 0; i < textClasses.Length; i++)
            {
                catalog.RegisterKnownUnimplementedMembers(textClasses[i],
                    RbxKnownUnimplementedMemberDescriptor.PlannedProperty("TextBounds", Phase, measure),
                    RbxKnownUnimplementedMemberDescriptor.PlannedProperty("TextFits", Phase, measure),
                    RbxKnownUnimplementedMemberDescriptor.BacklogProperty("FontFace",
                        "set Font = Enum.Font.GothamBold (weight and italic are drawn; the family is the host's)"),
                    RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty("OpenTypeFeatures",
                        "OpenType features are not drawn"),
                    RbxKnownUnimplementedMemberDescriptor.UnsupportedProperty("OpenTypeFeaturesError",
                        "OpenType features are not drawn"));
            }

            catalog.RegisterKnownUnimplementedMembers("TextBox",
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("ReturnPressedFromOnScreenKeyboard",
                    "connect FocusLost and read its enterPressed argument"),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("SelectionStart",
                    "the host field keeps its own selection; read Text"));
            string[] imageClasses = { "ImageLabel", "ImageButton" };
            for (int i = 0; i < imageClasses.Length; i++)
            {
                catalog.RegisterKnownUnimplementedMembers(imageClasses[i],
                    RbxKnownUnimplementedMemberDescriptor.BacklogProperty("ImageContent", content),
                    RbxKnownUnimplementedMemberDescriptor.BacklogProperty("SliceCenter",
                        "nine-slice is not drawn yet; the image is stretched"));
            }

            catalog.RegisterKnownUnimplementedMembers("ImageButton",
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("HoverImageContent", content),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("PressedImageContent", content));
            const string bar =
                "the scroll bar is a flat bar; color it with ScrollBarImageColor3 and ScrollBarImageTransparency";
            catalog.RegisterKnownUnimplementedMembers("ScrollingFrame",
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod("GetScrollVelocity",
                    "compare CanvasPosition between frames"),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod("ResetScrollVelocity",
                    "set CanvasPosition to stop where you want"),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("ElasticBehavior",
                    "the canvas always stops at its ends (as with Enum.ElasticBehavior.Never); leave this property out"),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("TopImage", bar),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("MidImage", bar),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("BottomImage", bar),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("TopImageContent", bar),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("MidImageContent", bar),
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("BottomImageContent", bar));
            catalog.RegisterKnownUnimplementedMembers("BasePlayerGui",
                RbxKnownUnimplementedMemberDescriptor.PlannedMethod("GetGuiObjectsAtPosition", Phase,
                    "compare the point with each object's AbsolutePosition and AbsoluteSize"));
            catalog.RegisterKnownUnimplementedMembers("PlayerGui",
                RbxKnownUnimplementedMemberDescriptor.BacklogProperty("SelectionImageObject", selection));
            catalog.RegisterKnownUnimplementedMembers("StarterGui",
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod("SetCore",
                    "CoreAI draws no Roblox core GUI; show your own notification with a ScreenGui and TextLabel"),
                RbxKnownUnimplementedMemberDescriptor.BacklogMethod("GetCore",
                    "CoreAI draws no Roblox core GUI; keep the state you need yourself"));
        }
    }
}
