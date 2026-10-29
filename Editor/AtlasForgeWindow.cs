using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
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
    }

    /// <summary>
    /// Editor window that packs all UI sprites in a folder into a single
    /// dynamically-sized, power-of-two atlas, with optional per-sprite 9-slice
    /// borders (manual values or automatic detection). Further tabs repack an
    /// existing sliced atlas in place and merge several sliced atlases into one,
    /// editing their slices' borders the same way.
    /// </summary>
    public sealed partial class AtlasForgeWindow : EditorWindow
    {
        private enum WindowTab
        {
            Pack,
            Repack,
            Merge
        }

        private static readonly string[] TabLabels = { "Pack Folder", "Repack Atlas", "Merge Atlases" };

        private const int DefaultPadding = 2;

        // Grid layout metrics.
        private const float ThumbSize = 72f;
        private const float CellLabelHeight = 14f;
        private const float CellPadding = 4f;
        private const float CellWidth = ThumbSize + CellPadding * 2f;
        private const float CellHeight = ThumbSize + CellLabelHeight + CellPadding * 2f;
        private const float BadgeSize = 14f;
        private const float BorderPreviewSize = 220f;
        private const float FocusOutlineThickness = 2f;

        // Resizable grid metrics: drag handles clamp between a fixed minimum and the
        // height needed to fit all content (no point growing further than that).
        private const float MinGridHeight = 160f;
        private const float MinDetailsGridHeight = 100f;
        private const float ResizeHandleHeight = 6f;

        private static readonly Color SelectionColor = new(0.24f, 0.48f, 0.90f, 0.35f);
        private static readonly Color FocusOutlineColor = new(1f, 0.8f, 0.2f, 1f);
        private static readonly Color GuideColor = new(0.2f, 1f, 0.4f, 0.9f);
        private static readonly Color NoneBadgeColor = new(0.4f, 0.4f, 0.4f, 0.9f);
        private static readonly Color AutoBadgeColor = new(0.25f, 0.55f, 0.95f, 0.9f);
        private static readonly Color ManualBadgeColor = new(0.95f, 0.55f, 0.15f, 0.9f);
        private static readonly Color ResizeHandleColor = new(0f, 0f, 0f, 0.35f);
        private static readonly Color DeletedOverlayColor = new(0.6f, 0.05f, 0.05f, 0.55f);

        private static GUIStyle _deletedMarkStyle;

        private static GUIStyle DeletedMarkStyle => _deletedMarkStyle ??= new GUIStyle(EditorStyles.boldLabel)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 28,
            normal = { textColor = Color.white }
        };

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

        // User-adjustable heights for the resizable grids, persisted across sessions.
        [SerializeField] private float _gridHeight = 280f;
        [SerializeField] private float _detailsGridHeight = 160f;

        private Vector2 _scroll;
        private Vector2 _detailsScroll;
        private SpriteBorderConfig _focusedConfig;
        private bool _isDragSelecting;
        private bool _isResizingGrid;
        private bool _isResizingDetailsGrid;

        // Session-only caches; never serialized.
        private readonly Dictionary<string, Texture2D> _previewCache = new();
        private readonly Dictionary<string, Texture2D> _readableCache = new();

        /// <summary>The atlas slices the current tab edits, or null on the folder tab.</summary>
        private AtlasSliceSet ActiveSliceSet => _tab switch
        {
            WindowTab.Repack => _repackSet,
            WindowTab.Merge => _mergeSet,
            _ => null
        };

        /// <summary>The sprite list the shared grid and details sections edit on the current tab.</summary>
        private List<SpriteBorderConfig> ActiveConfigs => ActiveSliceSet?.Configs ?? _borderConfigs;

        // Ctrl+Alt+A (Cmd+Alt+A on macOS); rebindable under Edit > Shortcuts.
        [MenuItem("Tools/AtlasForge %&a")]
        private static void Open()
        {
            GetWindow<AtlasForgeWindow>("AtlasForge").minSize = new Vector2(520f, 640f);
        }

        private void OnDisable()
        {
            ClearPreviewCaches();
            _repackSet.Unload();
            _mergeSet.Unload();
        }

        private void OnGUI()
        {
            DrawTabs();

            switch (_tab)
            {
                case WindowTab.Repack:
                    EnsureRepackLoaded();
                    DrawBoxedSection(DrawRepackSourceSection);
                    break;
                case WindowTab.Merge:
                    EnsureMergeLoaded();
                    DrawBoxedSection(DrawMergeSourceSection);
                    break;
                default:
                    DrawBoxedSection(DrawSourceSection);
                    break;
            }

            DrawBoxedSection(DrawSettingsSection);
            DrawBoxedSection(DrawBulkToolbar);
            DrawBoxedSection(DrawSpriteGrid);
            DrawBoxedSection(DrawDetailsSection);

            switch (_tab)
            {
                case WindowTab.Repack:
                    DrawRepackButton();
                    break;
                case WindowTab.Merge:
                    DrawMergeButton();
                    break;
                default:
                    DrawPackButton();
                    break;
            }

            // A drag can end anywhere (even outside a cell or the window), so end it globally.
            if (Event.current.type is EventType.MouseUp or EventType.Ignore)
            {
                _isDragSelecting = false;
            }
        }

        /// <summary>Wraps a section's contents in a bordered box with spacing, for visual separation.</summary>
        private static void DrawBoxedSection(System.Action drawContent)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            drawContent();
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(6f);
        }

        private void DrawTabs()
        {
            EditorGUI.BeginChangeCheck();
            _tab = (WindowTab)GUILayout.Toolbar((int)_tab, TabLabels, GUILayout.Height(24f));
            if (EditorGUI.EndChangeCheck())
            {
                // Grid state belongs to the previous tab's sprite list.
                _focusedConfig = null;
                _isDragSelecting = false;
                _scroll = Vector2.zero;
                _detailsScroll = Vector2.zero;
            }

            EditorGUILayout.Space(6f);
        }

        private void DrawSourceSection()
        {
            EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            _sourceFolder = (DefaultAsset)EditorGUILayout.ObjectField(
                "Sprites Folder", _sourceFolder, typeof(DefaultAsset), false);
            if (EditorGUI.EndChangeCheck() && _sourceFolder != null)
            {
                RefreshConfigs();
            }
        }

        private void DrawSettingsSection()
        {
            EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
            if (_tab == WindowTab.Pack)
            {
                _atlasName = EditorGUILayout.TextField("Atlas Name", _atlasName);
            }

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

        private void DrawBulkToolbar()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("9-Slice Borders", EditorStyles.boldLabel);
                if (GUILayout.Button("Refresh", GUILayout.Width(70f)))
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
            }

            _autoTolerance = EditorGUILayout.Slider("Auto Tolerance", _autoTolerance, 0f, 0.25f);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Select All"))
                {
                    SetAllSelected(true);
                }

                if (GUILayout.Button("Select None"))
                {
                    SetAllSelected(false);
                }
            }
        }

        /// <summary>Draws a wrapping grid of clickable sprite thumbnails, resizable between a fixed minimum and its full content height.</summary>
        private void DrawSpriteGrid()
        {
            EditorGUILayout.LabelField("Sprites", EditorStyles.boldLabel);

            if (ActiveConfigs.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    _tab switch
                    {
                        WindowTab.Repack => "Assign a sliced atlas to list its slices.",
                        WindowTab.Merge => "Add the sliced atlases to merge to list their slices.",
                        _ => "Assign a folder and click Refresh to list its sprites."
                    },
                    MessageType.Info);
                return;
            }

            DrawResizableSpriteGrid(ActiveConfigs, ref _scroll, ref _gridHeight, ref _isResizingGrid, MinGridHeight);
        }

        /// <summary>
        /// Draws a wrapping, resizable grid of thumbnails for the given sprites, with a drag
        /// handle beneath it clamped between <paramref name="minHeight"/> and the height needed
        /// to fit every row without scrolling.
        /// </summary>
        private void DrawResizableSpriteGrid(
            List<SpriteBorderConfig> items, ref Vector2 scroll, ref float height, ref bool resizing, float minHeight)
        {
            float availableWidth = Mathf.Max(position.width - 24f, CellWidth);
            int columns = Mathf.Max(1, Mathf.FloorToInt(availableWidth / CellWidth));
            int rows = Mathf.CeilToInt(items.Count / (float)columns);
            float maxHeight = Mathf.Max(minHeight, rows * CellHeight);
            height = Mathf.Clamp(height, minHeight, maxHeight);

            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.Height(height));
            for (int i = 0; i < items.Count; i += columns)
            {
                EditorGUILayout.BeginHorizontal();
                int rowEnd = Mathf.Min(i + columns, items.Count);
                for (int j = i; j < rowEnd; j++)
                {
                    DrawSpriteCell(items[j]);
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();

            if (maxHeight - minHeight > 1f)
            {
                DrawResizeHandle(ref height, minHeight, maxHeight, ref resizing);
            }
        }

        /// <summary>Draws a draggable horizontal splitter that adjusts <paramref name="height"/> within bounds.</summary>
        private void DrawResizeHandle(ref float height, float minHeight, float maxHeight, ref bool resizing)
        {
            Rect handleRect = GUILayoutUtility.GetRect(position.width, ResizeHandleHeight, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(handleRect, ResizeHandleColor);
            EditorGUIUtility.AddCursorRect(handleRect, MouseCursor.ResizeVertical);

            if (Event.current.type == EventType.MouseDown && handleRect.Contains(Event.current.mousePosition))
            {
                resizing = true;
                Event.current.Use();
            }
            else if (resizing && Event.current.type == EventType.MouseDrag)
            {
                height = Mathf.Clamp(height + Event.current.delta.y, minHeight, maxHeight);
                Event.current.Use();
                Repaint();
            }

            if (Event.current.type is EventType.MouseUp or EventType.Ignore)
            {
                resizing = false;
            }
        }

        /// <summary>Draws a single thumbnail cell: preview, selection highlight, mode badge, name.</summary>
        private void DrawSpriteCell(SpriteBorderConfig config)
        {
            Rect cellRect = GUILayoutUtility.GetRect(CellWidth, CellHeight, GUILayout.Width(CellWidth));

            if (config.Selected)
            {
                EditorGUI.DrawRect(cellRect, SelectionColor);
            }

            if (config == _focusedConfig)
            {
                DrawOutline(cellRect, FocusOutlineColor, FocusOutlineThickness);
            }

            var thumbRect = new Rect(cellRect.x + CellPadding, cellRect.y + CellPadding, ThumbSize, ThumbSize);
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
            GUI.Label(labelRect, config.Name, EditorStyles.miniLabel);

            if (Event.current.type == EventType.MouseDown && cellRect.Contains(Event.current.mousePosition))
            {
                // Shift extends the existing selection; a plain click/drag overrides it,
                // clearing everything else before this new drag starts painting selections.
                if (!Event.current.shift)
                {
                    SetAllSelected(false);
                }

                _isDragSelecting = true;
                config.Selected = true;
                _focusedConfig = config;
                Event.current.Use();
                Repaint();
            }
            else if (_isDragSelecting && Event.current.type == EventType.MouseDrag &&
                     cellRect.Contains(Event.current.mousePosition))
            {
                config.Selected = true;
                _focusedConfig = config;
                Event.current.Use();
                Repaint();
            }
        }

        /// <summary>
        /// Draws a single border-mode/border editor that applies to every sprite currently
        /// selected in the grid. Applying clears the selection so the values become that
        /// batch's final state and a fresh selection can be made for the next batch.
        /// </summary>
        private void DrawDetailsSection()
        {
            EditorGUILayout.LabelField("Sprite Details", EditorStyles.boldLabel);

            List<SpriteBorderConfig> selected = ActiveConfigs.Where(config => config.Selected).ToList();
            if (selected.Count == 0)
            {
                EditorGUILayout.HelpBox("Select one or more sprites in the grid to edit their border settings.", MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField(
                selected.Count == 1 ? selected[0].Name : $"{selected.Count} sprites selected",
                EditorStyles.miniBoldLabel);

            DrawResizableSpriteGrid(selected, ref _detailsScroll, ref _detailsGridHeight, ref _isResizingDetailsGrid, MinDetailsGridHeight);
            EditorGUILayout.Space();

            _detailsMode = (BorderMode)EditorGUILayout.EnumPopup("Border Mode", _detailsMode);

            if (_detailsMode == BorderMode.Manual)
            {
                _detailsUniform = EditorGUILayout.Toggle("Uniform Border", _detailsUniform);
                if (_detailsUniform)
                {
                    _detailsUniformValue = EditorGUILayout.FloatField("Border Size (px)", _detailsUniformValue);
                    _detailsBorder = new Vector4(_detailsUniformValue, _detailsUniformValue, _detailsUniformValue, _detailsUniformValue);
                }
                else
                {
                    _detailsBorder = EditorGUILayout.Vector4Field("Border (L, B, R, T)", _detailsBorder);
                }
            }

            // A per-pixel guide preview is only meaningful for a single sprite at a time,
            // since selected sprites may have different dimensions.
            if (selected.Count == 1)
            {
                Texture2D preview = GetPreviewTexture(selected[0]);
                if (preview != null)
                {
                    Vector4 previewBorder = _detailsMode switch
                    {
                        BorderMode.Manual => NineSliceBorderDetector.Clamp(_detailsBorder, preview.width, preview.height),
                        BorderMode.Auto => GetAutoBorderPreview(selected[0]),
                        _ => Vector4.zero
                    };
                    DrawBorderPreview(preview, previewBorder);
                }
            }

            EditorGUILayout.Space();
            if (GUILayout.Button($"Apply to Selected ({selected.Count})", GUILayout.Height(24f)))
            {
                ApplyDetailsToSelected(selected);
            }

            if (_tab == WindowTab.Repack)
            {
                DrawDeletionControls(selected);
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

        /// <summary>Draws an enlarged sprite preview with 9-slice guide lines overlaid.</summary>
        private void DrawBorderPreview(Texture2D texture, Vector4 border)
        {
            Rect bounds = GUILayoutUtility.GetRect(BorderPreviewSize, BorderPreviewSize, GUILayout.Width(BorderPreviewSize));
            Rect imageRect = FitRect(bounds, texture.width, texture.height);
            GUI.DrawTexture(imageRect, texture, ScaleMode.ScaleToFit);

            if (border == Vector4.zero)
            {
                return;
            }

            float scaleX = imageRect.width / texture.width;
            float scaleY = imageRect.height / texture.height;

            float leftX = imageRect.x + border.x * scaleX;
            float rightX = imageRect.xMax - border.z * scaleX;
            // Border.y/w are measured from the bottom-left origin (sprite convention);
            // GUI space grows downward, so top/bottom guides are flipped accordingly.
            float topY = imageRect.y + border.w * scaleY;
            float bottomY = imageRect.yMax - border.y * scaleY;

            EditorGUI.DrawRect(new Rect(leftX, imageRect.y, 1f, imageRect.height), GuideColor);
            EditorGUI.DrawRect(new Rect(rightX, imageRect.y, 1f, imageRect.height), GuideColor);
            EditorGUI.DrawRect(new Rect(imageRect.x, topY, imageRect.width, 1f), GuideColor);
            EditorGUI.DrawRect(new Rect(imageRect.x, bottomY, imageRect.width, 1f), GuideColor);
        }

        private void DrawPackButton()
        {
            using (new EditorGUI.DisabledScope(_sourceFolder == null))
            {
                if (GUILayout.Button("Pack Atlas", GUILayout.Height(32f)))
                {
                    Pack();
                }
            }
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
