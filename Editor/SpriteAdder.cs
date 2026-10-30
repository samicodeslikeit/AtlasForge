using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AtlasForge.Editor
{
    /// <summary>A standalone texture loaded to become a new slice of an atlas. Owns its entry texture; dispose when done.</summary>
    public sealed class AddedSprite : System.IDisposable
    {
        public string SourcePath;
        public string SourceGuid;

        /// <summary>Whether the source imports as a single sprite, so references to it can be redirected to the new slice.</summary>
        public bool IsSprite;

        /// <summary>The new slice: its name, decoded pixels and border; X/Y are set by packing.</summary>
        public AtlasSpriteEntry Entry;

        public long InternalId;
        public int Alignment;
        public Vector2 Pivot;
        public int TessellationDetail;

        public void Dispose()
        {
            if (Entry?.Texture != null)
            {
                Object.DestroyImmediate(Entry.Texture);
            }
        }
    }

    /// <summary>
    /// Turns standalone textures into new atlas slices: pixels are decoded from the file (so
    /// import resizing or compression doesn't leak in), and a sprite's pivot, 9-slice border,
    /// outline, physics shape and mesh data carry over onto its slice.
    /// </summary>
    public static class SpriteAdder
    {
        private const string SpriteSheetPrefix = "m_SpriteSheet.";

        /// <summary>Whether a texture can be added to an atlas: an importable PNG/JPG that isn't itself sliced into multiple sprites.</summary>
        public static bool CanAdd(string path) =>
            AssetImporter.GetAtPath(path) is TextureImporter importer &&
            !(importer.textureType == TextureImporterType.Sprite && importer.spriteImportMode == SpriteImportMode.Multiple) &&
            Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg";

        /// <summary>Loads a texture as a slice named <paramref name="name"/>. Returns false with a reason on failure.</summary>
        public static bool TryLoad(string path, string name, out AddedSprite sprite, out string error)
        {
            sprite = null;
            if (!CanAdd(path))
            {
                error = $"'{path}' can't be added: only PNG and JPG textures that aren't already sliced atlases can.";
                return false;
            }

            Texture2D texture = SpriteAtlasBuilder.LoadFromFile(path);
            if (texture == null)
            {
                error = $"Could not decode '{path}'.";
                return false;
            }

            texture.name = name;
            texture.hideFlags = HideFlags.HideAndDontSave;
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            var settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            bool isSprite = importer.textureType == TextureImporterType.Sprite;
            sprite = new AddedSprite
            {
                SourcePath = path,
                SourceGuid = AssetDatabase.AssetPathToGUID(path),
                IsSprite = isSprite,
                Entry = new AtlasSpriteEntry
                {
                    Name = name,
                    Texture = texture,
                    Width = texture.width,
                    Height = texture.height,
                    Border = isSprite ? NineSliceBorderDetector.Clamp(settings.spriteBorder, texture.width, texture.height) : Vector4.zero
                },
                Alignment = isSprite ? settings.spriteAlignment : (int)SpriteAlignment.Center,
                Pivot = isSprite ? settings.spritePivot : new Vector2(0.5f, 0.5f),
                TessellationDetail = isSprite ? Mathf.RoundToInt(settings.spriteTessellationDetail) : -1
            };
            error = null;
            return true;
        }

        /// <summary>
        /// Fills a freshly appended sprite-sheet element with the added slice. Every field is
        /// written, since a new array element starts as a copy of the one before it.
        /// <paramref name="spriteIds"/> holds the sheet's sprite IDs and receives the new one.
        /// </summary>
        public static void WriteSlice(AddedSprite sprite, SerializedProperty element, HashSet<string> spriteIds)
        {
            AtlasSpriteEntry entry = sprite.Entry;
            element.FindPropertyRelative("m_Name").stringValue = entry.Name;
            element.FindPropertyRelative("m_Rect").rectValue = new Rect(entry.X, entry.Y, entry.Width, entry.Height);
            element.FindPropertyRelative("m_Alignment").intValue = sprite.Alignment;
            element.FindPropertyRelative("m_Pivot").vector2Value = sprite.Pivot;
            element.FindPropertyRelative("m_Border").vector4Value = entry.Border;
            element.FindPropertyRelative("m_InternalID").longValue = sprite.InternalId;
            SerializedProperty tessellation = element.FindPropertyRelative("m_TessellationDetail");
            if (tessellation != null)
            {
                tessellation.floatValue = sprite.TessellationDetail;
            }

            string spriteId;
            do
            {
                spriteId = GUID.Generate().ToString();
            }
            while (!spriteIds.Add(spriteId));

            element.FindPropertyRelative("m_SpriteID").stringValue = spriteId;

            // A sprite's own outline, physics shape and mesh live in its importer's single-sprite sheet.
            SerializedObject source = sprite.IsSprite ? new SerializedObject(AssetImporter.GetAtPath(sprite.SourcePath)) : null;
            foreach (string property in SliceExporter.SpriteDataProperties)
            {
                SerializedProperty destination = element.FindPropertyRelative(property);
                if (destination == null)
                {
                    continue;
                }

                SerializedProperty from = source?.FindProperty(SpriteSheetPrefix + property);
                if (from != null)
                {
                    AtlasMerger.CopyValue(from, destination);
                }
                else if (destination.propertyType == SerializedPropertyType.String)
                {
                    destination.stringValue = "";
                }
                else if (destination.isArray)
                {
                    destination.arraySize = 0;
                }
            }
        }

        /// <summary>The name if no slice uses it yet, else numbered (<c>name_2</c>, <c>name_3</c>…); the result is added to <paramref name="taken"/>.</summary>
        public static string UniqueName(string name, HashSet<string> taken)
        {
            string candidate = name;
            for (int suffix = 2; !taken.Add(candidate); suffix++)
            {
                candidate = $"{name}_{suffix}";
            }

            return candidate;
        }
    }
}
