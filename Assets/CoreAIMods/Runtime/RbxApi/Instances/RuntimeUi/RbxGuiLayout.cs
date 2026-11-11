using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances.RuntimeUi
{
    /// <summary>
    /// One GUI object of a shown screen as the layout and the presenter see it: the instance, its GUI-object
    /// children in child order, the first modifier of each kind among its children, the box the layout computed
    /// and the element that draws it.
    /// </summary>
    internal sealed class RbxGuiNode
    {
        public RbxGuiNode(RbxGuiBase2d gui, RbxGuiNode parent)
        {
            Gui = gui;
            Parent = parent;
        }

        public RbxGuiBase2d Gui { get; }

        public RbxGuiObject Object => Gui as RbxGuiObject;

        public RbxGuiNode Parent { get; }

        public readonly List<RbxGuiNode> Children = new();

        public RbxUiCorner Corner;
        public RbxUiPadding Padding;
        public RbxUiStroke Stroke;
        public RbxUiScale Scale;
        public RbxUiGridStyleLayout Layout;
        public RbxUiAspectRatioConstraint Aspect;
        public RbxUiSizeConstraint SizeLimit;

        /// <summary>The box in screen pixels (Roblox's AbsolutePosition and AbsoluteSize).</summary>
        public float X;
        public float Y;
        public float W;
        public float H;

        /// <summary>The sum of this object's and its ancestors' rotations, degrees.</summary>
        public float Rotation;

        /// <summary>The area children are laid out in (the box minus padding; the canvas of a ScrollingFrame).</summary>
        public float ContentX;
        public float ContentY;
        public float ContentW;
        public float ContentH;

        /// <summary>The drawn border width and how far it lies outside the box.</summary>
        public float Border;
        public float Outline;

        /// <summary>The element that draws the node; null for the screen root and for a node the budget refused.</summary>
        public IRbxUiElement Element;

        /// <summary>The last value pushed per typed style property.</summary>
        public RbxUiStyleValue[] Pushed;

        public string PushedText;
        public string PushedImage;
        public string PushedTint;
        public string PushedScaleMode;
        public string PushedPicking;
        public string PushedRichText;
        public string PushedPlaceholder;
        public string PushedMultiline;
        public string PushedReadOnly;
        public string PushedFieldColor;
        public string PushedFieldSize;
        public string PushedFieldAlign;
        public bool PushedVisibleSet;
        public bool PushedVisible;
        public bool PushedOrderSet;
        public int PushedOrder;

        public RbxGuiChange Dirty;
        public int Generation;
        public bool Hovered;

        /// <summary>The handlers subscribed to <see cref="Element"/>'s events, kept to unsubscribe them.</summary>
        public Action OnClicked;
        public Action OnEntered;
        public Action OnExited;
        public Action OnFocusGained;
        public Action OnFocusLost;
        public Action OnSubmitted;
        public Action OnValueChanged;
    }

    /// <summary>
    /// The Roblox layout rules, engine-free: <c>Size</c> and <c>Position</c> are scale times the parent's content
    /// size plus offset; <c>AnchorPoint</c> shifts the box by a fraction of its own size; <c>SizeConstraint</c>
    /// picks which parent axis each scale reads; <c>UISizeConstraint</c> and <c>UIAspectRatioConstraint</c>
    /// adjust the size; <c>UIPadding</c> insets the children's area; a <c>UIListLayout</c> or
    /// <c>UIGridLayout</c> sibling places the GUI objects in a row, column or grid instead of their
    /// <c>Position</c>; a <c>ScrollingFrame</c> lays its children out on its canvas, moved by
    /// <c>CanvasPosition</c>; <c>AutomaticSize</c> grows a box to fit its children. Rotation never moves a box
    /// (Roblox's AbsolutePosition ignores it). Results are written to the instances' Absolute* properties.
    /// </summary>
    internal static class RbxGuiLayout
    {
        // WHY [ThreadStatic] scratch: a layout pass runs on the main thread only, and sorting the children of a
        // list must not allocate per frame; the list is cleared before every use.
        [ThreadStatic] private static List<RbxGuiNode> _sortScratch;

        /// <summary>Lays out a whole screen against a viewport of the given size.</summary>
        public static void LayoutScreen(RbxGuiNode root, float width, float height)
        {
            root.X = 0f;
            root.Y = 0f;
            root.W = Math.Max(0f, width);
            root.H = Math.Max(0f, height);
            root.Rotation = 0f;
            root.Gui.ApplyLayout(new RbxVector2(0f, 0f), new RbxVector2(root.W, root.H), 0f);
            LayoutChildren(root);
        }

        /// <summary>Places the children of <paramref name="parent"/>, whose box is already known, and recurses.</summary>
        private static void LayoutChildren(RbxGuiNode parent)
        {
            ResolveContentBox(parent);
            if (parent.Layout is RbxUiListLayout list)
            {
                ArrangeList(parent, list);
            }
            else if (parent.Layout is RbxUiGridLayout grid)
            {
                ArrangeGrid(parent, grid);
            }
            else
            {
                for (int i = 0; i < parent.Children.Count; i++)
                {
                    PlaceFree(parent.Children[i], parent);
                }
            }

            for (int i = 0; i < parent.Children.Count; i++)
            {
                RbxGuiNode child = parent.Children[i];
                LayoutChildren(child);
                if (child.Object != null && child.Object.AutomaticSize != RbxAutomaticSize.None)
                {
                    GrowToContent(child);
                }
            }

            if (parent.Gui is RbxScrollingFrame scrolling)
            {
                scrolling.ApplyCanvasLayout(new RbxVector2(parent.ContentW, parent.ContentH),
                    new RbxVector2(parent.W, parent.H));
            }
        }

        private static void ResolveContentBox(RbxGuiNode node)
        {
            float left = 0f;
            float top = 0f;
            float right = 0f;
            float bottom = 0f;
            if (node.Padding != null)
            {
                left = Resolve(node.Padding.PaddingLeft, node.W);
                right = Resolve(node.Padding.PaddingRight, node.W);
                top = Resolve(node.Padding.PaddingTop, node.H);
                bottom = Resolve(node.Padding.PaddingBottom, node.H);
            }

            node.ContentX = node.X + left;
            node.ContentY = node.Y + top;
            node.ContentW = Math.Max(0f, node.W - left - right);
            node.ContentH = Math.Max(0f, node.H - top - bottom);
            if (node.Gui is RbxScrollingFrame scrolling)
            {
                RbxUDim2 canvas = scrolling.CanvasSize;
                float canvasW = Math.Max(node.ContentW, canvas.X.Scale * node.W + canvas.X.Offset);
                float canvasH = Math.Max(node.ContentH, canvas.Y.Scale * node.H + canvas.Y.Offset);
                if ((scrolling.AutomaticCanvasSize & RbxAutomaticSize.X) != 0
                    || (scrolling.AutomaticCanvasSize & RbxAutomaticSize.Y) != 0)
                {
                    RbxVector2 extent = MeasureChildren(node);
                    if ((scrolling.AutomaticCanvasSize & RbxAutomaticSize.X) != 0)
                    {
                        canvasW = Math.Max(canvasW, extent.X);
                    }

                    if ((scrolling.AutomaticCanvasSize & RbxAutomaticSize.Y) != 0)
                    {
                        canvasH = Math.Max(canvasH, extent.Y);
                    }
                }

                node.ContentX -= scrolling.CanvasPosition.X;
                node.ContentY -= scrolling.CanvasPosition.Y;
                node.ContentW = canvasW;
                node.ContentH = canvasH;
            }
        }

        /// <summary>The size a child takes from the parent's content area, before any layout object moves it.</summary>
        public static RbxVector2 ResolveSize(RbxGuiNode node, float parentW, float parentH)
        {
            RbxGuiObject gui = node.Object;
            RbxUDim2 size = gui.Size;
            float scaleW = parentW;
            float scaleH = parentH;
            switch (gui.SizeConstraint)
            {
                case RbxSizeConstraint.RelativeXX:
                    scaleH = parentW;
                    break;
                case RbxSizeConstraint.RelativeYY:
                    scaleW = parentH;
                    break;
            }

            float w = size.X.Scale * scaleW + size.X.Offset;
            float h = size.Y.Scale * scaleH + size.Y.Offset;
            return Constrain(node, w, h, parentW, parentH);
        }

        private static RbxVector2 Constrain(RbxGuiNode node, float w, float h, float parentW, float parentH)
        {
            if (node.SizeLimit != null)
            {
                RbxVector2 min = node.SizeLimit.MinSize;
                RbxVector2 max = node.SizeLimit.MaxSize;
                w = Math.Max(min.X, Math.Min(max.X, w));
                h = Math.Max(min.Y, Math.Min(max.Y, h));
            }

            if (node.Aspect != null)
            {
                float ratio = node.Aspect.AspectRatio;
                if (node.Aspect.AspectType == RbxAspectType.FitWithinMaxSize)
                {
                    if (h > 0f && w / h > ratio)
                    {
                        w = h * ratio;
                    }
                    else
                    {
                        h = w / ratio;
                    }
                }
                else if (node.Aspect.DominantAxis == RbxDominantAxis.Width)
                {
                    h = w / ratio;
                    if (h > parentH && parentH > 0f)
                    {
                        h = parentH;
                        w = h * ratio;
                    }
                }
                else
                {
                    w = h * ratio;
                    if (w > parentW && parentW > 0f)
                    {
                        w = parentW;
                        h = w / ratio;
                    }
                }
            }

            return new RbxVector2(w, h);
        }

        private static void PlaceFree(RbxGuiNode child, RbxGuiNode parent)
        {
            RbxGuiObject gui = child.Object;
            RbxVector2 size = ResolveSize(child, parent.ContentW, parent.ContentH);
            RbxUDim2 position = gui.Position;
            float x = parent.ContentX + position.X.Scale * parent.ContentW + position.X.Offset
                      - gui.AnchorPoint.X * size.X;
            float y = parent.ContentY + position.Y.Scale * parent.ContentH + position.Y.Offset
                      - gui.AnchorPoint.Y * size.Y;
            SetBox(child, parent, x, y, size.X, size.Y);
        }

        private static void SetBox(RbxGuiNode node, RbxGuiNode parent, float x, float y, float w, float h)
        {
            node.X = x;
            node.Y = y;
            node.W = w;
            node.H = h;
            node.Rotation = parent.Rotation + node.Object.Rotation;
            node.Gui.ApplyLayout(new RbxVector2(x, y), new RbxVector2(w, h), node.Rotation);
        }

        private static void ArrangeList(RbxGuiNode parent, RbxUiListLayout list)
        {
            List<RbxGuiNode> items = SortedVisible(parent, list.SortOrder);
            bool vertical = list.FillDirection == RbxFillDirection.Vertical;
            float axisLength = vertical ? parent.ContentH : parent.ContentW;
            float crossLength = vertical ? parent.ContentW : parent.ContentH;
            float padding = Resolve(list.Padding, axisLength);
            float total = 0f;
            float widest = 0f;
            for (int i = 0; i < items.Count; i++)
            {
                RbxGuiNode item = items[i];
                RbxVector2 size = ResolveSize(item, parent.ContentW, parent.ContentH);
                item.W = size.X;
                item.H = size.Y;
                total += vertical ? size.Y : size.X;
                widest = Math.Max(widest, vertical ? size.X : size.Y);
            }

            if (items.Count > 1)
            {
                total += padding * (items.Count - 1);
            }

            float start = vertical
                ? Align(list.VerticalAlignment == RbxVerticalAlignment.Top ? 0 :
                    list.VerticalAlignment == RbxVerticalAlignment.Center ? 1 : 2, axisLength, total)
                : Align(list.HorizontalAlignment == RbxHorizontalAlignment.Left ? 0 :
                    list.HorizontalAlignment == RbxHorizontalAlignment.Center ? 1 : 2, axisLength, total);
            float cursor = start;
            for (int i = 0; i < items.Count; i++)
            {
                RbxGuiNode item = items[i];
                float along = vertical ? item.H : item.W;
                float across = vertical ? item.W : item.H;
                float crossOffset = vertical
                    ? Align(list.HorizontalAlignment == RbxHorizontalAlignment.Left ? 0 :
                        list.HorizontalAlignment == RbxHorizontalAlignment.Center ? 1 : 2, crossLength, across)
                    : Align(list.VerticalAlignment == RbxVerticalAlignment.Top ? 0 :
                        list.VerticalAlignment == RbxVerticalAlignment.Center ? 1 : 2, crossLength, across);
                float x = vertical ? parent.ContentX + crossOffset : parent.ContentX + cursor;
                float y = vertical ? parent.ContentY + cursor : parent.ContentY + crossOffset;
                SetBox(item, parent, x, y, item.W, item.H);
                cursor += along + padding;
            }

            PlaceHidden(parent);
            list.ApplyContentSize(vertical ? new RbxVector2(widest, total) : new RbxVector2(total, widest));
            items.Clear();
        }

        private static void ArrangeGrid(RbxGuiNode parent, RbxUiGridLayout grid)
        {
            List<RbxGuiNode> items = SortedVisible(parent, grid.SortOrder);
            RbxUDim2 cellSize = grid.CellSize;
            RbxUDim2 cellPadding = grid.CellPadding;
            float cellW = cellSize.X.Scale * parent.ContentW + cellSize.X.Offset;
            float cellH = cellSize.Y.Scale * parent.ContentH + cellSize.Y.Offset;
            float padX = cellPadding.X.Scale * parent.ContentW + cellPadding.X.Offset;
            float padY = cellPadding.Y.Scale * parent.ContentH + cellPadding.Y.Offset;
            bool horizontal = grid.FillDirection == RbxFillDirection.Horizontal;
            float lineLength = horizontal ? parent.ContentW : parent.ContentH;
            float stepAlong = horizontal ? cellW + padX : cellH + padY;
            int fit = stepAlong > 0f ? (int)Math.Floor((lineLength + (horizontal ? padX : padY)) / stepAlong) : 1;
            fit = Math.Max(1, fit);
            int perLine = grid.FillDirectionMaxCells > 0 ? Math.Min(grid.FillDirectionMaxCells, fit) : fit;
            int count = items.Count;
            int lines = count == 0 ? 0 : (count + perLine - 1) / perLine;
            int columns = horizontal ? Math.Min(count, perLine) : lines;
            int rows = horizontal ? lines : Math.Min(count, perLine);
            float blockW = columns > 0 ? columns * cellW + (columns - 1) * padX : 0f;
            float blockH = rows > 0 ? rows * cellH + (rows - 1) * padY : 0f;
            float originX = parent.ContentX + Align(grid.HorizontalAlignment == RbxHorizontalAlignment.Left ? 0 :
                grid.HorizontalAlignment == RbxHorizontalAlignment.Center ? 1 : 2, parent.ContentW, blockW);
            float originY = parent.ContentY + Align(grid.VerticalAlignment == RbxVerticalAlignment.Top ? 0 :
                grid.VerticalAlignment == RbxVerticalAlignment.Center ? 1 : 2, parent.ContentH, blockH);
            bool fromRight = grid.StartCorner == RbxStartCorner.TopRight || grid.StartCorner == RbxStartCorner.BottomRight;
            bool fromBottom = grid.StartCorner == RbxStartCorner.BottomLeft
                              || grid.StartCorner == RbxStartCorner.BottomRight;
            for (int i = 0; i < count; i++)
            {
                int along = i % perLine;
                int line = i / perLine;
                int column = horizontal ? along : line;
                int row = horizontal ? line : along;
                if (fromRight)
                {
                    column = columns - 1 - column;
                }

                if (fromBottom)
                {
                    row = rows - 1 - row;
                }

                RbxGuiNode item = items[i];
                RbxVector2 size = Constrain(item, cellW, cellH, parent.ContentW, parent.ContentH);
                SetBox(item, parent, originX + column * (cellW + padX), originY + row * (cellH + padY), size.X, size.Y);
            }

            PlaceHidden(parent);
            grid.ApplyContentSize(new RbxVector2(blockW, blockH));
            grid.ApplyCells(new RbxVector2(cellW, cellH), new RbxVector2(columns, rows));
            items.Clear();
        }

        /// <summary>Invisible children take no room in a layout; they keep a free placement of their own.</summary>
        private static void PlaceHidden(RbxGuiNode parent)
        {
            for (int i = 0; i < parent.Children.Count; i++)
            {
                RbxGuiNode child = parent.Children[i];
                if (!child.Object.Visible)
                {
                    PlaceFree(child, parent);
                }
            }
        }

        /// <summary>The visible GUI-object children in layout order: LayoutOrder (then child order) or Name.</summary>
        private static List<RbxGuiNode> SortedVisible(RbxGuiNode parent, RbxGuiSortOrder order)
        {
            List<RbxGuiNode> items = _sortScratch;
            _sortScratch = null;
            items ??= new List<RbxGuiNode>();
            items.Clear();
            for (int i = 0; i < parent.Children.Count; i++)
            {
                if (parent.Children[i].Object.Visible)
                {
                    items.Add(parent.Children[i]);
                }
            }

            for (int i = 1; i < items.Count; i++)
            {
                RbxGuiNode current = items[i];
                int j = i - 1;
                while (j >= 0 && Compare(items[j], current, order) > 0)
                {
                    items[j + 1] = items[j];
                    j--;
                }

                items[j + 1] = current;
            }

            _sortScratch = items;
            return items;
        }

        private static int Compare(RbxGuiNode a, RbxGuiNode b, RbxGuiSortOrder order)
        {
            switch (order)
            {
                case RbxGuiSortOrder.LayoutOrder:
                    return a.Object.LayoutOrder.CompareTo(b.Object.LayoutOrder);
                case RbxGuiSortOrder.Name:
                    return string.CompareOrdinal(a.Gui.Name, b.Gui.Name);
                default:
                    return 0;
            }
        }

        /// <summary>Grows a box with AutomaticSize to hold its children, keeping its anchor point where it was.</summary>
        private static void GrowToContent(RbxGuiNode node)
        {
            RbxVector2 extent = MeasureChildren(node);
            RbxAutomaticSize mode = node.Object.AutomaticSize;
            float paddingW = node.W - node.ContentW;
            float paddingH = node.H - node.ContentH;
            float w = (mode & RbxAutomaticSize.X) != 0 ? Math.Max(node.W, extent.X + paddingW) : node.W;
            float h = (mode & RbxAutomaticSize.Y) != 0 ? Math.Max(node.H, extent.Y + paddingH) : node.H;
            if (w.Equals(node.W) && h.Equals(node.H))
            {
                return;
            }

            RbxVector2 anchor = node.Object.AnchorPoint;
            bool laidOut = node.Parent?.Layout != null;
            float x = laidOut ? node.X : node.X - anchor.X * (w - node.W);
            float y = laidOut ? node.Y : node.Y - anchor.Y * (h - node.H);
            SetBox(node, node.Parent, x, y, w, h);
            LayoutChildren(node);
        }

        /// <summary>How far the children reach from the content origin (the arranged content of a layout).</summary>
        private static RbxVector2 MeasureChildren(RbxGuiNode node)
        {
            if (node.Layout != null && node.Layout.IsDestroyed == false)
            {
                RbxVector2 content = node.Layout.LaidOutContentSize;
                if (content.X > 0f || content.Y > 0f)
                {
                    return content;
                }
            }

            float right = 0f;
            float bottom = 0f;
            for (int i = 0; i < node.Children.Count; i++)
            {
                RbxGuiNode child = node.Children[i];
                if (!child.Object.Visible)
                {
                    continue;
                }

                RbxUDim2 position = child.Object.Position;
                RbxUDim2 size = child.Object.Size;
                right = Math.Max(right, position.X.Offset + Math.Max(0f, (float)size.X.Offset));
                bottom = Math.Max(bottom, position.Y.Offset + Math.Max(0f, (float)size.Y.Offset));
            }

            return new RbxVector2(right, bottom);
        }

        private static float Align(int mode, float available, float used)
        {
            switch (mode)
            {
                case 1:
                    return (available - used) * 0.5f;
                case 2:
                    return available - used;
                default:
                    return 0f;
            }
        }

        private static float Resolve(RbxUDim value, float reference)
        {
            return value.Scale * reference + value.Offset;
        }
    }
}
