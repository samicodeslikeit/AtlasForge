using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace AtlasForge.Editor
{
    /// <summary>How a sprite's 9-slice border is determined during packing.</summary>
    public enum BorderMode
    {
        None,
        Manual,
        Auto
    }

    /// <summary>Per-sprite 9-slice configuration edited in the packer window.</summary>
    [System.Serializable]
    public sealed class SpriteBorderConfig
    {
        public string Name;
        public string AssetPath;
        public bool Selected;
        public BorderMode Mode = BorderMode.None;
        public Vector4 Border;

        /// <summary>Internal ID of the atlas slice this config edits; 0 for standalone sprites in a folder.</summary>
        public long SliceId;

        /// <summary>GUID of the atlas the slice belongs to; set only for atlas slices.</summary>
        public string AtlasGuid;

        /// <summary>Atlas slice to remove on the next repack.</summary>
        public bool MarkedForDeletion;

        /// <summary>Whether the slice was moved or resized in the window (atlas slices only).</summary>
        public bool SliceRectEdited;

        /// <summary>The edited slice rect, in atlas pixels (bottom-up).</summary>
        public RectInt SliceRect;

        /// <summary>The imported rect the edit started from; the edit is dropped if the atlas no longer matches it.</summary>
        public RectInt SliceRectBase;
    }

    /// <summary>
    /// Editor window that packs all UI sprites in a folder into a single
    /// dynamically-sized, power-of-two atlas, with optional per-sprite 9-slice
    /// borders (manual values or automatic detection). Further tabs repack an
    /// existing sliced atlas in place and merge several sliced atlases into one,
    /// editing their slices' borders the same way.
    /// </summary>
    /// <remarks>
    /// Layout, top to bottom: tab toolbar, the tab's source fields, collapsible packing
    /// settings, the sprite grid (which takes the spare height), a resizable selection
    /// panel, and a fixed footer with the tab's status and primary action.
    /// </remarks>
    public sealed partial class AtlasForgeWindow : EditorWindow
    {
        private enum WindowTab
        {
            Pack,
            Repack,
            Merge
        }

        private static readonly string[] TabLabels = { "Pack Folder", "Repack Atlas", "Merge Atlases" };
        private static readonly string[] BorderModeLabels = { "None", "Manual", "Auto" };

        private const int DefaultPadding = 2;

        // Grid layout metrics; the thumbnail size itself is user-adjustable.
        private const float MinThumbSize = 40f;
        private const float MaxThumbSize = 128f;
        private const float CellLabelHeight = 14f;
        private const float CellPadding = 4f;
        private const float BadgeSize = 14f;
        private const float FocusOutlineThickness = 2f;
        private const float MinGridHeight = 120f;

        // Selection panel metrics.
        private const float BorderPreviewSize = 136f;
        private const float MaxEditablePreviewSize = 512f;
        private const float GuideHitSize = 5f;
        private const float GuideGripSize = 6f;
        private const float MinInspectorHeight = 90f;
        private const float SplitterHeight = 5f;
        private const float FooterButtonWidth = 150f;

        private static readonly Color SelectionColor = new(0.24f, 0.48f, 0.90f, 0.35f);
        private static readonly Color FocusOutlineColor = new(1f, 0.8f, 0.2f, 1f);
        private static readonly Color GuideColor = new(0.2f, 1f, 0.4f, 0.9f);
        private static readonly Color NoneBadgeColor = new(0.4f, 0.4f, 0.4f, 0.9f);
        private static readonly Color AutoBadgeColor = new(0.25f, 0.55f, 0.95f, 0.9f);
        private static readonly Color ManualBadgeColor = new(0.95f, 0.55f, 0.15f, 0.9f);
        private static readonly Color DeletedOverlayColor = new(0.6f, 0.05f, 0.05f, 0.55f);

        private static Color SeparatorColor => EditorGUIUtility.isProSkin ? new Color(0.1f, 0.1f, 0.1f) : new Color(0.6f, 0.6f, 0.6f);
        private static Color GridBackgroundColor => EditorGUIUtility.isProSkin ? new Color(0.16f, 0.16f, 0.16f) : new Color(0.76f, 0.76f, 0.76f);

        private static GUIStyle _deletedMarkStyle;
        private static GUIStyle _cellLabelStyle;
        private static GUIStyle _sectionStyle;
        private static GUIStyle _hintStyle;

        private static GUIStyle DeletedMarkStyle => _deletedMarkStyle ??= new GUIStyle(EditorStyles.boldLabel)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 28,
            normal = { textColor = Color.white }
        };

        private static GUIStyle CellLabelStyle => _cellLabelStyle ??= new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.UpperCenter,
            clipping = TextClipping.Clip
        };

        /// <summary>Inner padding for the stacked sections, matching the Inspector's margins.</summary>
        private static GUIStyle SectionStyle => _sectionStyle ??= new GUIStyle { padding = new RectOffset(8, 8, 6, 6) };

        private static GUIStyle HintStyle => _hintStyle ??= new GUIStyle(EditorStyles.centeredGreyMiniLabel) { wordWrap = true };

        [SerializeField] private WindowTab _tab;
        [SerializeField] private DefaultAsset _sourceFolder;
        [SerializeField] private string _atlasName = "UIAtlas";
        [SerializeField] private int _padding = DefaultPadding;
        [SerializeField] private int _maxAtlasSize = 4096;
        [SerializeField] private bool _forcePowerOfTwo;
        [SerializeField] private float _autoTolerance = NineSliceBorderDetector.DefaultTolerance;
        [SerializeField] private List<SpriteBorderConfig> _borderConfigs = new();

        // Pending edit values for whatever is currently selected in the grid; applied on demand.
        [SerializeField] private BorderMode _detailsMode = BorderMode.Manual;
        [SerializeField] private bool _detailsUniform = true;
        [SerializeField] private float _detailsUniformValue = 8f;
        [SerializeField] private Vector4 _detailsBorder = new(8f, 8f, 8f, 8f);

        // View preferences, persisted across sessions.
        [SerializeField] private bool _showPackingSettings;
        [SerializeField] private float _thumbSize = 72f;
        [SerializeField] private float _inspectorHeight = 240f;
        [SerializeField] private string _search = "";

        private Vector2 _scroll;
        private Vector2 _inspectorScroll;
        private SpriteBorderConfig _focusedConfig;

        // The single sprite whose border the selection panel is live-editing, and the guide being dragged on its preview.
        private SpriteBorderConfig _detailsLoadedFor;
        private int _draggedGuide = -1;
        private bool _isDragSelecting;
        private bool _isResizingInspector;
        private SearchField _searchField;

        // Measured on repaint to size the grid to the window's spare height.
        private float _gridTop = 100f;
        private float _footerHeight = 40f;

        // Session-only caches; never serialized.
        private readonly Dictionary<string, Texture2D> _previewCache = new();
        private readonly Dictionary<string, Texture2D> _readableCache = new();

        private float CellWidth => _thumbSize + CellPadding * 2f;
        private float CellHeight => _thumbSize + CellLabelHeight + CellPadding * 2f;

        /// <summary>The atlas slices the current tab edits, or null on the folder tab.</summary>
        private AtlasSliceSet ActiveSliceSet => _tab switch
        {
            WindowTab.Repack => _repackSet,
            WindowTab.Merge => _mergeSet,
            _ => null
        };

        /// <summary>The sprite list the shared grid and details sections edit on the current tab.</summary>
        private List<SpriteBorderConfig> ActiveConfigs => ActiveSliceSet?.Configs ?? _borderConfigs;

        /// <summary>The current tab's sprites that match the search filter, in grid order.</summary>
        private List<SpriteBorderConfig> VisibleConfigs => string.IsNullOrEmpty(_search)
            ? ActiveConfigs
            : ActiveConfigs.Where(config => config.Name.IndexOf(_search, System.StringComparison.OrdinalIgnoreCase) >= 0).ToList();

        // Ctrl+Alt+A (Cmd+Alt+A on macOS); rebindable under Edit > Shortcuts.
        [MenuItem("Tools/AtlasForge %&a")]
        private static void Open()
        {
            GetWindow<AtlasForgeWindow>("AtlasForge").minSize = new Vector2(480f, 560f);
        }

        private void OnDisable()
        {
            ClearPreviewCaches();
            _repackSet.Unload();
            _mergeSet.Unload();
        }

        private void OnGUI()
        {
            switch (_tab)
            {
                case WindowTab.Repack:
                    EnsureRepackLoaded();
                    break;
                case WindowTab.Merge:
                    EnsureMergeLoaded();
                    break;
            }

            DrawToolbar();

            using (new EditorGUILayout.VerticalScope(SectionStyle))
            {
                switch (_tab)
                {
                    case WindowTab.Repack:
                        DrawRepackSourceSection();
                        break;
                    case WindowTab.Merge:
                        DrawMergeSourceSection();
                        break;
                    default:
                        DrawSourceSection();
                        break;
                }
            }

            DrawPackingSettings();
            DrawGridToolbar();
            DrawSpriteGrid();
            DrawInspectorSplitter();

            _inspectorScroll = EditorGUILayout.BeginScrollView(_inspectorScroll, GUILayout.Height(_inspectorHeight));
            using (new EditorGUILayout.VerticalScope(SectionStyle))
            {
                DrawDetailsSection();
            }

            EditorGUILayout.EndScrollView();

            DrawFooter();
            HandleShortcuts();

            // A drag can end anywhere (even outside a cell or the window), so end it globally.
            if (Event.current.type is EventType.MouseUp or EventType.Ignore)
            {
                _isDragSelecting = false;
                _isResizingInspector = false;
            }
        }

        /// <summary>Tab strip plus window-wide actions, drawn as a standard editor toolbar.</summary>
        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                for (int i = 0; i < TabLabels.Length; i++)
                {
                    bool active = (int)_tab == i;
                    if (GUILayout.Toggle(active, TabLabels[i], EditorStyles.toolbarButton, GUILayout.Width(104f)) && !active)
                    {
                        SwitchTab((WindowTab)i);
                    }
                }

                GUILayout.FlexibleSpace();

                GUIContent refresh = EditorGUIUtility.IconContent("Refresh");
                refresh.tooltip = "Reload the sprites from disk.";
                if (GUILayout.Button(refresh, EditorStyles.toolbarButton, GUILayout.Width(28f)))
                {
                    Reload();
                }
            }
        }

        private void SwitchTab(WindowTab tab)
        {
            _tab = tab;

            // Grid state belongs to the previous tab's sprite list.
            _focusedConfig = null;
            _isDragSelecting = false;
            _scroll = Vector2.zero;
            _inspectorScroll = Vector2.zero;
            GUI.FocusControl(null);
        }

        private void Reload()
        {
            switch (_tab)
            {
                case WindowTab.Repack:
                    LoadRepackSet();
                    break;
                case WindowTab.Merge:
                    LoadMergeSet();
                    break;
                default:
                    RefreshConfigs();
                    break;
            }
        }

        private void DrawSourceSection()
        {
            EditorGUI.BeginChangeCheck();
            _sourceFolder = (DefaultAsset)EditorGUILayout.ObjectField(
                new GUIContent("Sprites Folder", "Every texture in this folder is packed into the atlas."),
                _sourceFolder, typeof(DefaultAsset), false);
            if (EditorGUI.EndChangeCheck() && _sourceFolder != null)
            {
                RefreshConfigs();
            }

            _atlasName = EditorGUILayout.TextField(
                new GUIContent("Atlas Name", "File name of the atlas written into the sprites folder."), _atlasName);
        }

        /// <summary>Padding, size limit and power-of-two settings, shared by every tab and folded away by default.</summary>
        private void DrawPackingSettings()
        {
            DrawSeparator();
            string summary = _showPackingSettings
                ? "Packing"
                : $"Packing  ({_padding}px padding, max {_maxAtlasSize}, {(_forcePowerOfTwo ? "power of two" : "trimmed")})";
            _showPackingSettings = EditorGUILayout.BeginFoldoutHeaderGroup(_showPackingSettings, summary);
            if (_showPackingSettings)
            {
                using (new EditorGUILayout.VerticalScope(SectionStyle))
                {
                    _padding = Mathf.Clamp(EditorGUILayout.IntField("Padding (px)", _padding), 0, 64);
                    _maxAtlasSize = EditorGUILayout.IntPopup(
                        "Max Atlas Size",
                        _maxAtlasSize,
                        new[] { "512", "1024", "2048", "4096", "8192" },
                        new[] { 512, 1024, 2048, 4096, 8192 });
                    _forcePowerOfTwo = EditorGUILayout.Toggle(
                        new GUIContent(
                            "Force Power of Two",
                            "Off (recommended): the atlas is trimmed to the smallest 4px-aligned size that fits. " +
                            "On: both dimensions are rounded up to a power of two, which can waste most of the texture."),
                        _forcePowerOfTwo);
                }
            }

            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        /// <summary>Grid header: count, selection helpers, search and thumbnail size.</summary>
        private void DrawGridToolbar()
        {
            List<SpriteBorderConfig> configs = ActiveConfigs;
            int selectedCount = configs.Count(config => config.Selected);
            string noun = _tab == WindowTab.Pack ? "Sprites" : "Slices";

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label(
                    selectedCount > 0 ? $"{noun}  ·  {selectedCount} of {configs.Count} selected" : $"{noun}  ·  {configs.Count}",
                    EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();

                if (CanAddToRepackAtlas)
                {
                    DrawAddSpritesButton();
                }

                using (new EditorGUI.DisabledScope(configs.Count == 0))
                {
                    if (_tab == WindowTab.Repack && _repackSet.IsLoaded && _repackSet.Atlases.Count == 1 &&
                        GUILayout.Button(
                            new GUIContent("Duplicates", "Select every slice whose pixels exactly match an earlier slice."),
                            EditorStyles.toolbarButton))
                    {
                        SelectDuplicateSlices(_repackSet.Atlases[0]);
                    }

                    if (GUILayout.Button(new GUIContent("All", "Select all visible sprites (Ctrl/Cmd+A)."), EditorStyles.toolbarButton))
                    {
                        SetSelected(VisibleConfigs, true);
                    }

                    if (GUILayout.Button(new GUIContent("None", "Clear the selection (Esc)."), EditorStyles.toolbarButton))
                    {
                        SetAllSelected(false);
                    }
                }

                GUILayout.Space(6f);
                _searchField ??= new SearchField();
                _search = _searchField.OnToolbarGUI(_search, GUILayout.MinWidth(80f), GUILayout.MaxWidth(180f));
                GUILayout.Space(6f);
                _thumbSize = GUILayout.HorizontalSlider(_thumbSize, MinThumbSize, MaxThumbSize, GUILayout.Width(64f));
                GUILayout.Space(4f);
            }
        }

        /// <summary>Wrapping grid of clickable thumbnails that fills the height left over by the other sections.</summary>
        private void DrawSpriteGrid()
        {
            List<SpriteBorderConfig> items = VisibleConfigs;

            // IMGUI won't stretch a group into the window's spare height, so size it from the
            // previous repaint's measurements of everything around it.
            float height = Mathf.Max(MinGridHeight, position.height - _gridTop - SplitterHeight - _inspectorHeight - _footerHeight);
            Rect area = EditorGUILayout.BeginVertical(GUILayout.Height(height));
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(area, GridBackgroundColor);
                if (Mathf.Abs(area.y - _gridTop) > 0.5f)
                {
                    _gridTop = area.y;
                    Repaint();
                }
            }

            HandleSpriteDrop(area);
            if (items.Count == 0)
            {
                GUILayout.FlexibleSpace();
                GUILayout.Label(EmptyGridHint(), HintStyle);
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndVertical();
                return;
            }

            // Leave room for the vertical scrollbar so the last column never gets clipped.
            float availableWidth = Mathf.Max(position.width - GUI.skin.verticalScrollbar.fixedWidth - 8f, CellWidth);
            int columns = Mathf.Max(1, Mathf.FloorToInt(availableWidth / CellWidth));

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            for (int i = 0; i < items.Count; i += columns)
            {
                EditorGUILayout.BeginHorizontal();
                int rowEnd = Mathf.Min(i + columns, items.Count);
                for (int j = i; j < rowEnd; j++)
                {
                    DrawSpriteCell(items, j);
                }

                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private string EmptyGridHint()
        {
            if (ActiveConfigs.Count > 0)
            {
                return $"Nothing matches \"{_search}\".";
            }

            return _tab switch
            {
                WindowTab.Repack => "Assign a sliced atlas to list its slices.",
                WindowTab.Merge => "Add two or more sliced atlases to list their slices.",
                _ => "Assign a sprites folder to list its sprites."
            };
        }

        /// <summary>Draggable divider between the grid and the selection panel; dragging up grows the panel.</summary>
        private void DrawInspectorSplitter()
        {
            Rect rect = GUILayoutUtility.GetRect(position.width, SplitterHeight, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(new Rect(rect.x, rect.y + rect.height * 0.5f, rect.width, 1f), SeparatorColor);
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.ResizeVertical);

            // Keep the grid at least at its minimum height whatever the window size.
            float maxHeight = Mathf.Max(MinInspectorHeight, position.height - _gridTop - MinGridHeight - SplitterHeight - _footerHeight);
            if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
            {
                _isResizingInspector = true;
                Event.current.Use();
            }
            else if (_isResizingInspector && Event.current.type == EventType.MouseDrag)
            {
                _inspectorHeight -= Event.current.delta.y;
                Event.current.Use();
                Repaint();
            }

            _inspectorHeight = Mathf.Clamp(_inspectorHeight, MinInspectorHeight, maxHeight);
        }

        /// <summary>Fixed bottom bar with the tab's status on the left and its primary action on the right.</summary>
        private void DrawFooter()
        {
            Rect footer = EditorGUILayout.BeginVertical();
            DrawSeparator();

            string status;
            string label;
            bool enabled;
            System.Action action;
            switch (_tab)
            {
                case WindowTab.Repack:
                    status = RepackStatus();
                    label = "Repack Atlas";
                    enabled = _repackSet.IsLoaded && _repackSet.Atlases.Count == 1;
                    action = Repack;
                    break;
                case WindowTab.Merge:
                    status = MergeStatus();
                    label = "Merge Atlases";
                    enabled = _mergeSet.IsLoaded && _mergeSet.Atlases.Count >= 2;
                    action = Merge;
                    break;
                default:
                    status = _sourceFolder != null
                        ? $"{_borderConfigs.Count} sprites  →  {AssetDatabase.GetAssetPath(_sourceFolder)}/{_atlasName}.png"
                        : "No folder selected.";
                    label = "Pack Atlas";
                    enabled = _sourceFolder != null;
                    action = Pack;
                    break;
            }

            using (new EditorGUILayout.HorizontalScope(SectionStyle))
            {
                GUILayout.Label(status, EditorStyles.miniLabel, GUILayout.MinWidth(0f));
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(!enabled))
                {
                    if (GUILayout.Button(label, GUILayout.Width(FooterButtonWidth), GUILayout.Height(26f)))
                    {
                        // Run after this GUI pass: the action may show dialogs and reload the lists being drawn.
                        EditorApplication.delayCall += () =>
                        {
                            action();
                            Repaint();
                        };
                    }
                }
            }

            EditorGUILayout.EndVertical();
            if (Event.current.type == EventType.Repaint && Mathf.Abs(footer.height - _footerHeight) > 0.5f)
            {
                _footerHeight = footer.height;
                Repaint();
            }
        }

        /// <summary>Grid keyboard and command shortcuts: Select All, Escape to deselect, Delete to mark slices.</summary>
        private void HandleShortcuts()
        {
            Event current = Event.current;
            if (EditorGUIUtility.editingTextField)
            {
                return;
            }

            if (current.type is EventType.ValidateCommand or EventType.ExecuteCommand)
            {
                bool execute = current.type == EventType.ExecuteCommand;
                switch (current.commandName)
                {
                    case "SelectAll":
                        if (execute)
                        {
                            SetSelected(VisibleConfigs, true);
                        }

                        current.Use();
                        break;
                    case "SoftDelete" or "Delete" when _tab == WindowTab.Repack:
                        if (execute)
                        {
                            List<SpriteBorderConfig> selected = ActiveConfigs.Where(config => config.Selected).ToList();
                            SetMarkedForDeletion(selected, !selected.All(config => config.MarkedForDeletion));
                        }

                        current.Use();
                        break;
                }
            }
            else if (current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape)
            {
                SetAllSelected(false);
                _focusedConfig = null;
                current.Use();
                Repaint();
            }
        }

        /// <summary>
        /// Draws one thumbnail cell: preview, selection highlight, mode badge, name. Click selects,
        /// Ctrl/Cmd+click toggles, Shift+click selects a range, and dragging paints a selection.
        /// </summary>
        private void DrawSpriteCell(List<SpriteBorderConfig> items, int index)
        {
            SpriteBorderConfig config = items[index];
            Rect cellRect = GUILayoutUtility.GetRect(CellWidth, CellHeight, GUILayout.Width(CellWidth));

            if (config.Selected)
            {
                EditorGUI.DrawRect(cellRect, SelectionColor);
            }

            if (config == _focusedConfig)
            {
                DrawOutline(cellRect, FocusOutlineColor, FocusOutlineThickness);
            }

            var thumbRect = new Rect(cellRect.x + CellPadding, cellRect.y + CellPadding, _thumbSize, _thumbSize);
            Texture2D preview = GetPreviewTexture(config);
            if (preview != null)
            {
                GUI.DrawTexture(thumbRect, preview, ScaleMode.ScaleToFit);
            }

            if (config.MarkedForDeletion)
            {
                EditorGUI.DrawRect(thumbRect, DeletedOverlayColor);
                GUI.Label(thumbRect, "✕", DeletedMarkStyle);
            }

            if (config.Mode != BorderMode.None)
            {
                var badgeRect = new Rect(thumbRect.xMax - BadgeSize, thumbRect.y, BadgeSize, BadgeSize);
                EditorGUI.DrawRect(badgeRect, BadgeColor(config.Mode));
                GUI.Label(badgeRect, BadgeLabel(config.Mode), EditorStyles.whiteMiniLabel);
            }

            var labelRect = new Rect(cellRect.x, thumbRect.yMax + 2f, cellRect.width, CellLabelHeight);
            string size = preview != null ? $"\n{preview.width}×{preview.height}" : "";
            // Centre names that fit; start long ones at their beginning so the clipped part is the tail.
            var label = new GUIContent(config.Name, config.Name + size);
            CellLabelStyle.alignment = CellLabelStyle.CalcSize(label).x > labelRect.width ? TextAnchor.UpperLeft : TextAnchor.UpperCenter;
            GUI.Label(labelRect, label, CellLabelStyle);

            Event current = Event.current;
            if (current.type == EventType.MouseDown && current.button == 0 && cellRect.Contains(current.mousePosition))
            {
                int anchor = _focusedConfig != null ? items.IndexOf(_focusedConfig) : -1;
                if (EditorGUI.actionKey)
                {
                    config.Selected = !config.Selected;
                }
                else if (current.shift && anchor >= 0)
                {
                    for (int i = Mathf.Min(anchor, index); i <= Mathf.Max(anchor, index); i++)
                    {
                        items[i].Selected = true;
                    }
                }
                else
                {
                    SetAllSelected(false);
                    config.Selected = true;
                    _isDragSelecting = true;
                }

                // Shift+click keeps the anchor so further ranges grow from the same cell.
                if (!current.shift || anchor < 0)
                {
                    _focusedConfig = config;
                }

                GUI.FocusControl(null);
                current.Use();
                Repaint();
            }
            else if (_isDragSelecting && current.type == EventType.MouseDrag && cellRect.Contains(current.mousePosition))
            {
                config.Selected = true;
                _focusedConfig = config;
                current.Use();
                Repaint();
            }
        }

        /// <summary>
        /// Selection panel: one border editor that applies to every sprite selected in the grid,
        /// with a 9-slice preview beside it for a single sprite. Applying clears the selection so
        /// the values become that batch's final state and a fresh selection can be made next.
        /// </summary>
        private void DrawDetailsSection()
        {
            List<SpriteBorderConfig> selected = ActiveConfigs.Where(config => config.Selected).ToList();
            if (selected.Count == 0)
            {
                _detailsLoadedFor = null;
                GUILayout.Space(12f);
                GUILayout.Label(
                    "Select sprites in the grid to edit their 9-slice borders.\n" +
                    "Ctrl/Cmd+click toggles, Shift+click selects a range, drag to paint.",
                    HintStyle);
                return;
            }

            if (selected.Count == 1)
            {
                DrawSingleSpriteEditor(selected[0]);
            }
            else
            {
                _detailsLoadedFor = null;
                GUILayout.Label($"{selected.Count} sprites selected", EditorStyles.boldLabel);
                float labelWidth = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth = 110f;
                DrawBorderFields();
                EditorGUIUtility.labelWidth = labelWidth;

                GUILayout.Space(4f);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button($"Apply to {selected.Count} Sprites", GUILayout.Width(150f)))
                    {
                        ApplyDetailsToSelected(selected);
                    }
                }
            }

            if (_tab == WindowTab.Repack)
            {
                GUILayout.Space(4f);
                DrawSeparator();
                DrawDeletionControls(selected);
                DrawExportControls(selected);
            }
        }

        /// <summary>
        /// Live editor for one sprite, like the Sprite Editor's border view: the preview's guide
        /// lines can be dragged, and every change (drag or field) is written straight onto the
        /// sprite's config. The tab's footer action is what finally writes it to the asset.
        /// </summary>
        private void DrawSingleSpriteEditor(SpriteBorderConfig config)
        {
            Texture2D preview = GetPreviewTexture(config);
            if (_detailsLoadedFor != config)
            {
                LoadDetailsFrom(config);
            }

            // Atlas slices can also be moved and resized inside their atlas.
            bool canEditRect = ActiveSliceSet != null && ActiveSliceSet.TryFindSlice(config, out _, out _);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(config.Name, EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                if (preview != null)
                {
                    GUILayout.Label($"{preview.width}×{preview.height}", EditorStyles.miniLabel);
                }

                if (canEditRect)
                {
                    _editSliceRect = GUILayout.Toolbar(
                        _editSliceRect ? 1 : 0, SliceEditModeLabels, EditorStyles.miniButton, GUILayout.Width(140f)) == 1;
                }
            }

            if (canEditRect && _editSliceRect)
            {
                ActiveSliceSet.TryFindSlice(config, out AtlasSlices slices, out int index);
                DrawSliceRectEditor(config, slices, index);
                return;
            }

            EditorGUI.BeginChangeCheck();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (preview != null)
                {
                    Vector4 shown = _detailsMode switch
                    {
                        BorderMode.Manual => NineSliceBorderDetector.Clamp(_detailsBorder, preview.width, preview.height),
                        BorderMode.Auto => GetAutoBorderPreview(config),
                        _ => Vector4.zero
                    };

                    // The preview grows with the panel (and widens for wide sprites), leaving room for the fields beside it.
                    float maxWidth = Mathf.Max(BorderPreviewSize, position.width - 320f);
                    float height = Mathf.Clamp(_inspectorHeight - 48f, BorderPreviewSize, MaxEditablePreviewSize);
                    float width = Mathf.Clamp(height * preview.width / preview.height, BorderPreviewSize, maxWidth);
                    Vector4 dragged = DrawBorderPreview(preview, shown, width, height, true);
                    if (dragged != shown)
                    {
                        // Dragging always produces a per-side manual border, starting from what was shown
                        // (so an Auto border can be fine-tuned by hand).
                        _detailsMode = BorderMode.Manual;
                        _detailsUniform = false;
                        _detailsBorder = dragged;
                    }

                    GUILayout.Space(8f);
                }

                using (new EditorGUILayout.VerticalScope())
                {
                    float labelWidth = EditorGUIUtility.labelWidth;
                    EditorGUIUtility.labelWidth = 110f;
                    DrawBorderFields();
                    EditorGUIUtility.labelWidth = labelWidth;

                    GUILayout.Space(4f);
                    string action = _tab switch
                    {
                        WindowTab.Repack => "Repack Atlas",
                        WindowTab.Merge => "Merge Atlases",
                        _ => "Pack Atlas"
                    };
                    GUILayout.Label(
                        $"Drag the green guides on the preview to set the border. Changes are written when you click {action}.",
                        EditorStyles.wordWrappedMiniLabel);
                }
            }

            if (EditorGUI.EndChangeCheck())
            {
                config.Mode = _detailsMode;
                if (_detailsMode == BorderMode.Manual)
                {
                    config.Border = _detailsBorder;
                }
            }
        }

        /// <summary>Loads a sprite's current border into the editing fields.</summary>
        private void LoadDetailsFrom(SpriteBorderConfig config)
        {
            _detailsLoadedFor = config;
            _detailsMode = config.Mode;
            _detailsBorder = config.Mode == BorderMode.Manual ? config.Border : Vector4.zero;
            Vector4 border = _detailsBorder;
            _detailsUniform = border.x == border.y && border.y == border.z && border.z == border.w;
            _detailsUniformValue = border.x;
            GUI.FocusControl(null);
        }

        /// <summary>Border mode as a segmented control, followed by the fields that mode needs.</summary>
        private void DrawBorderFields()
        {
            Rect modeRect = EditorGUI.PrefixLabel(EditorGUILayout.GetControlRect(), new GUIContent("9-Slice Border"));
            _detailsMode = (BorderMode)GUI.Toolbar(modeRect, (int)_detailsMode, BorderModeLabels, EditorStyles.miniButton);

            switch (_detailsMode)
            {
                case BorderMode.Manual:
                    _detailsUniform = EditorGUILayout.Toggle("Uniform", _detailsUniform);
                    if (_detailsUniform)
                    {
                        _detailsUniformValue = EditorGUILayout.FloatField("Size (px)", _detailsUniformValue);
                        _detailsBorder = new Vector4(_detailsUniformValue, _detailsUniformValue, _detailsUniformValue, _detailsUniformValue);
                    }
                    else
                    {
                        _detailsBorder.x = EditorGUILayout.FloatField("Left", _detailsBorder.x);
                        _detailsBorder.z = EditorGUILayout.FloatField("Right", _detailsBorder.z);
                        _detailsBorder.w = EditorGUILayout.FloatField("Top", _detailsBorder.w);
                        _detailsBorder.y = EditorGUILayout.FloatField("Bottom", _detailsBorder.y);
                    }

                    break;
                case BorderMode.Auto:
                    _autoTolerance = EditorGUILayout.Slider(
                        new GUIContent("Tolerance", "How different neighbouring pixels may be and still count as the stretchable middle. Shared by every Auto sprite."),
                        _autoTolerance, 0f, 0.25f);
                    break;
                default:
                    EditorGUILayout.LabelField(" ", "No 9-slice border.", EditorStyles.miniLabel);
                    break;
            }
        }

        /// <summary>Writes the pending border-mode/border values onto every given sprite, then deselects them.</summary>
        private void ApplyDetailsToSelected(List<SpriteBorderConfig> selected)
        {
            foreach (SpriteBorderConfig config in selected)
            {
                config.Mode = _detailsMode;
                if (_detailsMode == BorderMode.Manual)
                {
                    config.Border = _detailsBorder;
                }

                config.Selected = false;
            }

            _focusedConfig = null;
            Repaint();
        }

        /// <summary>
        /// Draws an enlarged sprite preview over a transparency checkerboard, with 9-slice guide
        /// lines overlaid. When <paramref name="editable"/>, each guide can be dragged with the
        /// mouse (snapping to whole pixels, never crossing its opposite guide); returns the
        /// border after any drag.
        /// </summary>
        private Vector4 DrawBorderPreview(Texture2D texture, Vector4 border, float width, float height, bool editable)
        {
            Rect bounds = GUILayoutUtility.GetRect(width, height, GUILayout.Width(width), GUILayout.Height(height));
            Rect imageRect = FitRect(bounds, texture.width, texture.height);
            float scaleX = imageRect.width / texture.width;
            float scaleY = imageRect.height / texture.height;

            // Guide positions in GUI space, indexed like the border: x = left, y = bottom, z = right, w = top.
            // Border.y/w are measured from the bottom-left origin (sprite convention); GUI space grows
            // downward, so the bottom/top guides are flipped accordingly.
            float[] guides =
            {
                imageRect.x + border.x * scaleX,
                imageRect.yMax - border.y * scaleY,
                imageRect.xMax - border.z * scaleX,
                imageRect.y + border.w * scaleY
            };

            int controlId = GUIUtility.GetControlID(FocusType.Passive, bounds);
            Event current = Event.current;
            if (editable)
            {
                for (int i = 0; i < 4; i++)
                {
                    EditorGUIUtility.AddCursorRect(
                        GuideHitRect(imageRect, guides[i], i),
                        IsVerticalGuide(i) ? MouseCursor.ResizeHorizontal : MouseCursor.ResizeVertical,
                        controlId);
                }

                switch (current.GetTypeForControl(controlId))
                {
                    case EventType.MouseDown when current.button == 0:
                        _draggedGuide = NearestGuide(imageRect, guides, current.mousePosition);
                        if (_draggedGuide >= 0)
                        {
                            GUIUtility.hotControl = controlId;
                            current.Use();
                        }

                        break;
                    case EventType.MouseDrag when GUIUtility.hotControl == controlId && _draggedGuide >= 0:
                        border[_draggedGuide] = GuideValue(_draggedGuide, current.mousePosition, imageRect, scaleX, scaleY, border, texture);
                        GUI.changed = true;
                        current.Use();
                        break;
                    case EventType.MouseUp when GUIUtility.hotControl == controlId:
                        GUIUtility.hotControl = 0;
                        _draggedGuide = -1;
                        current.Use();
                        break;
                }
            }

            if (current.type != EventType.Repaint)
            {
                return border;
            }

            EditorGUI.DrawRect(bounds, GridBackgroundColor);
            EditorGUI.DrawTextureTransparent(imageRect, texture, ScaleMode.ScaleToFit);
            if (border == Vector4.zero && !editable)
            {
                return border;
            }

            // Recompute after a drag so the lines follow the mouse this frame.
            guides[0] = imageRect.x + border.x * scaleX;
            guides[1] = imageRect.yMax - border.y * scaleY;
            guides[2] = imageRect.xMax - border.z * scaleX;
            guides[3] = imageRect.y + border.w * scaleY;
            bool dragging = GUIUtility.hotControl == controlId;
            for (int i = 0; i < 4; i++)
            {
                Color color = dragging && _draggedGuide == i ? FocusOutlineColor : GuideColor;
                Rect line = IsVerticalGuide(i)
                    ? new Rect(guides[i], imageRect.y, 1f, imageRect.height)
                    : new Rect(imageRect.x, guides[i], imageRect.width, 1f);
                EditorGUI.DrawRect(line, color);

                if (editable)
                {
                    // A small square grip in the middle of each guide, as in the Sprite Editor.
                    Vector2 middle = IsVerticalGuide(i) ? new Vector2(guides[i], imageRect.center.y) : new Vector2(imageRect.center.x, guides[i]);
                    EditorGUI.DrawRect(new Rect(middle.x - GuideGripSize * 0.5f, middle.y - GuideGripSize * 0.5f, GuideGripSize, GuideGripSize), color);
                }
            }

            if (dragging && _draggedGuide >= 0)
            {
                var label = new GUIContent($"{BorderSideNames[_draggedGuide]} {border[_draggedGuide]:0} px");
                Vector2 labelSize = EditorStyles.helpBox.CalcSize(label);
                var labelRect = new Rect(bounds.x + 4f, bounds.y + 4f, labelSize.x, labelSize.y);
                GUI.Label(labelRect, label, EditorStyles.helpBox);
            }

            return border;
        }

        private static readonly string[] BorderSideNames = { "Left", "Bottom", "Right", "Top" };

        private static bool IsVerticalGuide(int side) => side is 0 or 2;

        /// <summary>The strip around a guide line that grabs it.</summary>
        private static Rect GuideHitRect(Rect imageRect, float guide, int side) => IsVerticalGuide(side)
            ? new Rect(guide - GuideHitSize, imageRect.y, GuideHitSize * 2f, imageRect.height)
            : new Rect(imageRect.x, guide - GuideHitSize, imageRect.width, GuideHitSize * 2f);

        /// <summary>The guide closest to the mouse within grabbing distance, or -1.</summary>
        private static int NearestGuide(Rect imageRect, float[] guides, Vector2 mouse)
        {
            int nearest = -1;
            float best = GuideHitSize;
            for (int i = 0; i < 4; i++)
            {
                if (!GuideHitRect(imageRect, guides[i], i).Contains(mouse))
                {
                    continue;
                }

                float distance = Mathf.Abs((IsVerticalGuide(i) ? mouse.x : mouse.y) - guides[i]);

                // Ties (e.g. a zero border on both sides of a 1px sprite) go to the side the mouse is on.
                if (distance <= best)
                {
                    best = distance;
                    nearest = i;
                }
            }

            return nearest;
        }

        /// <summary>
        /// The whole-pixel border value for a guide dragged to the mouse, stopping 1px short of its
        /// opposite guide so a stretchable middle always remains (as <see cref="NineSliceBorderDetector.Clamp"/> requires).
        /// </summary>
        private static float GuideValue(int side, Vector2 mouse, Rect imageRect, float scaleX, float scaleY, Vector4 border, Texture2D texture)
        {
            float value = side switch
            {
                0 => (mouse.x - imageRect.x) / scaleX,
                1 => (imageRect.yMax - mouse.y) / scaleY,
                2 => (imageRect.xMax - mouse.x) / scaleX,
                _ => (mouse.y - imageRect.y) / scaleY
            };

            float limit = (IsVerticalGuide(side) ? texture.width : texture.height) - border[(side + 2) % 4] - 1f;
            return Mathf.Clamp(Mathf.Round(value), 0f, Mathf.Max(0f, limit));
        }

        /// <summary>A full-width 1px line between sections.</summary>
        private static void DrawSeparator()
        {
            Rect rect = GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, SeparatorColor);
        }

        /// <summary>Syncs the border list with the textures currently in the source folder.</summary>
        private void RefreshConfigs()
        {
            string folderPath = AssetDatabase.GetAssetPath(_sourceFolder);
            if (!AssetDatabase.IsValidFolder(folderPath))
            {
                return;
            }

            Dictionary<string, string> pathsByName = SpriteAtlasBuilder.CollectTexturePaths(folderPath)
                .Where(path => Path.GetFileNameWithoutExtension(path) != _atlasName)
                .ToDictionary(Path.GetFileNameWithoutExtension, path => path);

            _borderConfigs.RemoveAll(config => !pathsByName.ContainsKey(config.Name));
            foreach (string name in pathsByName.Keys.Where(name => _borderConfigs.All(config => config.Name != name)))
            {
                _borderConfigs.Add(new SpriteBorderConfig { Name = name, AssetPath = pathsByName[name] });
            }

            foreach (SpriteBorderConfig config in _borderConfigs)
            {
                config.AssetPath = pathsByName[config.Name];
            }

            if (_focusedConfig != null && !_borderConfigs.Contains(_focusedConfig))
            {
                _focusedConfig = null;
            }

            ClearPreviewCaches();
        }

        private void SetAllSelected(bool selected)
        {
            foreach (SpriteBorderConfig config in ActiveConfigs)
            {
                config.Selected = selected;
            }
        }

        private static void SetSelected(IEnumerable<SpriteBorderConfig> configs, bool selected)
        {
            foreach (SpriteBorderConfig config in configs)
            {
                config.Selected = selected;
            }
        }

        private void Pack()
        {
            string folderPath = AssetDatabase.GetAssetPath(_sourceFolder);
            if (!AssetDatabase.IsValidFolder(folderPath))
            {
                EditorUtility.DisplayDialog("AtlasForge", "Select a valid sprites folder.", "OK");
                return;
            }

            List<string> paths = SpriteAtlasBuilder.CollectTexturePaths(folderPath)
                .Where(path => Path.GetFileNameWithoutExtension(path) != _atlasName)
                .ToList();
            if (paths.Count == 0)
            {
                EditorUtility.DisplayDialog("AtlasForge", "No textures found in folder.", "OK");
                return;
            }

            CloseSpriteEditor();

            List<string> revertedReadable = SpriteAtlasBuilder.SetReadable(paths, true);
            try
            {
                List<AtlasSpriteEntry> entries = BuildEntries(paths);
                if (!SpriteAtlasBuilder.TryPack(entries, _padding, _maxAtlasSize, _forcePowerOfTwo, out int atlasWidth, out int atlasHeight))
                {
                    EditorUtility.DisplayDialog(
                        "AtlasForge",
                        $"Sprites do not fit within {_maxAtlasSize}px. Increase Max Atlas Size, lower Padding, or reduce sprite count.",
                        "OK");
                    return;
                }

                ApplyBorders(entries);

                Texture2D atlas = SpriteAtlasBuilder.Render(entries, atlasWidth, atlasHeight);
                string outputPath = SpriteAtlasBuilder.WritePng(atlas, folderPath, _atlasName);
                DestroyImmediate(atlas);

                SpriteSheetWriter.Write(outputPath, entries, atlasWidth, atlasHeight);

                float fill = SpriteAtlasBuilder.FillRatio(entries, atlasWidth, atlasHeight);
                EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(outputPath));
                Debug.Log(
                    $"[AtlasForge] Packed {entries.Count} sprites into a {atlasWidth}x{atlasHeight} atlas " +
                    $"({fill:P1} filled) at '{outputPath}'.");
            }
            finally
            {
                SpriteAtlasBuilder.SetReadable(revertedReadable, false);
                AssetDatabase.Refresh();
            }
        }

        /// <summary>
        /// Closes the Sprite Editor before packing. Packing reimports the atlas and its
        /// sources, which destroys the importers that window caches; left open it throws a
        /// NullReferenceException on every repaint and silently refuses to apply changes.
        /// Matched by type name so the 2D Sprite package stays an optional dependency.
        /// </summary>
        private static void CloseSpriteEditor()
        {
            foreach (EditorWindow window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (window != null && window.GetType().FullName == "UnityEditor.U2D.Sprites.SpriteEditorWindow")
                {
                    window.Close();
                }
            }
        }

        private static List<AtlasSpriteEntry> BuildEntries(List<string> paths)
        {
            var entries = new List<AtlasSpriteEntry>(paths.Count);
            foreach (string path in paths)
            {
                Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (texture == null)
                {
                    continue;
                }

                entries.Add(new AtlasSpriteEntry
                {
                    Name = Path.GetFileNameWithoutExtension(path),
                    Texture = texture,
                    Width = texture.width,
                    Height = texture.height
                });
            }

            return entries;
        }

        private void ApplyBorders(List<AtlasSpriteEntry> entries)
        {
            foreach (AtlasSpriteEntry entry in entries)
            {
                entry.Border = ResolveBorder(_borderConfigs.FirstOrDefault(c => c.Name == entry.Name), entry);
            }
        }

        /// <summary>The final 9-slice border a config produces for an entry; requires a readable entry texture for Auto.</summary>
        private Vector4 ResolveBorder(SpriteBorderConfig config, AtlasSpriteEntry entry)
        {
            if (config == null)
            {
                return Vector4.zero;
            }

            return config.Mode switch
            {
                BorderMode.Manual => NineSliceBorderDetector.Clamp(config.Border, entry.Width, entry.Height),
                BorderMode.Auto => NineSliceBorderDetector.Detect(entry.Texture, _autoTolerance),
                _ => Vector4.zero
            };
        }

        /// <summary>
        /// Loads (and caches) the imported texture asset used for grid/detail previews, or
        /// returns the cut-out slice texture for an atlas slice.
        /// </summary>
        private Texture2D GetPreviewTexture(SpriteBorderConfig config)
        {
            if (config.SliceId != 0)
            {
                return ActiveSliceSet?.FindTexture(config);
            }

            if (string.IsNullOrEmpty(config.AssetPath))
            {
                return null;
            }

            if (_previewCache.TryGetValue(config.AssetPath, out Texture2D cached) && cached != null)
            {
                return cached;
            }

            Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(config.AssetPath);
            _previewCache[config.AssetPath] = texture;
            return texture;
        }

        /// <summary>Runs auto-detection against a readable copy of the sprite for live preview purposes.</summary>
        private Vector4 GetAutoBorderPreview(SpriteBorderConfig config)
        {
            // Slice textures are already readable copies.
            Texture2D readable = config.SliceId != 0 ? GetPreviewTexture(config) : GetReadableTexture(config.AssetPath);
            return readable != null ? NineSliceBorderDetector.Detect(readable, _autoTolerance) : Vector4.zero;
        }

        /// <summary>
        /// Loads a throwaway readable copy of a texture (decoded from its file bytes) so detection
        /// can run for preview purposes without toggling the asset's import settings.
        /// </summary>
        private Texture2D GetReadableTexture(string assetPath)
        {
            if (_readableCache.TryGetValue(assetPath, out Texture2D cached) && cached != null)
            {
                return cached;
            }

            Texture2D texture = SpriteAtlasBuilder.LoadFromFile(assetPath);
            if (texture == null)
            {
                return null;
            }

            _readableCache[assetPath] = texture;
            return texture;
        }

        private void ClearPreviewCaches()
        {
            foreach (Texture2D texture in _readableCache.Values)
            {
                if (texture != null)
                {
                    DestroyImmediate(texture);
                }
            }

            _readableCache.Clear();
            _previewCache.Clear();
        }

        private static Color BadgeColor(BorderMode mode) => mode switch
        {
            BorderMode.Auto => AutoBadgeColor,
            BorderMode.Manual => ManualBadgeColor,
            _ => NoneBadgeColor
        };

        private static string BadgeLabel(BorderMode mode) => mode switch
        {
            BorderMode.Auto => "A",
            BorderMode.Manual => "M",
            _ => ""
        };

        private static void DrawOutline(Rect rect, Color color, float thickness)
        {
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, thickness), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, thickness, rect.height), color);
            EditorGUI.DrawRect(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), color);
        }

        private static Rect FitRect(Rect bounds, int textureWidth, int textureHeight)
        {
            float aspect = (float)textureWidth / textureHeight;
            float width = bounds.width;
            float height = width / aspect;
            if (height > bounds.height)
            {
                height = bounds.height;
                width = height * aspect;
            }

            float x = bounds.x + (bounds.width - width) * 0.5f;
            float y = bounds.y + (bounds.height - height) * 0.5f;
            return new Rect(x, y, width, height);
        }
    }
}
