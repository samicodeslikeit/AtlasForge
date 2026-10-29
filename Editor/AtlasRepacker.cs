using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AtlasForge.Editor
{
    /// <summary>
    /// The slices of an already sliced atlas, cut out of its source file into standalone
    /// readable textures. Owns those textures; dispose when done.
    /// </summary>
    public sealed class AtlasSlices : System.IDisposable
    {
        public string AtlasPath;
        public string AtlasGuid;

        /// <summary>Source-file dimensions of the atlas when it was loaded.</summary>
        public int Width;
        public int Height;

        /// <summary>One entry per slice; Border holds the slice's currently imported border.</summary>
        public List<AtlasSpriteEntry> Entries;

        /// <summary>Each slice's internal ID (the file ID references point at), parallel to <see cref="Entries"/>.</summary>
        public List<long> InternalIds;

        /// <summary>The decoded atlas file, kept so slices can be re-cut after their rect is edited.</summary>
        public Texture2D Source;

        /// <summary>Each slice's rect in the atlas as imported (source-file pixels, bottom-up), parallel to <see cref="Entries"/>.</summary>
        public List<RectInt> SourceRects;

        /// <summary>Each slice's current rect: its imported rect, or an edited one its entry was re-cut from.</summary>
        public List<RectInt> Rects;

        private Color32[] _sourcePixels;

        /// <summary>Whether a slice's rect was edited away from its imported rect.</summary>
        public bool IsRectEdited(int index) => !Rects[index].Equals(SourceRects[index]);

        /// <summary>
        /// Moves or resizes a slice: re-cuts its entry texture from <see cref="Source"/> at the
        /// rect (clamped to the atlas, at least 1px), and records it in <see cref="Rects"/>.
        /// </summary>
        public RectInt Recut(int index, RectInt rect)
        {
            rect = ClampRect(rect);
            _sourcePixels ??= Source.GetPixels32();

            AtlasSpriteEntry entry = Entries[index];
            var texture = new Texture2D(rect.width, rect.height, TextureFormat.RGBA32, false)
            {
                name = entry.Name,
                hideFlags = HideFlags.HideAndDontSave
            };
            texture.SetPixels32(AtlasRepacker.CopyRegion(_sourcePixels, Width, rect));
            texture.Apply();

            if (entry.Texture != null)
            {
                Object.DestroyImmediate(entry.Texture);
            }

            entry.Texture = texture;
            entry.X = rect.x;
            entry.Y = rect.y;
            entry.Width = rect.width;
            entry.Height = rect.height;
            Rects[index] = rect;
            return rect;
        }

        /// <summary>Keeps a rect inside the atlas and at least 1x1.</summary>
        public RectInt ClampRect(RectInt rect)
        {
            int xMin = Mathf.Clamp(rect.xMin, 0, Width - 1);
            int yMin = Mathf.Clamp(rect.yMin, 0, Height - 1);
            int xMax = Mathf.Clamp(rect.xMax, xMin + 1, Width);
            int yMax = Mathf.Clamp(rect.yMax, yMin + 1, Height);
            return new RectInt(xMin, yMin, xMax - xMin, yMax - yMin);
        }

        public void Dispose()
        {
            foreach (AtlasSpriteEntry entry in Entries)
            {
                if (entry.Texture != null)
                {
                    Object.DestroyImmediate(entry.Texture);
                }
            }

            Entries.Clear();
            if (Source != null)
            {
                Object.DestroyImmediate(Source);
            }

            _sourcePixels = null;
        }
    }

    /// <summary>
    /// Repacks an already sliced atlas in place to reclaim its empty space. Slices are
    /// moved by editing the importer's serialized sprite sheet rather than through the
    /// spritesheet API, so every sprite keeps its internal ID and all existing references
    /// to it (Image components, prefabs, materials) stay valid.
    /// </summary>
    public static class AtlasRepacker
    {
        internal const string SpritesProperty = "m_SpriteSheet.m_Sprites";
        internal const string NameFileIdTableProperty = "m_SpriteSheet.m_NameFileIdTable";
        private const string SecondaryTexturesProperty = "m_SpriteSheet.m_SecondaryTextures";

        /// <summary>Cuts every slice out of the atlas's source file. Returns false with a reason on failure.</summary>
        public static bool TryLoad(Texture2D atlas, out AtlasSlices slices, out string error)
        {
            slices = null;
            string path = AssetDatabase.GetAssetPath(atlas);
            if (AssetImporter.GetAtPath(path) is not TextureImporter importer ||
                importer.spriteImportMode != SpriteImportMode.Multiple)
            {
                error = "The texture is not a sliced atlas (Sprite Mode must be Multiple).";
                return false;
            }

            var serialized = new SerializedObject(importer);
            SerializedProperty sprites = serialized.FindProperty(SpritesProperty);
            if (sprites == null || sprites.arraySize == 0)
            {
                error = "The atlas has no sprite slices.";
                return false;
            }

            // Secondary textures (normal maps etc.) share the slice layout; moving slices
            // without repacking those too would misalign them.
            SerializedProperty secondary = serialized.FindProperty(SecondaryTexturesProperty);
            if (secondary != null && secondary.arraySize > 0)
            {
                error = "Atlases with secondary textures can't be repacked.";
                return false;
            }

            // Sprite rects are in source-file pixels, so slices are cut from the decoded file
            // rather than the imported texture, which may be resized or compressed.
            Texture2D source = SpriteAtlasBuilder.LoadFromFile(path);
            if (source == null)
            {
                error = "Could not decode the atlas file. Only PNG and JPG atlases can be repacked.";
                return false;
            }

            var loaded = new AtlasSlices
            {
                AtlasPath = path,
                AtlasGuid = AssetDatabase.AssetPathToGUID(path),
                Width = source.width,
                Height = source.height,
                Entries = new List<AtlasSpriteEntry>(sprites.arraySize),
                InternalIds = new List<long>(sprites.arraySize),
                Source = source,
                SourceRects = new List<RectInt>(sprites.arraySize),
                Rects = new List<RectInt>(sprites.arraySize)
            };
            source.hideFlags = HideFlags.HideAndDontSave;

            // The slices own the decoded source from here on (Dispose destroys it).
            bool loadedOk = false;
            try
            {
                Color32[] pixels = source.GetPixels32();
                var seenIds = new HashSet<long>();
                for (int i = 0; i < sprites.arraySize; i++)
                {
                    SerializedProperty sprite = sprites.GetArrayElementAtIndex(i);
                    string name = sprite.FindPropertyRelative("m_Name").stringValue;
                    long internalId = sprite.FindPropertyRelative("m_InternalID").longValue;
                    if (internalId == 0 || !seenIds.Add(internalId))
                    {
                        error = $"Sprite '{name}' has a missing or duplicate internal ID. Open and apply the atlas in the Sprite Editor once, then retry.";
                        loaded.Dispose();
                        return false;
                    }

                    RectInt rect = ToPixelRect(sprite.FindPropertyRelative("m_Rect").rectValue, source.width, source.height);
                    if (rect.width <= 0 || rect.height <= 0)
                    {
                        error = $"Sprite '{name}' lies outside the texture.";
                        loaded.Dispose();
                        return false;
                    }

                    var texture = new Texture2D(rect.width, rect.height, TextureFormat.RGBA32, false)
                    {
                        name = name,
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    texture.SetPixels32(CopyRegion(pixels, source.width, rect));
                    texture.Apply();

                    loaded.Entries.Add(new AtlasSpriteEntry
                    {
                        Name = name,
                        Texture = texture,
                        X = rect.x,
                        Y = rect.y,
                        Width = rect.width,
                        Height = rect.height,
                        Border = sprite.FindPropertyRelative("m_Border").vector4Value
                    });
                    loaded.InternalIds.Add(internalId);
                    loaded.SourceRects.Add(rect);
                    loaded.Rects.Add(rect);
                }

                slices = loaded;
                error = null;
                loadedOk = true;
                return true;
            }
            finally
            {
                if (!loadedOk)
                {
                    Object.DestroyImmediate(source);
                }
            }
        }

        /// <summary>
        /// Removes the slices in <paramref name="deletedIds"/> and writes the given borders onto
        /// the rest. With <paramref name="relayout"/> the atlas PNG is also re-rendered from the
        /// kept slices at <paramref name="width"/> x <paramref name="height"/>, each moved to its
        /// entry's packed X/Y; otherwise the layout is untouched. Slices are matched by internal
        /// ID, so nothing is written if the atlas changed since loading.
        /// </summary>
        public static bool Apply(
            AtlasSlices slices, IReadOnlyList<Vector4> borders, ICollection<long> deletedIds, bool relayout,
            int width, int height, out string error)
        {
            if (AssetImporter.GetAtPath(slices.AtlasPath) is not TextureImporter importer)
            {
                error = $"'{slices.AtlasPath}' is no longer a texture.";
                return false;
            }

            var serialized = new SerializedObject(importer);
            SerializedProperty sprites = serialized.FindProperty(SpritesProperty);
            var indexById = new Dictionary<long, int>(slices.InternalIds.Count);
            for (int i = 0; i < slices.InternalIds.Count; i++)
            {
                indexById[slices.InternalIds[i]] = i;
            }

            // Resolve every serialized sprite before writing anything.
            const string changedError = "The atlas slices changed since they were loaded. Click Refresh and try again.";
            if (sprites == null || sprites.arraySize != slices.Entries.Count)
            {
                error = changedError;
                return false;
            }

            var entryIndices = new int[sprites.arraySize];
            for (int i = 0; i < sprites.arraySize; i++)
            {
                long internalId = sprites.GetArrayElementAtIndex(i).FindPropertyRelative("m_InternalID").longValue;
                if (!indexById.TryGetValue(internalId, out entryIndices[i]))
                {
                    error = changedError;
                    return false;
                }
            }

            if (relayout)
            {
                var kept = new List<AtlasSpriteEntry>(slices.Entries.Count);
                for (int i = 0; i < slices.Entries.Count; i++)
                {
                    if (!deletedIds.Contains(slices.InternalIds[i]))
                    {
                        kept.Add(slices.Entries[i]);
                    }
                }

                Texture2D atlas = SpriteAtlasBuilder.Render(kept, width, height);
                File.WriteAllBytes(slices.AtlasPath, atlas.EncodeToPNG());
                Object.DestroyImmediate(atlas);
            }

            // Backwards so deletions don't shift the indices still to be visited.
            for (int i = sprites.arraySize - 1; i >= 0; i--)
            {
                int entryIndex = entryIndices[i];
                if (deletedIds.Contains(slices.InternalIds[entryIndex]))
                {
                    sprites.DeleteArrayElementAtIndex(i);
                    continue;
                }

                SerializedProperty sprite = sprites.GetArrayElementAtIndex(i);
                sprite.FindPropertyRelative("m_Border").vector4Value = borders[entryIndex];
                if (relayout)
                {
                    AtlasSpriteEntry entry = slices.Entries[entryIndex];
                    sprite.FindPropertyRelative("m_Rect").rectValue = new Rect(entry.X, entry.Y, entry.Width, entry.Height);
                }
                else if (slices.IsRectEdited(entryIndex))
                {
                    // Unpacked layout: an edited rect just points the sprite at different atlas pixels.
                    RectInt rect = slices.Rects[entryIndex];
                    sprite.FindPropertyRelative("m_Rect").rectValue = new Rect(rect.x, rect.y, rect.width, rect.height);
                }
            }

            // The importer's name → ID table must not keep entries for deleted slices.
            SerializedProperty table = serialized.FindProperty(NameFileIdTableProperty);
            if (table != null && deletedIds.Count > 0)
            {
                for (int i = table.arraySize - 1; i >= 0; i--)
                {
                    if (deletedIds.Contains(table.GetArrayElementAtIndex(i).FindPropertyRelative("second").longValue))
                    {
                        table.DeleteArrayElementAtIndex(i);
                    }
                }
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
            if (relayout)
            {
                SpriteSheetWriter.EnsureMaxTextureSize(importer, width, height);
            }

            importer.SaveAndReimport();
            error = null;
            return true;
        }

        /// <summary>
        /// Groups slices whose pixels are exactly identical. Returns, per entry, the index of the
        /// first entry with the same pixels (its own index when it has no earlier twin).
        /// </summary>
        public static int[] FindIdenticalSlices(IReadOnlyList<AtlasSpriteEntry> entries)
        {
            var groups = new int[entries.Count];
            var pixels = new Color32[entries.Count][];
            var candidatesByKey = new Dictionary<(int, int, ulong), List<int>>();
            for (int i = 0; i < entries.Count; i++)
            {
                groups[i] = i;
                pixels[i] = entries[i].Texture.GetPixels32();
                var key = (entries[i].Width, entries[i].Height, Hash(pixels[i]));
                if (!candidatesByKey.TryGetValue(key, out List<int> candidates))
                {
                    candidatesByKey[key] = candidates = new List<int>();
                }

                // A hash match is only a hint; confirm pixel for pixel.
                foreach (int candidate in candidates)
                {
                    if (PixelsEqual(pixels[candidate], pixels[i]))
                    {
                        groups[i] = groups[candidate];
                        break;
                    }
                }

                candidates.Add(i);
            }

            return groups;
        }

        private static bool PixelsEqual(Color32[] a, Color32[] b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].r != b[i].r || a[i].g != b[i].g || a[i].b != b[i].b || a[i].a != b[i].a)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>FNV-1a over the raw RGBA bytes.</summary>
        private static ulong Hash(Color32[] pixels)
        {
            ulong hash = 14695981039346656037UL;
            foreach (Color32 pixel in pixels)
            {
                hash = (hash ^ pixel.r) * 1099511628211UL;
                hash = (hash ^ pixel.g) * 1099511628211UL;
                hash = (hash ^ pixel.b) * 1099511628211UL;
                hash = (hash ^ pixel.a) * 1099511628211UL;
            }

            return hash;
        }

        /// <summary>Snaps a sprite rect to whole pixels, clipped to the texture bounds.</summary>
        private static RectInt ToPixelRect(Rect rect, int textureWidth, int textureHeight)
        {
            int xMin = Mathf.Clamp(Mathf.RoundToInt(rect.xMin), 0, textureWidth);
            int yMin = Mathf.Clamp(Mathf.RoundToInt(rect.yMin), 0, textureHeight);
            int xMax = Mathf.Clamp(Mathf.RoundToInt(rect.xMax), 0, textureWidth);
            int yMax = Mathf.Clamp(Mathf.RoundToInt(rect.yMax), 0, textureHeight);
            return new RectInt(xMin, yMin, xMax - xMin, yMax - yMin);
        }

        /// <summary>Copies a sub-rectangle out of a bottom-up, row-major pixel buffer.</summary>
        internal static Color32[] CopyRegion(Color32[] pixels, int textureWidth, RectInt rect)
        {
            var region = new Color32[rect.width * rect.height];
            for (int row = 0; row < rect.height; row++)
            {
                System.Array.Copy(pixels, (rect.y + row) * textureWidth + rect.x, region, row * rect.width, rect.width);
            }

            return region;
        }
    }
}
