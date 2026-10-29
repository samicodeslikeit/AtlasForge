using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AtlasForge.Editor
{
    /// <summary>
    /// Configures a texture asset as a multi-sprite sheet (with 9-slice borders)
    /// using the core <see cref="TextureImporter"/> sprite-sheet API, so no extra
    /// 2D package dependency is required.
    /// </summary>
    public static class SpriteSheetWriter
    {
        /// <summary>Applies the packed entries as named sprite rects on the atlas texture.</summary>
        public static void Write(string atlasPath, List<AtlasSpriteEntry> entries, int atlasWidth, int atlasHeight)
        {
            if (AssetImporter.GetAtPath(atlasPath) is not TextureImporter importer)
            {
                return;
            }

            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Multiple;
            importer.mipmapEnabled = false;
            importer.alphaIsTransparency = true;
            importer.npotScale = TextureImporterNPOTScale.None;
            EnsureMaxTextureSize(importer, atlasWidth, atlasHeight);

            var metaData = new SpriteMetaData[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                AtlasSpriteEntry entry = entries[i];
                metaData[i] = new SpriteMetaData
                {
                    name = entry.Name,
                    // Sprite rects use a bottom-left origin, matching the packed layout.
                    rect = new Rect(entry.X, entry.Y, entry.Width, entry.Height),
                    border = entry.Border,
                    alignment = (int)SpriteAlignment.Center,
                    pivot = new Vector2(0.5f, 0.5f)
                };
            }

#pragma warning disable CS0618 // spritesheet: stable multi-sprite authoring path without the 2D Sprite package.
            importer.spritesheet = metaData;
#pragma warning restore CS0618

            importer.SaveAndReimport();
        }

        /// <summary>Raises the importer's Max Size so an atlas of the given dimensions is never downscaled.</summary>
        public static void EnsureMaxTextureSize(TextureImporter importer, int atlasWidth, int atlasHeight)
        {
            // Only powers of two are legal here, whatever the atlas dimensions are: an
            // arbitrary value is stored verbatim, leaves the inspector's Max Size dropdown
            // with no matching entry, and can silently downscale the atlas on import.
            int requiredSize = Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.Max(atlasWidth, atlasHeight)), 32, 16384);
            importer.maxTextureSize = Mathf.Max(importer.maxTextureSize, requiredSize);
        }
    }
}
