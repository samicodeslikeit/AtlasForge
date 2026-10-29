using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace AtlasForge.Editor
{
    /// <summary>
    /// Finds and rewrites references to texture sub-assets (sprite slices) in the project's
    /// text-serialized assets: scenes, prefabs, ScriptableObjects, materials, USS/UXML…
    /// Requires Asset Serialization to be Force Text.
    /// </summary>
    public static class SpriteReferenceRewriter
    {
        /// <summary>Text-serialized asset types that can hold sprite references.</summary>
        private static readonly HashSet<string> ReferencingExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".prefab", ".unity", ".asset", ".mat", ".anim", ".controller", ".overrideController",
            ".playable", ".mask", ".preset", ".spriteatlas", ".spriteatlasv2", ".guiskin",
            ".fontsettings", ".signal", ".lighting", ".mixer", ".uss", ".tss", ".uxml"
        };

        /// <summary>YAML object reference to a texture sub-asset: <c>{fileID: 123, guid: abc…, type: 3}</c>.</summary>
        private static readonly Regex YamlReference = new(
            @"fileID: (-?\d+), guid: ([0-9a-f]{32}), type: 3", RegexOptions.Compiled);

        /// <summary>UI Toolkit asset URL, as written in USS (<c>&amp;</c>) and UXML (<c>&amp;amp;</c>).</summary>
        private static readonly Regex UrlReference = new(
            @"project://database/([^?""'\s)]+)\?fileID=(-?\d+)(&amp;|&)guid=([0-9a-f]{32})(&amp;|&)type=3(#[^""'\s)]*)?",
            RegexOptions.Compiled);

        /// <summary>The slice a rewritten reference should point at instead.</summary>
        public readonly struct SpriteTarget
        {
            public readonly string Guid;
            public readonly long FileId;
            public readonly string Name;
            public readonly string AssetPath;

            public SpriteTarget(string guid, long fileId, string name, string assetPath)
            {
                Guid = guid;
                FileId = fileId;
                Name = name;
                AssetPath = assetPath;
            }
        }

        /// <summary>Returns true with a new target for references that should change, false to leave one as is.</summary>
        public delegate bool Remap(string guid, long fileId, out SpriteTarget target);

        public static bool IsTextSerialization => EditorSettings.serializationMode == SerializationMode.ForceText;

        /// <summary>Text assets under Assets/ whose contents mention any of the given GUIDs.</summary>
        public static List<string> FindFilesMentioning(ICollection<string> guids)
        {
            string[] candidates = AssetDatabase.GetAllAssetPaths()
                .Where(path => path.StartsWith("Assets/", StringComparison.Ordinal) &&
                               ReferencingExtensions.Contains(Path.GetExtension(path)) &&
                               File.Exists(path))
                .ToArray();

            var matches = new List<string>();
            try
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    if (i % 50 == 0)
                    {
                        EditorUtility.DisplayProgressBar(
                            "AtlasForge", "Finding sprite references…", i / (float)candidates.Length);
                    }

                    string text = File.ReadAllText(candidates[i]);
                    if (guids.Any(guid => text.Contains(guid, StringComparison.Ordinal)))
                    {
                        matches.Add(candidates[i]);
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            return matches;
        }

        /// <summary>Counts the sprite references in the given files that match a predicate.</summary>
        public static int CountReferences(IEnumerable<string> files, Func<string, long, bool> match)
        {
            int count = 0;
            foreach (string path in files)
            {
                string text = File.ReadAllText(path);
                foreach (Match reference in YamlReference.Matches(text))
                {
                    if (long.TryParse(reference.Groups[1].Value, out long id) && match(reference.Groups[2].Value, id))
                    {
                        count++;
                    }
                }

                foreach (Match reference in UrlReference.Matches(text))
                {
                    if (long.TryParse(reference.Groups[2].Value, out long id) && match(reference.Groups[4].Value, id))
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        /// <summary>
        /// Lets the user save modified scenes and leaves Prefab Mode, since open scenes and prefab
        /// stages hold their own copies of files about to be rewritten. Call before writing
        /// anything; returns false if the user cancelled.
        /// </summary>
        public static bool PrepareOpenScenes()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return false;
            }

            if (PrefabStageUtility.GetCurrentPrefabStage() != null)
            {
                StageUtility.GoToMainStage();
            }

            return true;
        }

        /// <summary>Rewrites every reference in the given files that <paramref name="remap"/> redirects, then reimports them.</summary>
        public static void Rewrite(IReadOnlyCollection<string> files, Remap remap, out int rewritten, out int changedFiles)
        {
            int rewrittenCount = 0;

            // Open scenes would otherwise keep (and later save back) their old contents.
            var fileSet = new HashSet<string>(files);
            SceneSetup[] sceneSetup = EditorSceneManager.GetSceneManagerSetup();
            bool reloadScenes = sceneSetup.Any(scene => fileSet.Contains(scene.path));
            if (reloadScenes)
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }

            var changed = new List<string>();
            foreach (string path in files)
            {
                string text = File.ReadAllText(path);
                string updated = YamlReference.Replace(text, match =>
                {
                    if (!long.TryParse(match.Groups[1].Value, out long id) || !remap(match.Groups[2].Value, id, out SpriteTarget target))
                    {
                        return match.Value;
                    }

                    rewrittenCount++;
                    return $"fileID: {target.FileId}, guid: {target.Guid}, type: 3";
                });
                updated = UrlReference.Replace(updated, match =>
                {
                    if (!long.TryParse(match.Groups[2].Value, out long id) || !remap(match.Groups[4].Value, id, out SpriteTarget target))
                    {
                        return match.Value;
                    }

                    rewrittenCount++;
                    string urlPath = string.Join("/", target.AssetPath.Split('/').Select(Uri.EscapeDataString));
                    string fragment = match.Groups[6].Success ? "#" + Uri.EscapeDataString(target.Name) : "";
                    return $"project://database/{urlPath}?fileID={target.FileId}{match.Groups[3].Value}" +
                           $"guid={target.Guid}{match.Groups[5].Value}type=3{fragment}";
                });

                if (updated != text)
                {
                    File.WriteAllText(path, updated);
                    changed.Add(path);
                }
            }

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (string path in changed)
                {
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            if (reloadScenes)
            {
                SceneSetup[] saved = sceneSetup.Where(scene => !string.IsNullOrEmpty(scene.path)).ToArray();
                if (saved.Length > 0)
                {
                    if (!saved.Any(scene => scene.isActive))
                    {
                        saved[0].isActive = true;
                    }

                    EditorSceneManager.RestoreSceneManagerSetup(saved);
                }
            }

            rewritten = rewrittenCount;
            changedFiles = changed.Count;
        }
    }
}
