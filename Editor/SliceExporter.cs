using System.IO;
using UnityEditor;
using UnityEngine;

namespace AtlasForge.Editor
{
    /// <summary>
    /// Writes an atlas slice out as a standalone sprite texture that imports exactly like the
    /// slice did: the atlas's import and platform settings, plus the slice's pivot, 9-slice
    /// border, outline, physics shape and mesh data.
    /// </summary>
    public static class SliceExporter
    {
        /// <summary>The file ID Unity gives the sprite of a texture imported in Single sprite mode.</summary>
        public const long SingleSpriteFileId = 21300000;

        private const string PlatformSettingsProperty = "m_PlatformSettings";

        /// <summary>Per-sprite data shared by a Multiple-mode slice and a Single-mode sprite sheet.</summary>
        private static readonly string[] SpriteDataProperties =
        {
            "m_Outline", "m_CustomData", "m_PhysicsShape", "m_Bones", "m_Vertices", "m_Indices", "m_Edges", "m_Weights"
        };

        /// <summary>
        /// Exports slice <paramref name="index"/> to <paramref name="path"/> (a .png under Assets/)
        /// with the given border. Returns false with a reason on failure.
        /// </summary>
        public static bool Export(AtlasSlices slices, int index, Vector4 border, string path, out string error)
        {
            if (AssetImporter.GetAtPath(slices.AtlasPath) is not TextureImporter atlasImporter)
            {
                error = $"'{slices.AtlasPath}' is no longer a texture.";
                return false;
            }

            var atlasSerialized = new SerializedObject(atlasImporter);
            SerializedProperty slice = FindSlice(atlasSerialized, slices.InternalIds[index]);
            if (slice == null)
            {
                error = "The atlas slices changed since they were loaded. Click Refresh and try again.";
                return false;
            }

            File.WriteAllBytes(path, slices.Entries[index].Texture.EncodeToPNG());
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            if (AssetImporter.GetAtPath(path) is not TextureImporter importer)
            {
                error = $"'{path}' could not be imported as a texture.";
                return false;
            }

            var settings = new TextureImporterSettings();
            atlasImporter.ReadTextureSettings(settings);
            settings.spriteMode = (int)SpriteImportMode.Single;
            settings.spriteAlignment = slice.FindPropertyRelative("m_Alignment").intValue;
            settings.spritePivot = slice.FindPropertyRelative("m_Pivot").vector2Value;
            settings.spriteBorder = border;
            importer.SetTextureSettings(settings);

            var serialized = new SerializedObject(importer);
            AtlasMerger.CopyValue(
                atlasSerialized.FindProperty(PlatformSettingsProperty), serialized.FindProperty(PlatformSettingsProperty));
            foreach (string property in SpriteDataProperties)
            {
                SerializedProperty source = slice.FindPropertyRelative(property);
                SerializedProperty destination = serialized.FindProperty("m_SpriteSheet." + property);
                if (source != null && destination != null)
                {
                    AtlasMerger.CopyValue(source, destination);
                }
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
            importer.SaveAndReimport();
            error = null;
            return true;
        }

        private static SerializedProperty FindSlice(SerializedObject atlasSerialized, long internalId)
        {
            SerializedProperty sprites = atlasSerialized.FindProperty(AtlasRepacker.SpritesProperty);
            for (int i = 0; i < sprites.arraySize; i++)
            {
                SerializedProperty sprite = sprites.GetArrayElementAtIndex(i);
                if (sprite.FindPropertyRelative("m_InternalID").longValue == internalId)
                {
                    return sprite;
                }
            }

            return null;
        }
    }
}
