using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace AtlasForge.Editor
{
    /// <summary>Repack-tab adding: packs standalone sprites into the atlas as new slices.</summary>
    public sealed partial class AtlasForgeWindow
    {
        private bool CanAddToRepackAtlas => _tab == WindowTab.Repack && _repackSet.IsLoaded && _repackSet.Atlases.Count == 1;

        /// <summary>Grid-toolbar dropdown that adds sprites from the Project selection or a file browser.</summary>
        private void DrawAddSpritesButton()
        {
            var content = new GUIContent("Add Sprites", "Pack textures into this atlas as new slices. You can also drop them onto the grid.");

            // Keep the button's own rect: the menu is anchored under it.
            Rect buttonRect = GUILayoutUtility.GetRect(content, EditorStyles.toolbarDropDown);
            if (!EditorGUI.DropdownButton(buttonRect, content, FocusType.Passive, EditorStyles.toolbarDropDown))
            {
                return;
            }

            var menu = new GenericMenu();
            List<string> selected = AddablePaths(Selection.GetFiltered<Texture2D>(SelectionMode.Assets));
            var addSelected = new GUIContent($"Selected in Project ({selected.Count})");
            if (selected.Count > 0)
            {
                menu.AddItem(addSelected, false, () => AddSpritesLater(selected));
            }
            else
            {
                menu.AddDisabledItem(addSelected);
            }

            menu.AddItem(new GUIContent("Browse…"), false, BrowseForSprite);
            menu.DropDown(buttonRect);
        }

        private void BrowseForSprite()
        {
            string folder = Path.GetDirectoryName(_repackSet.Atlases[0].AtlasPath);
            string file = EditorUtility.OpenFilePanelWithFilters("Add Sprite to Atlas", folder, new[] { "Image", "png,jpg,jpeg" });
            if (string.IsNullOrEmpty(file))
            {
                return;
            }

            string path = FileUtil.GetProjectRelativePath(file.Replace('\\', '/'));
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", System.StringComparison.Ordinal))
            {
                EditorUtility.DisplayDialog("AtlasForge", "Pick a texture inside this project's Assets folder.", "OK");
                return;
            }

            AddSpritesLater(new List<string> { path });
        }

        /// <summary>Accepts textures dragged from the Project window onto the grid.</summary>
        private void HandleSpriteDrop(Rect gridArea)
        {
            Event current = Event.current;
            if (!CanAddToRepackAtlas || current.type is not (EventType.DragUpdated or EventType.DragPerform) ||
                !gridArea.Contains(current.mousePosition))
            {
                return;
            }

            List<string> paths = AddablePaths(DragAndDrop.objectReferences.OfType<Texture2D>());
            if (paths.Count == 0)
            {
                return;
            }

            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (current.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                AddSpritesLater(paths);
            }

            current.Use();
        }

        /// <summary>Paths of the textures that can become slices of the repack atlas (not the atlas itself).</summary>
        private List<string> AddablePaths(IEnumerable<Texture2D> textures) =>
            textures.Select(AssetDatabase.GetAssetPath)
                .Where(path => path != _repackSet.Atlases[0].AtlasPath && SpriteAdder.CanAdd(path))
                .Distinct()
                .ToList();

        /// <summary>Runs after the current GUI pass (or menu callback): adding shows dialogs and reloads the slices being drawn.</summary>
        private void AddSpritesLater(List<string> paths)
        {
            EditorApplication.delayCall += () =>
            {
                AddSprites(paths);
                Repaint();
            };
        }

        /// <summary>
        /// Adds each texture to the repack atlas as a new slice and repacks it. Sprites keep their
        /// pivot, 9-slice border, outline and physics shape. Optionally, every reference to a
        /// source sprite is redirected to its new slice; the source files themselves are left alone.
        /// </summary>
        private void AddSprites(List<string> paths)
        {
            if (!CanAddToRepackAtlas)
            {
                return;
            }

            AtlasSlices slices = _repackSet.Atlases[0];
            var names = new HashSet<string>(slices.Entries.Select(entry => entry.Name));
            var ids = new HashSet<long>(slices.InternalIds);
            var added = new List<AddedSprite>();
            try
            {
                foreach (string path in paths)
                {
                    string name = SpriteAdder.UniqueName(Path.GetFileNameWithoutExtension(path), names);
                    if (!SpriteAdder.TryLoad(path, name, out AddedSprite sprite, out string error))
                    {
                        EditorUtility.DisplayDialog("AtlasForge", error, "OK");
                        return;
                    }

                    sprite.InternalId = AtlasMerger.NewUniqueId(ids);
                    added.Add(sprite);
                }

                if (added.Count == 0)
                {
                    return;
                }

                List<AtlasSpriteEntry> entries = slices.Entries.Concat(added.Select(sprite => sprite.Entry)).ToList();
                if (!SpriteAtlasBuilder.TryPack(entries, _padding, _maxAtlasSize, _forcePowerOfTwo, out int width, out int height))
                {
                    EditorUtility.DisplayDialog(
                        "AtlasForge", $"The slices don't fit within {_maxAtlasSize}px with the new sprites. Increase Max Atlas Size or lower Padding.", "OK");
                    return;
                }

                // References to a source sprite can follow it into the atlas.
                var bySource = added.Where(sprite => sprite.IsSprite).ToDictionary(sprite => sprite.SourceGuid);
                var referencingFiles = new List<string>();
                int references = 0;
                if (bySource.Count > 0 && SpriteReferenceRewriter.IsTextSerialization)
                {
                    referencingFiles = SpriteReferenceRewriter.FindFilesMentioning(bySource.Keys.ToList());
                    references = SpriteReferenceRewriter.CountReferences(
                        referencingFiles, (guid, id) => id == SliceExporter.SingleSpriteFileId && bySource.ContainsKey(guid));
                }

                float oldFill = SpriteAtlasBuilder.FillRatio(slices.Entries, slices.Width, slices.Height);
                float newFill = SpriteAtlasBuilder.FillRatio(entries, width, height);
                var message = new StringBuilder($"Add {added.Count} sprites to '{_repackAtlas.name}'?\n\n");
                foreach (AddedSprite sprite in added.Take(10))
                {
                    message.Append($"{sprite.SourcePath}  →  {sprite.Entry.Name} ({sprite.Entry.Width}x{sprite.Entry.Height})\n");
                }

                if (added.Count > 10)
                {
                    message.Append($"… and {added.Count - 10} more\n");
                }

                message.Append($"\nThe atlas is repacked: {slices.Width}x{slices.Height} ({oldFill:P1} filled)  →  {width}x{height} ({newFill:P1} filled).")
                    .Append("\nReferences to existing slices are unaffected; the source files are left in place.");
                bool redirect = false;
                if (references > 0)
                {
                    message.Append($"\n\n{references} references point at the source sprites. Redirect them to the new slices?");
                    int choice = EditorUtility.DisplayDialogComplex("AtlasForge", message.ToString(), "Add and Redirect", "Cancel", "Add Only");
                    if (choice == 1)
                    {
                        return;
                    }

                    redirect = choice == 0;
                }
                else if (!EditorUtility.DisplayDialog("AtlasForge", message.ToString(), "Add", "Cancel"))
                {
                    return;
                }

                if (redirect && !SpriteReferenceRewriter.PrepareOpenScenes())
                {
                    return;
                }

                CloseSpriteEditor();

                // Existing slices keep their imported borders; pending edits stay pending for the next Repack.
                Vector4[] borders = slices.Entries.Select(entry => entry.Border).ToArray();
                if (!AtlasRepacker.Apply(slices, borders, new HashSet<long>(), true, width, height, out string applyError, added))
                {
                    EditorUtility.DisplayDialog("AtlasForge", applyError, "OK");
                    return;
                }

                int rewritten = 0;
                int changedFiles = 0;
                if (redirect)
                {
                    SpriteReferenceRewriter.Rewrite(
                        referencingFiles,
                        (string guid, long fileId, out SpriteReferenceRewriter.SpriteTarget target) =>
                        {
                            target = default;
                            if (fileId != SliceExporter.SingleSpriteFileId || !bySource.TryGetValue(guid, out AddedSprite sprite))
                            {
                                return false;
                            }

                            target = new SpriteReferenceRewriter.SpriteTarget(
                                slices.AtlasGuid, sprite.InternalId, sprite.Entry.Name, slices.AtlasPath);
                            return true;
                        },
                        out rewritten,
                        out changedFiles);
                }

                var log = new StringBuilder($"[AtlasForge] Added {added.Count} sprites to '{slices.AtlasPath}': ")
                    .Append(string.Join(", ", added.Select(sprite => sprite.Entry.Name)))
                    .Append($". Repacked {slices.Width}x{slices.Height} → {width}x{height}.");
                if (redirect)
                {
                    log.Append($" Redirected {rewritten} references in {changedFiles} files to the new slices.");
                }

                Debug.Log(log.ToString());

                // The repack baked any rect edits into the atlas. Select the new slices to show where they went.
                _repackSet.ClearRectEdits();
                LoadRepackSet();
                var newIds = new HashSet<long>(added.Select(sprite => sprite.InternalId));
                _focusedConfig = null;
                foreach (SpriteBorderConfig config in _repackSet.Configs)
                {
                    config.Selected = newIds.Contains(config.SliceId);
                    _focusedConfig ??= config.Selected ? config : null;
                }

                EditorGUIUtility.PingObject(_repackAtlas);
            }
            finally
            {
                added.ForEach(sprite => sprite.Dispose());
            }
        }
    }
}
