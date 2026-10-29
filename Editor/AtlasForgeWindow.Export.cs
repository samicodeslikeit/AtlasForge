using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace AtlasForge.Editor
{
    /// <summary>Repack-tab export: writes selected slices out as standalone sprites, optionally removing them from the atlas.</summary>
    public sealed partial class AtlasForgeWindow
    {
        [SerializeField] private DefaultAsset _exportFolder;
        [SerializeField] private bool _removeExported;
        [SerializeField] private bool _showExport;

        private string _exportName;
        private SpriteBorderConfig _exportNameFor;

        /// <summary>Repack-tab group in the selection panel that exports the selected slices.</summary>
        private void DrawExportControls(List<SpriteBorderConfig> selected)
        {
            _showExport = EditorGUILayout.Foldout(
                _showExport, new GUIContent("Export as Sprite", "Write the selected slices out as standalone sprite textures."), true);
            if (!_showExport)
            {
                return;
            }

            EditorGUI.indentLevel++;
            EditorGUI.BeginChangeCheck();
            var folder = (DefaultAsset)EditorGUILayout.ObjectField(
                new GUIContent("Folder", "Where exported sprites are written. Empty uses the atlas's folder."),
                _exportFolder, typeof(DefaultAsset), false);
            if (EditorGUI.EndChangeCheck())
            {
                _exportFolder = folder != null && AssetDatabase.IsValidFolder(AssetDatabase.GetAssetPath(folder)) ? folder : null;
            }

            // A custom name only makes sense for one slice; a batch keeps the slice names.
            if (selected.Count == 1)
            {
                if (_exportNameFor != selected[0])
                {
                    _exportNameFor = selected[0];
                    _exportName = selected[0].Name;
                }

                _exportName = EditorGUILayout.TextField("Name", _exportName);
            }

            _removeExported = EditorGUILayout.Toggle(
                new GUIContent(
                    "Remove from Atlas",
                    "Deletes the slices from the atlas after exporting, repacks the rest and redirects their references to the exported sprites. " +
                    "Off exports a copy and leaves the atlas untouched."),
                _removeExported);
            EditorGUI.indentLevel--;

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                string label = _removeExported ? $"Export and Remove ({selected.Count})" : $"Export Copy ({selected.Count})";
                if (GUILayout.Button(label, GUILayout.Width(170f)))
                {
                    // Run after this GUI pass: exporting shows dialogs and reloads the slices being drawn.
                    var toExport = new List<SpriteBorderConfig>(selected);
                    EditorApplication.delayCall += () =>
                    {
                        ExportSlices(toExport);
                        Repaint();
                    };
                }
            }
        }

        private string ExportFolderPath =>
            _exportFolder != null ? AssetDatabase.GetAssetPath(_exportFolder) : Path.GetDirectoryName(_repackSet.Atlases[0].AtlasPath).Replace('\\', '/');

        /// <summary>
        /// Exports each selected slice as a Single-mode sprite texture with the slice's pivot,
        /// border (including pending edits), outline and physics shape. With Remove from Atlas the
        /// slices are then deleted from the atlas and every reference to them is redirected to the
        /// exported sprite.
        /// </summary>
        private void ExportSlices(List<SpriteBorderConfig> selected)
        {
            if (!_repackSet.IsLoaded || _repackSet.Atlases.Count != 1)
            {
                return;
            }

            AtlasSlices slices = _repackSet.Atlases[0];
            List<AtlasSpriteEntry> entries = slices.Entries;
            var exports = new List<(int Index, SpriteBorderConfig Config)>();
            for (int i = 0; i < entries.Count; i++)
            {
                SpriteBorderConfig config = _repackSet.FindConfig(slices, i);
                if (config != null && selected.Contains(config))
                {
                    exports.Add((i, config));
                }
            }

            if (exports.Count == 0)
            {
                return;
            }

            if (_removeExported && exports.Count == entries.Count)
            {
                EditorUtility.DisplayDialog("AtlasForge", "Removing every slice would leave the atlas empty. Keep at least one.", "OK");
                return;
            }

            if (_removeExported && !SpriteReferenceRewriter.IsTextSerialization)
            {
                EditorUtility.DisplayDialog(
                    "AtlasForge",
                    "Removing slices needs Asset Serialization set to Force Text (Project Settings > Editor) so their references can be redirected.",
                    "OK");
                return;
            }

            string folder = ExportFolderPath;
            var paths = new List<string>(exports.Count);
            var planned = new HashSet<string>();
            foreach ((int index, _) in exports)
            {
                string name = exports.Count == 1 ? _exportName?.Trim() : entries[index].Name;
                if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    EditorUtility.DisplayDialog("AtlasForge", $"'{name}' is not a valid file name.", "OK");
                    return;
                }

                string path = UniqueExportPath(folder, name, planned);
                planned.Add(path);
                paths.Add(path);
            }

            var ids = new HashSet<long>(exports.Select(export => slices.InternalIds[export.Index]));
            var referencingFiles = new List<string>();
            int references = 0;
            bool relayout = false;
            int width = slices.Width;
            int height = slices.Height;
            if (_removeExported)
            {
                referencingFiles = SpriteReferenceRewriter.FindFilesMentioning(new[] { slices.AtlasGuid });
                references = SpriteReferenceRewriter.CountReferences(
                    referencingFiles, (guid, id) => guid == slices.AtlasGuid && ids.Contains(id));

                // Re-layout the remaining slices so the removed ones leave no gap behind.
                List<AtlasSpriteEntry> kept = entries.Where((_, index) => !ids.Contains(slices.InternalIds[index])).ToList();
                relayout = SpriteAtlasBuilder.TryPack(kept, _padding, _maxAtlasSize, _forcePowerOfTwo, out width, out height);
            }

            var message = new StringBuilder($"Export {exports.Count} slices from '{_repackAtlas.name}' to:\n\n");
            foreach (string path in paths.Take(10))
            {
                message.Append(path).Append('\n');
            }

            if (paths.Count > 10)
            {
                message.Append($"… and {paths.Count - 10} more\n");
            }

            if (!_removeExported)
            {
                message.Append("\nThe atlas is left untouched.");
            }
            else
            {
                message.Append($"\nThe slices are removed from the atlas and their {references} references are redirected to the exported sprites.");
                message.Append(relayout
                    ? $"\nThe remaining slices are repacked: {slices.Width}x{slices.Height} → {width}x{height}."
                    : $"\nThe remaining slices don't fit within {_maxAtlasSize}px, so the layout stays as it is and the removed slices' pixels remain in the image.");
            }
            if (!EditorUtility.DisplayDialog("AtlasForge", message.ToString(), _removeExported ? "Export and Remove" : "Export", "Cancel"))
            {
                return;
            }

            if (references > 0 && !SpriteReferenceRewriter.PrepareOpenScenes())
            {
                return;
            }

            CloseSpriteEditor();
            for (int i = 0; i < exports.Count; i++)
            {
                (int index, SpriteBorderConfig config) = exports[i];
                if (!SliceExporter.Export(slices, index, ResolveBorder(config, entries[index]), paths[i], out string error))
                {
                    EditorUtility.DisplayDialog(
                        "AtlasForge", $"{error}\n\n{i} of {exports.Count} slices were exported; the atlas was not changed.", "OK");
                    return;
                }
            }

            var log = new StringBuilder($"[AtlasForge] Exported {exports.Count} slices from '{slices.AtlasPath}': {string.Join(", ", paths)}.");
            if (_removeExported)
            {
                // Kept slices keep their imported borders; pending edits stay pending for the next Repack.
                Vector4[] borders = entries.Select(entry => entry.Border).ToArray();
                if (!AtlasRepacker.Apply(slices, borders, ids, relayout, width, height, out string error))
                {
                    EditorUtility.DisplayDialog("AtlasForge", $"{error}\n\nThe sprites were exported but the atlas was not changed.", "OK");
                    return;
                }

                var targets = new Dictionary<long, SpriteReferenceRewriter.SpriteTarget>();
                for (int i = 0; i < exports.Count; i++)
                {
                    targets[slices.InternalIds[exports[i].Index]] = new SpriteReferenceRewriter.SpriteTarget(
                        AssetDatabase.AssetPathToGUID(paths[i]),
                        SliceExporter.SingleSpriteFileId,
                        Path.GetFileNameWithoutExtension(paths[i]),
                        paths[i]);
                }

                int rewritten = 0;
                int changedFiles = 0;
                if (references > 0)
                {
                    SpriteReferenceRewriter.Rewrite(
                        referencingFiles,
                        (string guid, long fileId, out SpriteReferenceRewriter.SpriteTarget target) =>
                        {
                            target = default;
                            return guid == slices.AtlasGuid && targets.TryGetValue(fileId, out target);
                        },
                        out rewritten,
                        out changedFiles);
                }

                log.Append($" Removed them from the atlas and redirected {rewritten} references in {changedFiles} files.");
                if (relayout)
                {
                    log.Append($" Repacked the atlas: {slices.Width}x{slices.Height} → {width}x{height}.");
                }
            }

            Debug.Log(log.ToString());

            _exportNameFor = null;
            foreach ((_, SpriteBorderConfig config) in exports)
            {
                config.Selected = false;
            }

            _focusedConfig = null;
            if (_removeExported)
            {
                // Removing rewrote the atlas, rect edits included.
                _repackSet.ClearRectEdits();
                LoadRepackSet();
            }

            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<Texture2D>(paths[0]));
        }

        /// <summary>A free .png path for the name in the folder, numbered if the file (or an earlier export) already exists.</summary>
        private static string UniqueExportPath(string folder, string name, HashSet<string> planned)
        {
            string path = $"{folder}/{name}.png";
            for (int n = 1; planned.Contains(path) || File.Exists(path); n++)
            {
                path = $"{folder}/{name} {n}.png";
            }

            return path;
        }
    }
}
