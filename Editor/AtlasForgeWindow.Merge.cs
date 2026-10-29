using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
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

        private ReorderableList _mergeList;

        // Set by list callbacks (some run outside OnGUI, from menus); the set reloads on the next draw.
        private bool _mergeListChanged;

        private void DrawMergeSourceSection()
        {
            // The list wraps _mergeAtlases itself, so rebuild it whenever that list is replaced.
            if (_mergeList == null || _mergeList.list != _mergeAtlases)
            {
                _mergeList = CreateMergeList();
            }

            _mergeList.DoLayoutList();
            HandleAtlasDrop(GUILayoutUtility.GetLastRect());

            bool changed = _mergeListChanged;
            _mergeListChanged = false;

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
        }

        /// <summary>Footer status: how many atlases and slices are loaded for the merge.</summary>
        private string MergeStatus()
        {
            if (!_mergeSet.IsLoaded || _mergeSet.Atlases.Count == 0)
            {
                return "Add two or more sliced atlases.";
            }

            string status = $"{_mergeSet.Atlases.Count} atlases  ·  {_mergeSet.Atlases.Sum(atlas => atlas.Entries.Count)} slices";
            return _mergeSet.Atlases.Count < 2 ? status + "  ·  add another atlas to merge" : status;
        }

        private ReorderableList CreateMergeList()
        {
            float lineHeight = EditorGUIUtility.singleLineHeight;
            var list = new ReorderableList(_mergeAtlases, typeof(Texture2D), true, true, true, true)
            {
                elementHeight = lineHeight + 4f,
                drawHeaderCallback = rect => EditorGUI.LabelField(
                    rect, new GUIContent("Atlases to Merge", "Drop sliced atlases here, or add them with the + button.")),
                drawElementCallback = (rect, index, _, _) =>
                {
                    rect = new Rect(rect.x, rect.y + 2f, rect.width, lineHeight);
                    EditorGUI.BeginChangeCheck();
                    var atlas = (Texture2D)EditorGUI.ObjectField(rect, _mergeAtlases[index], typeof(Texture2D), false);
                    if (EditorGUI.EndChangeCheck())
                    {
                        _mergeAtlases[index] = atlas;
                        _mergeListChanged = true;
                    }
                },
                onRemoveCallback = reorderable =>
                {
                    _mergeListChanged |= _mergeAtlases[reorderable.index] != null;
                    _mergeAtlases.RemoveAt(reorderable.index);
                    reorderable.index = Mathf.Clamp(reorderable.index, 0, _mergeAtlases.Count - 1);
                },
                onAddDropdownCallback = (rect, _) =>
                {
                    var menu = new GenericMenu();
                    menu.AddItem(new GUIContent("Empty Slot"), false, () =>
                    {
                        _mergeAtlases.Add(null);
                        Repaint();
                    });
                    List<Texture2D> selected = SelectedSlicedAtlases().ToList();
                    var addSelected = new GUIContent($"Selected in Project ({selected.Count})");
                    if (selected.Count > 0)
                    {
                        menu.AddItem(addSelected, false, () =>
                        {
                            _mergeListChanged |= AddAtlases(selected);
                            Repaint();
                        });
                    }
                    else
                    {
                        menu.AddDisabledItem(addSelected);
                    }

                    menu.DropDown(rect);
                }
            };

            return list;
        }

        /// <summary>Accepts sliced atlases dragged from the Project window onto the list.</summary>
        private void HandleAtlasDrop(Rect rect)
        {
            Event current = Event.current;
            if (current.type is not (EventType.DragUpdated or EventType.DragPerform) || !rect.Contains(current.mousePosition))
            {
                return;
            }

            List<Texture2D> atlases = DragAndDrop.objectReferences.OfType<Texture2D>().Where(IsSlicedAtlas).ToList();
            if (atlases.Count == 0)
            {
                return;
            }

            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (current.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                _mergeListChanged |= AddAtlases(atlases);
            }

            current.Use();
        }

        /// <summary>Distinct, assigned atlases from the list, in list order.</summary>
        private List<Texture2D> ValidMergeAtlases() => _mergeAtlases.Where(atlas => atlas != null).Distinct().ToList();

        private static IEnumerable<Texture2D> SelectedSlicedAtlases() =>
            Selection.GetFiltered<Texture2D>(SelectionMode.Assets).Where(IsSlicedAtlas);

        private static bool IsSlicedAtlas(Texture2D texture) =>
            AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture)) is TextureImporter { spriteImportMode: SpriteImportMode.Multiple };

        /// <summary>Appends the given atlases that aren't listed yet; returns whether any were added.</summary>
        private bool AddAtlases(IEnumerable<Texture2D> textures)
        {
            bool added = false;
            foreach (Texture2D texture in textures)
            {
                if (_mergeAtlases.Contains(texture))
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

            // Only the merged atlas is left to work with; rect edits are now part of it.
            _mergeSet.ClearRectEdits();
            _mergeAtlases = new List<Texture2D> { _mergeTarget };
            LoadMergeSet();
            EditorGUIUtility.PingObject(_mergeTarget);
        }
    }
}
