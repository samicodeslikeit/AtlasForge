using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AtlasForge.Editor
{
    /// <summary>
    /// Selection-panel editor that moves and resizes an atlas slice's rect inside its atlas,
    /// Sprite Editor style: drag an edge or corner to resize, or the inside to move. Edits
    /// re-cut the slice immediately and are written to the atlas by Repack or Merge.
    /// </summary>
    public sealed partial class AtlasForgeWindow
    {
        private static readonly string[] SliceEditModeLabels = { "Border", "Rect" };

        // Which sides a rect drag moves; Move shifts the whole rect.
        private const int DragLeft = 1;
        private const int DragRight = 2;
        private const int DragBottom = 4;
        private const int DragTop = 8;
        private const int DragMove = 16;

        private const float RectHandleSize = 7f;
        private const float RectEdgeHitSize = 5f;

        private static readonly Color OtherSliceColor = new(1f, 1f, 1f, 0.3f);
        private static Color ViewportBackgroundColor => EditorGUIUtility.isProSkin ? new Color(0.12f, 0.12f, 0.12f) : new Color(0.55f, 0.55f, 0.55f);

        [SerializeField] private bool _editSliceRect;

        // Rect drag state: the atlas region on screen is frozen while dragging so it doesn't jump.
        private bool _rectDragging;
        private int _rectDragMode;
        private RectInt _rectDragStart;
        private RectInt _rectDragCurrent;
        private RectInt _rectView;
        private Vector2 _rectDragMouseStart;

        private void DrawSliceRectEditor(SpriteBorderConfig config, AtlasSlices slices, int index)
        {
            RectInt current = _rectDragging ? _rectDragCurrent : slices.Rects[index];
            RectInt region = _rectDragging ? _rectView : ViewRegion(slices, index, current);

            using (new EditorGUILayout.HorizontalScope())
            {
                float maxWidth = Mathf.Max(BorderPreviewSize, position.width - 320f);
                float height = Mathf.Clamp(_inspectorHeight - 48f, BorderPreviewSize, MaxEditablePreviewSize);
                float width = Mathf.Clamp(height * region.width / region.height, BorderPreviewSize, maxWidth);
                Rect bounds = GUILayoutUtility.GetRect(width, height, GUILayout.Width(width), GUILayout.Height(height));
                DrawSliceViewport(bounds, config, slices, index, region, current);
                GUILayout.Space(8f);

                using (new EditorGUILayout.VerticalScope())
                {
                    float labelWidth = EditorGUIUtility.labelWidth;
                    EditorGUIUtility.labelWidth = 110f;
                    EditorGUI.BeginChangeCheck();
                    int x = EditorGUILayout.IntField("X", current.x);
                    int y = EditorGUILayout.IntField("Y", current.y);
                    int w = EditorGUILayout.IntField("Width", current.width);
                    int h = EditorGUILayout.IntField("Height", current.height);
                    if (EditorGUI.EndChangeCheck())
                    {
                        ActiveSliceSet.SetSliceRect(config, new RectInt(x, y, w, h));
                    }

                    EditorGUIUtility.labelWidth = labelWidth;

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.FlexibleSpace();
                        using (new EditorGUI.DisabledScope(!slices.IsRectEdited(index)))
                        {
                            if (GUILayout.Button(new GUIContent("Reset", "Back to the slice's rect in the atlas file."), GUILayout.Width(80f)))
                            {
                                ActiveSliceSet.SetSliceRect(config, slices.SourceRects[index]);
                                GUI.FocusControl(null);
                            }
                        }
                    }

                    string overlaps = OverlappingSlices(slices, index, current);
                    if (overlaps != null)
                    {
                        EditorGUILayout.HelpBox($"Overlaps {overlaps}.", MessageType.Warning);
                    }

                    string action = _tab == WindowTab.Merge ? "Merge Atlases" : "Repack Atlas";
                    GUILayout.Label(
                        "Drag an edge or corner to resize, or inside the rect to move it. Coordinates are atlas pixels " +
                        $"from the bottom-left. Changes are written when you click {action}.",
                        EditorStyles.wordWrappedMiniLabel);
                }
            }
        }

        /// <summary>The part of the atlas to show: the slice's original and current rects plus a margin of context.</summary>
        private static RectInt ViewRegion(AtlasSlices slices, int index, RectInt current)
        {
            RectInt original = slices.SourceRects[index];
            int xMin = Mathf.Min(original.xMin, current.xMin);
            int yMin = Mathf.Min(original.yMin, current.yMin);
            int xMax = Mathf.Max(original.xMax, current.xMax);
            int yMax = Mathf.Max(original.yMax, current.yMax);
            int margin = Mathf.Max(16, Mathf.Max(xMax - xMin, yMax - yMin) / 2);
            xMin = Mathf.Max(0, xMin - margin);
            yMin = Mathf.Max(0, yMin - margin);
            xMax = Mathf.Min(slices.Width, xMax + margin);
            yMax = Mathf.Min(slices.Height, yMax + margin);
            return new RectInt(xMin, yMin, Mathf.Max(1, xMax - xMin), Mathf.Max(1, yMax - yMin));
        }

        /// <summary>Draws the atlas region with every slice outlined, and handles dragging the edited slice's rect.</summary>
        private void DrawSliceViewport(Rect bounds, SpriteBorderConfig config, AtlasSlices slices, int index, RectInt region, RectInt current)
        {
            float scale = Mathf.Min(bounds.width / region.width, bounds.height / region.height);
            var area = new Rect(
                bounds.x + (bounds.width - region.width * scale) * 0.5f,
                bounds.y + (bounds.height - region.height * scale) * 0.5f,
                region.width * scale,
                region.height * scale);

            // Atlas pixels (bottom-up) to GUI space (top-down).
            float GuiX(float px) => area.x + (px - region.x) * scale;
            float GuiY(float py) => area.yMax - (py - region.y) * scale;
            Rect ToGui(RectInt rect) => Rect.MinMaxRect(GuiX(rect.xMin), GuiY(rect.yMax), GuiX(rect.xMax), GuiY(rect.yMin));

            Rect selection = ToGui(current);
            int controlId = GUIUtility.GetControlID(FocusType.Passive, bounds);
            AddRectCursors(selection, controlId);

            Event currentEvent = Event.current;
            switch (currentEvent.GetTypeForControl(controlId))
            {
                case EventType.MouseDown when currentEvent.button == 0 && area.Contains(currentEvent.mousePosition):
                    int mode = RectDragMode(selection, currentEvent.mousePosition);
                    if (mode != 0)
                    {
                        _rectDragging = true;
                        _rectDragMode = mode;
                        _rectDragStart = current;
                        _rectDragCurrent = current;
                        _rectView = region;
                        _rectDragMouseStart = currentEvent.mousePosition;
                        GUIUtility.hotControl = controlId;
                        currentEvent.Use();
                    }

                    break;
                case EventType.MouseDrag when GUIUtility.hotControl == controlId && _rectDragging:
                    Vector2 delta = (currentEvent.mousePosition - _rectDragMouseStart) / scale;
                    _rectDragCurrent = DraggedRect(slices, _rectDragStart, _rectDragMode, Mathf.RoundToInt(delta.x), -Mathf.RoundToInt(delta.y));
                    currentEvent.Use();
                    Repaint();
                    break;
                case EventType.MouseUp when GUIUtility.hotControl == controlId:
                    GUIUtility.hotControl = 0;
                    if (_rectDragging)
                    {
                        _rectDragging = false;
                        ActiveSliceSet.SetSliceRect(config, _rectDragCurrent);
                    }

                    currentEvent.Use();
                    Repaint();
                    break;
                case EventType.Repaint:
                    EditorGUI.DrawRect(bounds, ViewportBackgroundColor);
                    var uv = new Rect(
                        (float)region.x / slices.Width, (float)region.y / slices.Height,
                        (float)region.width / slices.Width, (float)region.height / slices.Height);
                    GUI.DrawTextureWithTexCoords(area, slices.Source, uv, true);

                    GUI.BeginClip(area);
                    var offset = new Vector2(area.x, area.y);
                    for (int i = 0; i < slices.Rects.Count; i++)
                    {
                        if (i != index && slices.Rects[i].Overlaps(region))
                        {
                            Rect other = ToGui(slices.Rects[i]);
                            other.position -= offset;
                            DrawOutline(other, OtherSliceColor, 1f);
                        }
                    }

                    GUI.EndClip();

                    DrawOutline(selection, FocusOutlineColor, 1f);
                    foreach (Vector2 handle in RectHandles(selection))
                    {
                        EditorGUI.DrawRect(
                            new Rect(handle.x - RectHandleSize * 0.5f, handle.y - RectHandleSize * 0.5f, RectHandleSize, RectHandleSize),
                            FocusOutlineColor);
                    }

                    if (_rectDragging)
                    {
                        var label = new GUIContent($"{current.x}, {current.y}  ·  {current.width}×{current.height}");
                        Vector2 size = EditorStyles.helpBox.CalcSize(label);
                        GUI.Label(new Rect(bounds.x + 4f, bounds.y + 4f, size.x, size.y), label, EditorStyles.helpBox);
                    }

                    break;
            }
        }

        /// <summary>Corner and edge-midpoint grips of a rect in GUI space.</summary>
        private static IEnumerable<Vector2> RectHandles(Rect rect)
        {
            yield return new Vector2(rect.xMin, rect.yMin);
            yield return new Vector2(rect.center.x, rect.yMin);
            yield return new Vector2(rect.xMax, rect.yMin);
            yield return new Vector2(rect.xMax, rect.center.y);
            yield return new Vector2(rect.xMax, rect.yMax);
            yield return new Vector2(rect.center.x, rect.yMax);
            yield return new Vector2(rect.xMin, rect.yMax);
            yield return new Vector2(rect.xMin, rect.center.y);
        }

        /// <summary>
        /// What a press at the mouse position drags: the sides near it (corners take two), the
        /// whole rect when inside, or 0 for nothing. GUI top is the rect's atlas top.
        /// </summary>
        private static int RectDragMode(Rect rect, Vector2 mouse)
        {
            Rect grab = new Rect(rect.x - RectEdgeHitSize, rect.y - RectEdgeHitSize, rect.width + RectEdgeHitSize * 2f, rect.height + RectEdgeHitSize * 2f);
            if (!grab.Contains(mouse))
            {
                return 0;
            }

            int mode = 0;
            if (Mathf.Abs(mouse.x - rect.xMin) <= RectEdgeHitSize)
            {
                mode |= DragLeft;
            }
            else if (Mathf.Abs(mouse.x - rect.xMax) <= RectEdgeHitSize)
            {
                mode |= DragRight;
            }

            if (Mathf.Abs(mouse.y - rect.yMin) <= RectEdgeHitSize)
            {
                mode |= DragTop;
            }
            else if (Mathf.Abs(mouse.y - rect.yMax) <= RectEdgeHitSize)
            {
                mode |= DragBottom;
            }

            return mode != 0 ? mode : rect.Contains(mouse) ? DragMove : 0;
        }

        /// <summary>The start rect with the dragged sides (or the whole rect) shifted by whole atlas pixels.</summary>
        private static RectInt DraggedRect(AtlasSlices slices, RectInt start, int mode, int dx, int dy)
        {
            if (mode == DragMove)
            {
                int x = Mathf.Clamp(start.x + dx, 0, slices.Width - start.width);
                int y = Mathf.Clamp(start.y + dy, 0, slices.Height - start.height);
                return new RectInt(x, y, start.width, start.height);
            }

            int xMin = start.xMin;
            int xMax = start.xMax;
            int yMin = start.yMin;
            int yMax = start.yMax;
            if ((mode & DragLeft) != 0)
            {
                xMin = Mathf.Clamp(xMin + dx, 0, xMax - 1);
            }

            if ((mode & DragRight) != 0)
            {
                xMax = Mathf.Clamp(xMax + dx, xMin + 1, slices.Width);
            }

            if ((mode & DragBottom) != 0)
            {
                yMin = Mathf.Clamp(yMin + dy, 0, yMax - 1);
            }

            if ((mode & DragTop) != 0)
            {
                yMax = Mathf.Clamp(yMax + dy, yMin + 1, slices.Height);
            }

            return new RectInt(xMin, yMin, xMax - xMin, yMax - yMin);
        }

        /// <summary>Resize cursors over the rect's edges and corners, and a move cursor inside it.</summary>
        private static void AddRectCursors(Rect rect, int controlId)
        {
            float e = RectEdgeHitSize;
            EditorGUIUtility.AddCursorRect(new Rect(rect.x + e, rect.y + e, rect.width - e * 2f, rect.height - e * 2f), MouseCursor.MoveArrow, controlId);
            EditorGUIUtility.AddCursorRect(new Rect(rect.xMin - e, rect.y + e, e * 2f, rect.height - e * 2f), MouseCursor.ResizeHorizontal, controlId);
            EditorGUIUtility.AddCursorRect(new Rect(rect.xMax - e, rect.y + e, e * 2f, rect.height - e * 2f), MouseCursor.ResizeHorizontal, controlId);
            EditorGUIUtility.AddCursorRect(new Rect(rect.x + e, rect.yMin - e, rect.width - e * 2f, e * 2f), MouseCursor.ResizeVertical, controlId);
            EditorGUIUtility.AddCursorRect(new Rect(rect.x + e, rect.yMax - e, rect.width - e * 2f, e * 2f), MouseCursor.ResizeVertical, controlId);
            EditorGUIUtility.AddCursorRect(new Rect(rect.xMin - e, rect.yMin - e, e * 2f, e * 2f), MouseCursor.ResizeUpLeft, controlId);
            EditorGUIUtility.AddCursorRect(new Rect(rect.xMax - e, rect.yMax - e, e * 2f, e * 2f), MouseCursor.ResizeUpLeft, controlId);
            EditorGUIUtility.AddCursorRect(new Rect(rect.xMax - e, rect.yMin - e, e * 2f, e * 2f), MouseCursor.ResizeUpRight, controlId);
            EditorGUIUtility.AddCursorRect(new Rect(rect.xMin - e, rect.yMax - e, e * 2f, e * 2f), MouseCursor.ResizeUpRight, controlId);
        }

        /// <summary>Names of up to two other slices the rect overlaps (plus a count of the rest), or null.</summary>
        private static string OverlappingSlices(AtlasSlices slices, int index, RectInt rect)
        {
            var names = new List<string>();
            int count = 0;
            for (int i = 0; i < slices.Rects.Count; i++)
            {
                if (i == index || !slices.Rects[i].Overlaps(rect))
                {
                    continue;
                }

                count++;
                if (names.Count < 2)
                {
                    names.Add($"'{slices.Entries[i].Name}'");
                }
            }

            if (count == 0)
            {
                return null;
            }

            return string.Join(", ", names) + (count > names.Count ? $" and {count - names.Count} more" : "");
        }
    }
}
