using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AtlasForge.Editor
{
    /// <summary>Merge Atlases tab: combines several sliced atlases into one, keeping every reference intact.</summary>
    public sealed partial class AtlasForgeWindow
    {
        [SerializeField] private List<Texture2D> _mergeAtlases = new() { null, null };
        [SerializeField] private Texture2D _mergeTarget;
        [SerializeField] private bool _trashMergedSources;
        [SerializeField] private AtlasSliceSet _mergeSet = new();

        private void DrawMergeSourceSection()
        {
            EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);

            bool changed = false;
            int removeIndex = -1;
            for (int i = 0; i < _mergeAtlases.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    _mergeAtlases[i] = AtlasField($"Atlas {i + 1}", _mergeAtlases[i]);
                    changed |= EditorGUI.EndChangeCheck();

                    if (GUILayout.Button("−", GUILayout.Width(22f)))
                    {
                        removeIndex = i;
                    }
                }
            }

            if (removeIndex >= 0)
            {
                changed |= _mergeAtlases[removeIndex] != null;
                _mergeAtlases.RemoveAt(removeIndex);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add Atlas"))
                {
                    _mergeAtlases.Add(null);
                }

                if (GUILayout.Button(new GUIContent("Add Selected", "Adds the sliced atlases selected in the Project window.")))
                {
                    changed |= AddSelectedAtlases();
                }
            }

            List<Texture2D> atlases = ValidMergeAtlases();
            if (atlases.Count > 0)
            {
                if (!atlases.Contains(_mergeTarget))
                {
                    _mergeTarget = atlases[0];
                }

                int targetIndex = EditorGUILayout.Popup(
                    new GUIContent("Merge Into", "This atlas keeps its file, GUID and slice IDs; the others' slices are moved into it."),
                    atlases.IndexOf(_mergeTarget),
                    atlases.Select(atlas => atlas.name).ToArray());
                _mergeTarget = atlases[targetIndex];
            }

            _trashMergedSources = EditorGUILayout.Toggle(
                new GUIContent("Trash Merged Atlases", "Move the other atlases to the OS trash once their references point at the merged atlas."),
                _trashMergedSources);

            if (changed)
            {
                LoadMergeSet();
            }

            if (_mergeSet.Error != null)
            {
                EditorGUILayout.HelpBox(_mergeSet.Error, MessageType.Warning);
            }
            else if (_mergeSet.IsLoaded && _mergeSet.Atlases.Count > 0)
            {
                EditorGUILayout.LabelField(
                    $"{_mergeSet.Atlases.Count} atlases  ·  {_mergeSet.Atlases.Sum(atlas => atlas.Entries.Count)} slices",
                    EditorStyles.miniLabel);
            }
        }

        private void DrawMergeButton()
        {
            using (new EditorGUI.DisabledScope(!_mergeSet.IsLoaded || _mergeSet.Atlases.Count < 2))
            {
                if (GUILayout.Button("Merge Atlases", GUILayout.Height(32f)))
                {
                    Merge();
                }
            }
        }

        /// <summary>Distinct, assigned atlases from the list, in list order.</summary>
        private List<Texture2D> ValidMergeAtlases() => _mergeAtlases.Where(atlas => atlas != null).Distinct().ToList();

        /// <summary>Appends the sliced textures selected in the Project window; returns whether any were added.</summary>
        private bool AddSelectedAtlases()
        {
            bool added = false;
            foreach (Texture2D texture in Selection.GetFiltered<Texture2D>(SelectionMode.Assets))
            {
                if (_mergeAtlases.Contains(texture) ||
                    AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture)) is not TextureImporter { spriteImportMode: SpriteImportMode.Multiple })
                {
                    continue;
                }

                // Fill empty rows first so the list doesn't grow needlessly.
                int empty = _mergeAtlases.IndexOf(null);
                if (empty >= 0)
                {
                    _mergeAtlases[empty] = texture;
                }
                else
                {
                    _mergeAtlases.Add(texture);
                }

                added = true;
            }

            return added;
        }

        /// <summary>Loads the merge atlases' slices on first use, e.g. after a domain reload dropped them.</summary>
        private void EnsureMergeLoaded()
        {
            if (!_mergeSet.IsLoaded && _mergeSet.Error == null && ValidMergeAtlases().Count > 0)
            {
                LoadMergeSet();
            }
        }

        private void LoadMergeSet()
        {
            _mergeSet.Load(ValidMergeAtlases());
            DropStaleFocus();
        }

        /// <summary>Plans the merge, confirms it with the user, then writes it and reports what changed.</summary>
        private void Merge()
        {
            string targetGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(_mergeTarget));
            AtlasSlices target = _mergeSet.Atlases.FirstOrDefault(atlas => atlas.AtlasGuid == targetGuid);
            if (target == null)
            {
                EditorUtility.DisplayDialog("AtlasForge", "Pick the atlas to merge into.", "OK");
                return;
            }

            if (!AtlasMerger.TryPlan(
                    _mergeSet.Atlases, target, _padding, _maxAtlasSize, _forcePowerOfTwo,
                    out AtlasMerger.MergePlan plan, out string error))
            {
                EditorUtility.DisplayDialog("AtlasForge", error, "OK");
                return;
            }

            var borders = plan.Slices
                .Select(slice => ResolveBorder(_mergeSet.FindConfig(slice.Source, slice.SourceIndex), slice.Entry))
                .ToArray();
            float fill = SpriteAtlasBuilder.FillRatio(plan.Slices.Select(slice => slice.Entry).ToList(), plan.Width, plan.Height);
            int renamed = plan.RenamedSlices.Count();

            string message =
                $"Merge {plan.Sources.Count + 1} atlases ({plan.Slices.Count} slices) into '{_mergeTarget.name}'?\n\n" +
                $"Result: {plan.Width}x{plan.Height} ({fill:P1} filled)\n" +
                $"{plan.ReferencingFiles.Count} files mention the merged atlases; their slice references are rewritten.\n" +
                (renamed > 0 ? $"{renamed} slices are renamed to avoid name clashes (listed in the Console afterwards).\n" : "") +
                (_trashMergedSources
                    ? "The other atlases are moved to the trash.\n"
                    : "The other atlases are left in place, no longer referenced.\n") +
                "\nThis rewrites project files and can't be undone with Undo. Commit or back up first.";
            if (!EditorUtility.DisplayDialog("AtlasForge", message, "Merge", "Cancel"))
            {
                return;
            }

            CloseSpriteEditor();
            if (!AtlasMerger.Apply(plan, borders, _trashMergedSources, out string summary))
            {
                EditorUtility.DisplayDialog("AtlasForge", summary, "OK");
                return;
            }

            Debug.Log("[AtlasForge] " + summary);

            // Only the merged atlas is left to work with.
            _mergeAtlases = new List<Texture2D> { _mergeTarget };
            LoadMergeSet();
            EditorGUIUtility.PingObject(_mergeTarget);
        }
    }
}
