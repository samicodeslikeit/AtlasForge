using System.Collections.Generic;
using UnityEngine;

namespace AtlasForge.Editor
{
    /// <summary>
    /// The loaded slices of one or more sliced atlases, plus the per-slice border configs
    /// edited for them. Configs survive domain reloads; the slices themselves are not
    /// serialized and are cut from the atlas files again on demand.
    /// </summary>
    [System.Serializable]
    public sealed class AtlasSliceSet
    {
        public List<SpriteBorderConfig> Configs = new();

        [System.NonSerialized] private List<AtlasSlices> _atlases;
        [System.NonSerialized] private Dictionary<(string, long), SpriteBorderConfig> _configsByKey;
        [System.NonSerialized] private Dictionary<(string, long), Texture2D> _textures;

        /// <summary>Why the last load failed, or null.</summary>
        [System.NonSerialized] public string Error;

        /// <summary>The loaded atlases, in load order; null until loaded.</summary>
        public IReadOnlyList<AtlasSlices> Atlases => _atlases;

        public bool IsLoaded => _atlases != null;

        /// <summary>
        /// Cuts the slices out of every given atlas and syncs <see cref="Configs"/> with them.
        /// Existing edits are kept per slice; new slices start from their imported border.
        /// Loading is all or nothing: if any atlas fails, nothing stays loaded.
        /// </summary>
        public void Load(IReadOnlyList<Texture2D> atlases)
        {
            Unload();
            Error = null;

            var loaded = new List<AtlasSlices>(atlases.Count);
            foreach (Texture2D atlas in atlases)
            {
                if (!AtlasRepacker.TryLoad(atlas, out AtlasSlices slices, out string error))
                {
                    loaded.ForEach(slicesToDrop => slicesToDrop.Dispose());
                    Error = atlases.Count > 1 ? $"{atlas.name}: {error}" : error;
                    Configs.Clear();
                    return;
                }

                loaded.Add(slices);
            }

            var existing = new Dictionary<(string, long), SpriteBorderConfig>();
            foreach (SpriteBorderConfig config in Configs)
            {
                existing[(config.AtlasGuid, config.SliceId)] = config;
            }

            _atlases = loaded;
            _configsByKey = new Dictionary<(string, long), SpriteBorderConfig>();
            _textures = new Dictionary<(string, long), Texture2D>();
            var synced = new List<SpriteBorderConfig>();
            foreach (AtlasSlices slices in loaded)
            {
                for (int i = 0; i < slices.Entries.Count; i++)
                {
                    AtlasSpriteEntry entry = slices.Entries[i];
                    (string, long) key = (slices.AtlasGuid, slices.InternalIds[i]);
                    if (!existing.TryGetValue(key, out SpriteBorderConfig config))
                    {
                        config = new SpriteBorderConfig
                        {
                            AtlasGuid = slices.AtlasGuid,
                            SliceId = slices.InternalIds[i],
                            Mode = entry.Border != Vector4.zero ? BorderMode.Manual : BorderMode.None,
                            Border = entry.Border
                        };
                    }

                    config.Name = entry.Name;
                    config.AssetPath = slices.AtlasPath;
                    synced.Add(config);
                    _configsByKey[key] = config;
                    _textures[key] = entry.Texture;
                }
            }

            Configs = synced;
        }

        /// <summary>Releases the cut-out slice textures; configs are kept.</summary>
        public void Unload()
        {
            _atlases?.ForEach(slices => slices.Dispose());
            _atlases = null;
            _configsByKey = null;
            _textures = null;
        }

        /// <summary>The cut-out texture for a slice config, or null if it isn't loaded.</summary>
        public Texture2D FindTexture(SpriteBorderConfig config) =>
            _textures != null && _textures.TryGetValue((config.AtlasGuid, config.SliceId), out Texture2D texture) ? texture : null;

        /// <summary>The border config for a loaded slice, or null.</summary>
        public SpriteBorderConfig FindConfig(AtlasSlices slices, int index) =>
            _configsByKey != null && _configsByKey.TryGetValue((slices.AtlasGuid, slices.InternalIds[index]), out SpriteBorderConfig config)
                ? config
                : null;
    }
}
