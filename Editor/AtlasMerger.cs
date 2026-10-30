using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AtlasForge.Editor
{
    /// <summary>
    /// Merges several sliced atlases into one of them (the target). The target keeps its
    /// GUID and its slices keep their IDs; slices from the other atlases are appended to the
    /// target's sprite sheet, and every reference to them in the project's text-serialized
    /// assets (scenes, prefabs, ScriptableObjects, materials, USS/UXML…) is rewritten to
    /// point at their new home, so nothing that used them loses its sprite.
    /// </summary>
    public static class AtlasMerger
    {

        /// <summary>Where one slice ends up in the merged atlas.</summary>
        public sealed class MergedSlice
        {
            public AtlasSlices Source;
            public int SourceIndex;
            public long NewInternalId;
            public string NewName;

            public AtlasSpriteEntry Entry => Source.Entries[SourceIndex];
            public long SourceInternalId => Source.InternalIds[SourceIndex];
        }

        /// <summary>A computed merge, ready to be written by <see cref="Apply"/>.</summary>
        public sealed class MergePlan
        {
            public AtlasSlices Target;

            /// <summary>Every atlas being merged in, excluding the target.</summary>
            public List<AtlasSlices> Sources;

            /// <summary>All slices: the target's first, then each source's, in serialized order.</summary>
            public List<MergedSlice> Slices;

            public int Width;
            public int Height;

            /// <summary>Text assets that mention a source atlas's GUID and will be scanned for references.</summary>
            public List<string> ReferencingFiles;

            public IEnumerable<MergedSlice> RenamedSlices => Slices.Where(slice => slice.NewName != slice.Entry.Name);
        }

        /// <summary>
        /// Resolves name and ID clashes, packs every slice into one layout, and finds the files
        /// whose references will need rewriting. Nothing is written.
        /// </summary>
        public static bool TryPlan(
            IReadOnlyList<AtlasSlices> atlases, AtlasSlices target, int padding, int maxSize, bool forcePowerOfTwo,
            out MergePlan plan, out string error)
        {
            plan = null;
            if (!SpriteReferenceRewriter.IsTextSerialization)
            {
                error = "Asset Serialization must be set to Force Text (Project Settings > Editor) so references can be rewritten.";
                return false;
            }

            if (atlases.Count < 2 || !atlases.Contains(target))
            {
                error = "Add at least two atlases and pick the one to merge into.";
                return false;
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            var ids = new HashSet<long>();
            var slices = new List<MergedSlice>();

            // The target's slices keep their names and IDs; everything else has to fit around them.
            for (int i = 0; i < target.Entries.Count; i++)
            {
                names.Add(target.Entries[i].Name);
                ids.Add(target.InternalIds[i]);
                slices.Add(new MergedSlice
                {
                    Source = target,
                    SourceIndex = i,
                    NewInternalId = target.InternalIds[i],
                    NewName = target.Entries[i].Name
                });
            }

            List<AtlasSlices> sources = atlases.Where(atlas => atlas != target).ToList();
            foreach (AtlasSlices source in sources)
            {
                for (int i = 0; i < source.Entries.Count; i++)
                {
                    long id = source.InternalIds[i];
                    slices.Add(new MergedSlice
                    {
                        Source = source,
                        SourceIndex = i,
                        NewInternalId = ids.Add(id) ? id : NewUniqueId(ids),
                        NewName = UniqueName(source.Entries[i].Name, source, names)
                    });
                }
            }

            List<AtlasSpriteEntry> entries = slices.Select(slice => slice.Entry).ToList();
            if (!SpriteAtlasBuilder.TryPack(entries, padding, maxSize, forcePowerOfTwo, out int width, out int height))
            {
                error = $"The merged slices don't fit within {maxSize}px. Increase Max Atlas Size or lower Padding.";
                return false;
            }

            plan = new MergePlan
            {
                Target = target,
                Sources = sources,
                Slices = slices,
                Width = width,
                Height = height,
                ReferencingFiles = SpriteReferenceRewriter.FindFilesMentioning(sources.Select(source => source.AtlasGuid).ToList())
            };
            error = null;
            return true;
        }

        /// <summary>
        /// Writes the merge: renders the target atlas, appends the source slices to its sprite
        /// sheet, rewrites references to them across the project, and optionally moves the
        /// source atlases to the trash. <paramref name="borders"/> is parallel to the plan's slices.
        /// </summary>
        public static bool Apply(
            MergePlan plan, IReadOnlyList<Vector4> borders, bool trashSources, out string summary)
        {
            if (!SpriteReferenceRewriter.PrepareOpenScenes())
            {
                summary = "Merge cancelled.";
                return false;
            }

            if (!TryWriteTarget(plan, borders, out summary))
            {
                return false;
            }

            RewriteReferences(plan, out int rewritten, out int changedFiles, out int unresolved);

            var trashed = new List<string>();
            if (trashSources)
            {
                var failed = new List<string>();
                AssetDatabase.MoveAssetsToTrash(plan.Sources.Select(source => source.AtlasPath).ToArray(), failed);
                trashed.AddRange(plan.Sources.Select(source => source.AtlasPath).Except(failed));
            }

            var builder = new StringBuilder();
            builder.Append($"Merged {plan.Sources.Count + 1} atlases into '{plan.Target.AtlasPath}' ")
                .Append($"({plan.Width}x{plan.Height}, {plan.Slices.Count} slices). ")
                .Append($"Rewrote {rewritten} references in {changedFiles} files.");
            List<MergedSlice> renamed = plan.RenamedSlices.ToList();
            if (renamed.Count > 0)
            {
                builder.Append($"\nRenamed {renamed.Count} slices to avoid name clashes: ")
                    .Append(string.Join(", ", renamed.Select(slice => $"'{slice.Entry.Name}' → '{slice.NewName}'")));
            }

            if (unresolved > 0)
            {
                builder.Append($"\n{unresolved} references point at a source texture itself rather than one of its slices and were left as they are.");
            }

            if (trashed.Count > 0)
            {
                builder.Append("\nMoved to trash: ").Append(string.Join(", ", trashed));
            }

            summary = builder.ToString();
            return true;
        }

        /// <summary>Renders the merged atlas over the target and rebuilds the target's sprite sheet.</summary>
        private static bool TryWriteTarget(MergePlan plan, IReadOnlyList<Vector4> borders, out string error)
        {
            const string changedError = "An atlas's slices changed since they were loaded. Click Refresh and try again.";
            if (AssetImporter.GetAtPath(plan.Target.AtlasPath) is not TextureImporter importer)
            {
                error = $"'{plan.Target.AtlasPath}' is no longer a texture.";
                return false;
            }

            // Every atlas's serialized sprites must still match what was loaded, slice for slice.
            var spritesByAtlas = new Dictionary<AtlasSlices, (SerializedObject Serialized, Dictionary<long, int> IndexById)>();
            foreach (AtlasSlices atlas in plan.Sources.Prepend(plan.Target))
            {
                SerializedObject serialized = atlas == plan.Target
                    ? new SerializedObject(importer)
                    : new SerializedObject(AssetImporter.GetAtPath(atlas.AtlasPath));
                SerializedProperty sprites = serialized.FindProperty(AtlasRepacker.SpritesProperty);
                if (sprites == null || sprites.arraySize != atlas.Entries.Count)
                {
                    error = changedError;
                    return false;
                }

                var indexById = new Dictionary<long, int>();
                for (int i = 0; i < sprites.arraySize; i++)
                {
                    indexById[sprites.GetArrayElementAtIndex(i).FindPropertyRelative("m_InternalID").longValue] = i;
                }

                if (atlas.InternalIds.Any(id => !indexById.ContainsKey(id)))
                {
                    error = changedError;
                    return false;
                }

                spritesByAtlas[atlas] = (serialized, indexById);
            }

            List<AtlasSpriteEntry> entries = plan.Slices.Select(slice => slice.Entry).ToList();
            Texture2D atlasTexture = SpriteAtlasBuilder.Render(entries, plan.Width, plan.Height);
            File.WriteAllBytes(plan.Target.AtlasPath, atlasTexture.EncodeToPNG());
            Object.DestroyImmediate(atlasTexture);

            (SerializedObject targetSerialized, Dictionary<long, int> targetIndexById) = spritesByAtlas[plan.Target];
            SerializedProperty targetSprites = targetSerialized.FindProperty(AtlasRepacker.SpritesProperty);
            var spriteIds = new HashSet<string>();
            for (int i = 0; i < targetSprites.arraySize; i++)
            {
                spriteIds.Add(targetSprites.GetArrayElementAtIndex(i).FindPropertyRelative("m_SpriteID").stringValue);
            }

            for (int i = 0; i < plan.Slices.Count; i++)
            {
                MergedSlice slice = plan.Slices[i];
                SerializedProperty sprite;
                if (slice.Source == plan.Target)
                {
                    sprite = targetSprites.GetArrayElementAtIndex(targetIndexById[slice.SourceInternalId]);
                }
                else
                {
                    // Start from a full copy so pivots, outlines, physics shapes etc. carry over.
                    (SerializedObject sourceSerialized, Dictionary<long, int> sourceIndexById) = spritesByAtlas[slice.Source];
                    SerializedProperty sourceSprite = sourceSerialized.FindProperty(AtlasRepacker.SpritesProperty)
                        .GetArrayElementAtIndex(sourceIndexById[slice.SourceInternalId]);
                    targetSprites.arraySize++;
                    sprite = targetSprites.GetArrayElementAtIndex(targetSprites.arraySize - 1);
                    CopyValue(sourceSprite, sprite);

                    sprite.FindPropertyRelative("m_Name").stringValue = slice.NewName;
                    sprite.FindPropertyRelative("m_InternalID").longValue = slice.NewInternalId;
                    SerializedProperty spriteId = sprite.FindPropertyRelative("m_SpriteID");
                    if (!spriteIds.Add(spriteId.stringValue))
                    {
                        spriteId.stringValue = GUID.Generate().ToString();
                        spriteIds.Add(spriteId.stringValue);
                    }
                }

                AtlasSpriteEntry entry = slice.Entry;
                sprite.FindPropertyRelative("m_Rect").rectValue = new Rect(entry.X, entry.Y, entry.Width, entry.Height);
                sprite.FindPropertyRelative("m_Border").vector4Value = borders[i];
            }

            // The importer also keeps a name → ID table; rebuild it to match the new sprite list.
            SerializedProperty table = targetSerialized.FindProperty(AtlasRepacker.NameFileIdTableProperty);
            if (table != null)
            {
                table.arraySize = targetSprites.arraySize;
                for (int i = 0; i < targetSprites.arraySize; i++)
                {
                    SerializedProperty sprite = targetSprites.GetArrayElementAtIndex(i);
                    SerializedProperty pair = table.GetArrayElementAtIndex(i);
                    pair.FindPropertyRelative("first").stringValue = sprite.FindPropertyRelative("m_Name").stringValue;
                    pair.FindPropertyRelative("second").longValue = sprite.FindPropertyRelative("m_InternalID").longValue;
                }
            }

            targetSerialized.ApplyModifiedPropertiesWithoutUndo();
            SpriteSheetWriter.EnsureMaxTextureSize(importer, plan.Width, plan.Height);
            importer.SaveAndReimport();
            error = null;
            return true;
        }

        /// <summary>Points every reference to a merged-in slice at its new ID inside the target atlas.</summary>
        private static void RewriteReferences(MergePlan plan, out int rewritten, out int changedFiles, out int unresolved)
        {
            var remap = new Dictionary<string, Dictionary<long, MergedSlice>>();
            foreach (MergedSlice slice in plan.Slices.Where(slice => slice.Source != plan.Target))
            {
                if (!remap.TryGetValue(slice.Source.AtlasGuid, out Dictionary<long, MergedSlice> byId))
                {
                    remap[slice.Source.AtlasGuid] = byId = new Dictionary<long, MergedSlice>();
                }

                byId[slice.SourceInternalId] = slice;
            }

            int unresolvedCount = 0;
            SpriteReferenceRewriter.Rewrite(
                plan.ReferencingFiles,
                (string guid, long fileId, out SpriteReferenceRewriter.SpriteTarget target) =>
                {
                    target = default;
                    if (!remap.TryGetValue(guid, out Dictionary<long, MergedSlice> byId))
                    {
                        return false;
                    }

                    if (!byId.TryGetValue(fileId, out MergedSlice slice))
                    {
                        unresolvedCount++;
                        return false;
                    }

                    target = new SpriteReferenceRewriter.SpriteTarget(
                        plan.Target.AtlasGuid, slice.NewInternalId, slice.NewName, plan.Target.AtlasPath);
                    return true;
                },
                out rewritten,
                out changedFiles);
            unresolved = unresolvedCount;
        }

        /// <summary>The slice's own name if free, else suffixed with its atlas name (and a counter if needed).</summary>
        private static string UniqueName(string name, AtlasSlices source, HashSet<string> taken)
        {
            if (taken.Add(name))
            {
                return name;
            }

            string baseName = $"{name}_{Path.GetFileNameWithoutExtension(source.AtlasPath)}";
            string candidate = baseName;
            for (int suffix = 2; !taken.Add(candidate); suffix++)
            {
                candidate = $"{baseName}_{suffix}";
            }

            return candidate;
        }

        /// <summary>A random nonzero internal ID not in <paramref name="taken"/>; it is added to the set.</summary>
        internal static long NewUniqueId(HashSet<long> taken)
        {
            long id;
            do
            {
                id = BitConverter.ToInt64(Guid.NewGuid().ToByteArray(), 0);
            }
            while (id == 0 || !taken.Add(id));

            return id;
        }

        /// <summary>Deep-copies a serialized value (struct, array or leaf) onto another property of the same layout.</summary>
        internal static void CopyValue(SerializedProperty source, SerializedProperty destination)
        {
            if (source.propertyType == SerializedPropertyType.String)
            {
                destination.stringValue = source.stringValue;
                return;
            }

            if (source.isArray)
            {
                destination.arraySize = source.arraySize;
                for (int i = 0; i < source.arraySize; i++)
                {
                    CopyValue(source.GetArrayElementAtIndex(i), destination.GetArrayElementAtIndex(i));
                }

                return;
            }

            switch (source.propertyType)
            {
                case SerializedPropertyType.Integer:
                    destination.longValue = source.longValue;
                    return;
                case SerializedPropertyType.Enum:
                case SerializedPropertyType.LayerMask:
                case SerializedPropertyType.Character:
                    destination.intValue = source.intValue;
                    return;
                case SerializedPropertyType.Boolean:
                    destination.boolValue = source.boolValue;
                    return;
                case SerializedPropertyType.Float:
                    destination.doubleValue = source.doubleValue;
                    return;
                case SerializedPropertyType.Color:
                    destination.colorValue = source.colorValue;
                    return;
                case SerializedPropertyType.Vector2:
                    destination.vector2Value = source.vector2Value;
                    return;
                case SerializedPropertyType.Vector3:
                    destination.vector3Value = source.vector3Value;
                    return;
                case SerializedPropertyType.Vector4:
                    destination.vector4Value = source.vector4Value;
                    return;
                case SerializedPropertyType.Vector2Int:
                    destination.vector2IntValue = source.vector2IntValue;
                    return;
                case SerializedPropertyType.Vector3Int:
                    destination.vector3IntValue = source.vector3IntValue;
                    return;
                case SerializedPropertyType.Rect:
                    destination.rectValue = source.rectValue;
                    return;
                case SerializedPropertyType.RectInt:
                    destination.rectIntValue = source.rectIntValue;
                    return;
                case SerializedPropertyType.Bounds:
                    destination.boundsValue = source.boundsValue;
                    return;
                case SerializedPropertyType.BoundsInt:
                    destination.boundsIntValue = source.boundsIntValue;
                    return;
                case SerializedPropertyType.Quaternion:
                    destination.quaternionValue = source.quaternionValue;
                    return;
                case SerializedPropertyType.Hash128:
                    destination.hash128Value = source.hash128Value;
                    return;
                case SerializedPropertyType.ObjectReference:
                    destination.objectReferenceValue = source.objectReferenceValue;
                    return;
            }

            // Generic struct: copy each direct child by name.
            SerializedProperty child = source.Copy();
            SerializedProperty end = source.GetEndProperty();
            if (!child.Next(true))
            {
                return;
            }

            while (!SerializedProperty.EqualContents(child, end))
            {
                SerializedProperty target = destination.FindPropertyRelative(child.name);
                if (target != null)
                {
                    CopyValue(child, target);
                }

                if (!child.Next(false))
                {
                    break;
                }
            }
        }
    }
}
