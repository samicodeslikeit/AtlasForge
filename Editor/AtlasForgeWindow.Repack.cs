using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace AtlasForge.Editor
{
    /// <summary>Repack Atlas tab: squeezes the empty space out of one sliced atlas in place.</summary>
    public sealed partial class AtlasForgeWindow
    {
        [SerializeField] private Texture2D _repackAtlas;
        [SerializeField] private AtlasSliceSet _repackSet = new();

        private void DrawRepackSourceSection()
        {
            EditorGUI.BeginChangeCheck();
            _repackAtlas = AtlasField("Sliced Atlas", _repackAtlas);
            if (EditorGUI.EndChangeCheck())
            {
                // Border edits belong to the previous atlas's slices.
                _repackSet.Configs.Clear();
                LoadRepackSet();
            }

            if (_repackSet.Error != null)
            {
                EditorGUILayout.HelpBox(_repackSet.Error, MessageType.Warning);
            }
        }

        /// <summary>Footer status: atlas size, slice count, fill and pending deletions.</summary>
        private string RepackStatus()
        {
            if (!_repackSet.IsLoaded || _repackSet.Atlases.Count != 1)
            {
                return "No atlas selected.";
            }

            AtlasSlices slices = _repackSet.Atlases[0];
            float fill = SpriteAtlasBuilder.FillRatio(slices.Entries, slices.Width, slices.Height);
            int marked = _repackSet.Configs.Count(config => config.MarkedForDeletion);
            return $"{slices.Width}×{slices.Height}  ·  {slices.Entries.Count} slices  ·  {fill:P1} filled" +
                   (marked > 0 ? $"  ·  {marked} marked for deletion" : "");
        }

        /// <summary>Repack-tab row in the selection panel that marks or unmarks the selected slices for deletion.</summary>
        private void DrawDeletionControls(List<SpriteBorderConfig> selected)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(new GUIContent(
                    "Deletion", "Marked slices are removed on the next Repack. Delete toggles the mark on the selection."));
                if (GUILayout.Button($"Mark ({selected.Count})", EditorStyles.miniButtonLeft))
                {
                    SetMarkedForDeletion(selected, true);
                }

                using (new EditorGUI.DisabledScope(!selected.Any(config => config.MarkedForDeletion)))
                {
                    if (GUILayout.Button("Unmark", EditorStyles.miniButtonRight))
                    {
                        SetMarkedForDeletion(selected, false);
                    }
                }
            }
        }

        /// <summary>Marks or unmarks the given slices, then deselects them so the next batch can be picked.</summary>
        private void SetMarkedForDeletion(List<SpriteBorderConfig> configs, bool marked)
        {
            foreach (SpriteBorderConfig config in configs)
            {
                config.MarkedForDeletion = marked;
                config.Selected = false;
            }

            _focusedConfig = null;
            Repaint();
        }

        /// <summary>Selects (only) the slices that are pixel-identical copies of an earlier slice.</summary>
        private void SelectDuplicateSlices(AtlasSlices slices)
        {
            int[] groups = AtlasRepacker.FindIdenticalSlices(slices.Entries);
            int duplicates = 0;
            for (int i = 0; i < groups.Length; i++)
            {
                SpriteBorderConfig config = _repackSet.FindConfig(slices, i);
                if (config == null)
                {
                    continue;
                }

                config.Selected = groups[i] != i;
                duplicates += config.Selected ? 1 : 0;
            }

            _focusedConfig = null;
            if (duplicates == 0)
            {
                EditorUtility.DisplayDialog("AtlasForge", "No duplicate slices found.", "OK");
            }
        }

        /// <summary>Loads the repack atlas's slices on first use, e.g. after a domain reload dropped them.</summary>
        private void EnsureRepackLoaded()
        {
            if (!_repackSet.IsLoaded && _repackSet.Error == null && _repackAtlas != null)
            {
                LoadRepackSet();
            }
        }

        private void LoadRepackSet()
        {
            _repackSet.Load(_repackAtlas != null ? new[] { _repackAtlas } : System.Array.Empty<Texture2D>());
            DropStaleFocus();
        }

        /// <summary>
        /// Packs the kept slices into the tightest layout and, once confirmed, rewrites the atlas in
        /// place: slices marked for deletion are removed (references to deleted duplicates are
        /// redirected to their kept, pixel-identical twin) and 9-slice border edits are applied.
        /// If the layout can't shrink, the deletions and border edits are still offered on their own.
        /// </summary>
        private void Repack()
        {
            AtlasSlices slices = _repackSet.Atlases[0];
            List<AtlasSpriteEntry> entries = slices.Entries;
            var borders = new Vector4[entries.Count];
            var kept = new List<int>();
            var deleted = new List<int>();
            bool bordersChanged = false;
            bool rectsChanged = false;
            for (int i = 0; i < entries.Count; i++)
            {
                SpriteBorderConfig config = _repackSet.FindConfig(slices, i);
                if (config is { MarkedForDeletion: true })
                {
                    deleted.Add(i);
                    continue;
                }

                kept.Add(i);
                borders[i] = ResolveBorder(config, entries[i]);
                bordersChanged |= borders[i] != entries[i].Border;
                rectsChanged |= slices.IsRectEdited(i);
            }

            if (kept.Count == 0)
            {
                EditorUtility.DisplayDialog("AtlasForge", "Every slice is marked for deletion. Keep at least one.", "OK");
                return;
            }

            if (deleted.Count > 0 && !SpriteReferenceRewriter.IsTextSerialization)
            {
                EditorUtility.DisplayDialog(
                    "AtlasForge",
                    "Deleting slices needs Asset Serialization set to Force Text (Project Settings > Editor) so their references can be redirected.",
                    "OK");
                return;
            }

            // A deleted duplicate hands its references over to a kept slice with identical pixels.
            var deletedIds = new HashSet<long>(deleted.Select(index => slices.InternalIds[index]));
            var redirects = new Dictionary<long, int>();
            var referencingFiles = new List<string>();
            int redirectedReferences = 0;
            int brokenReferences = 0;
            if (deleted.Count > 0)
            {
                int[] groups = AtlasRepacker.FindIdenticalSlices(entries);
                var keptByGroup = new Dictionary<int, int>();
                foreach (int index in kept)
                {
                    keptByGroup.TryAdd(groups[index], index);
                }

                foreach (int index in deleted)
                {
                    if (keptByGroup.TryGetValue(groups[index], out int twin))
                    {
                        redirects[slices.InternalIds[index]] = twin;
                    }
                }

                referencingFiles = SpriteReferenceRewriter.FindFilesMentioning(new[] { slices.AtlasGuid });
                redirectedReferences = SpriteReferenceRewriter.CountReferences(
                    referencingFiles, (guid, id) => guid == slices.AtlasGuid && redirects.ContainsKey(id));
                brokenReferences = SpriteReferenceRewriter.CountReferences(
                    referencingFiles, (guid, id) => guid == slices.AtlasGuid && deletedIds.Contains(id) && !redirects.ContainsKey(id));
            }

            List<AtlasSpriteEntry> keptEntries = kept.Select(index => entries[index]).ToList();
            string atlasName = _repackAtlas.name;
            int oldWidth = slices.Width;
            int oldHeight = slices.Height;
            float oldFill = SpriteAtlasBuilder.FillRatio(entries, oldWidth, oldHeight);
            bool packed = SpriteAtlasBuilder.TryPack(keptEntries, _padding, _maxAtlasSize, _forcePowerOfTwo, out int width, out int height);
            bool shrinks = packed && (long)width * height < (long)oldWidth * oldHeight;

            // Deleting slices frees space, so a fitting layout is always worth rewriting.
            bool relayout = packed && (shrinks || deleted.Count > 0);
            float newFill = relayout ? SpriteAtlasBuilder.FillRatio(keptEntries, width, height) : oldFill;

            var message = new StringBuilder();
            if (relayout)
            {
                message.Append($"Repack '{atlasName}' ({kept.Count} slices)?\n\n")
                    .Append($"{oldWidth}x{oldHeight} ({oldFill:P1} filled)  →  {width}x{height} ({newFill:P1} filled)\n");
            }
            else
            {
                string reason = packed
                    ? $"'{atlasName}' ({oldWidth}x{oldHeight}, {oldFill:P1} filled) can't be packed any tighter with the current settings."
                    : $"The slices don't fit within {_maxAtlasSize}px. Increase Max Atlas Size or lower Padding.";
                if (!bordersChanged && !rectsChanged && deleted.Count == 0)
                {
                    EditorUtility.DisplayDialog("AtlasForge", reason, "OK");
                    return;
                }

                message.Append(reason).Append("\n\nApply the changes below anyway? The layout stays as it is.\n");
            }

            if (deleted.Count > 0)
            {
                message.Append($"\nDelete {deleted.Count} slices.");
                if (redirects.Count > 0)
                {
                    message.Append($" {redirects.Count} of them are duplicates: their {redirectedReferences} references " +
                                   "are redirected to the identical slice that is kept.");
                }

                if (brokenReferences > 0)
                {
                    message.Append($"\n⚠ {brokenReferences} references point at deleted slices with no identical copy and will become Missing.");
                }

                message.Append('\n');
            }

            if (bordersChanged)
            {
                message.Append("\n9-slice border edits are applied.\n");
            }

            if (rectsChanged)
            {
                message.Append(relayout
                    ? "\nMoved or resized slices are re-cut from their new rects.\n"
                    : "\nMoved or resized slices now point at their new rects in the atlas.\n");
            }

            message.Append("\nThe atlas is overwritten in place; references to kept slices are unaffected.");
            string confirmLabel = relayout ? "Repack" : deleted.Count > 0 ? "Delete Slices" : "Apply Changes";
            if (!EditorUtility.DisplayDialog("AtlasForge", message.ToString(), confirmLabel, "Cancel"))
            {
                return;
            }

            if (redirectedReferences > 0 && !SpriteReferenceRewriter.PrepareOpenScenes())
            {
                return;
            }

            CloseSpriteEditor();
            if (!AtlasRepacker.Apply(slices, borders, deletedIds, relayout, width, height, out string error))
            {
                EditorUtility.DisplayDialog("AtlasForge", error, "OK");
                return;
            }

            int rewritten = 0;
            int changedFiles = 0;
            if (redirectedReferences > 0)
            {
                SpriteReferenceRewriter.Rewrite(
                    referencingFiles,
                    (string guid, long fileId, out SpriteReferenceRewriter.SpriteTarget target) =>
                    {
                        target = default;
                        if (guid != slices.AtlasGuid || !redirects.TryGetValue(fileId, out int twin))
                        {
                            return false;
                        }

                        target = new SpriteReferenceRewriter.SpriteTarget(
                            guid, slices.InternalIds[twin], entries[twin].Name, slices.AtlasPath);
                        return true;
                    },
                    out rewritten,
                    out changedFiles);
            }

            var log = new StringBuilder($"[AtlasForge] Updated '{slices.AtlasPath}'");
            if (relayout)
            {
                log.Append($": {oldWidth}x{oldHeight} → {width}x{height} ({oldFill:P1} → {newFill:P1} filled)");
            }

            log.Append('.');
            if (deleted.Count > 0)
            {
                log.Append($" Deleted {deleted.Count} slices: {string.Join(", ", deleted.Select(index => entries[index].Name))}.");
            }

            if (rewritten > 0)
            {
                log.Append($" Redirected {rewritten} references in {changedFiles} files to identical kept slices.");
            }

            if (brokenReferences > 0)
            {
                log.Append($" {brokenReferences} references to deleted slices are now Missing.");
            }

            Debug.Log(log.ToString());

            // Pick up the rewritten layout and borders; rect edits are now part of the atlas.
            _repackSet.ClearRectEdits();
            LoadRepackSet();
            EditorGUIUtility.PingObject(_repackAtlas);
        }

        /// <summary>A single-line Texture2D field (instead of the default large thumbnail slot).</summary>
        private static Texture2D AtlasField(string label, Texture2D atlas, params GUILayoutOption[] options)
        {
            var layout = new GUILayoutOption[options.Length + 1];
            options.CopyTo(layout, 0);
            layout[options.Length] = GUILayout.Height(EditorGUIUtility.singleLineHeight);
            return (Texture2D)EditorGUILayout.ObjectField(label, atlas, typeof(Texture2D), false, layout);
        }

        /// <summary>Clears the grid focus if its sprite is no longer listed on the current tab.</summary>
        private void DropStaleFocus()
        {
            if (_focusedConfig != null && !ActiveConfigs.Contains(_focusedConfig))
            {
                _focusedConfig = null;
            }
        }
    }
}
