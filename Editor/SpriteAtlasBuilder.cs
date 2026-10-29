using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AtlasForge.Editor
{
    /// <summary>Placement and slicing info for one source sprite inside the atlas.</summary>
    public sealed class AtlasSpriteEntry
    {
        public string Name;
        public Texture2D Texture;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public Vector4 Border;
    }

    /// <summary>
    /// Collects textures, force-enables read/write, packs them into the smallest
    /// canvas they fit in and writes the result to disk.
    /// </summary>
    public static class SpriteAtlasBuilder
    {
        private const int MinAtlasSize = 32;

        /// <summary>Non-power-of-two atlases stay on a 4px grid so block compression remains valid.</summary>
        private const int SizeAlignment = 4;

        /// <summary>
        /// Canvas widths to try, relative to the width a perfectly square atlas would need.
        /// The best aspect ratio depends on the sprite mix, so a few shapes are searched.
        /// </summary>
        private static readonly float[] WidthScales = { 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f, 3f, 4f };

        /// <summary>Which free rectangle a sprite is dropped into when several could hold it.</summary>
        private enum FitHeuristic
        {
            /// <summary>Smallest leftover strip; keeps the remaining space in large blocks.</summary>
            ShortSide,

            /// <summary>Smallest free rectangle; spends awkward gaps before opening new ground.</summary>
            AreaFit
        }

        private static readonly FitHeuristic[] Heuristics = { FitHeuristic.ShortSide, FitHeuristic.AreaFit };

        /// <summary>
        /// Finds all readable-capable Texture2D assets in a folder, largest first. Textures
        /// already sliced into multiple sprites are skipped: those are atlases themselves, so
        /// this keeps a previous output from being packed back in when the atlas is renamed.
        /// </summary>
        public static List<string> CollectTexturePaths(string folderPath)
        {
            return AssetDatabase.FindAssets("t:Texture2D", new[] { folderPath })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Distinct()
                .Where(path => AssetImporter.GetAtPath(path) is TextureImporter importer &&
                               importer.spriteImportMode != SpriteImportMode.Multiple)
                .OrderByDescending(path =>
                {
                    Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                    return texture != null ? texture.width * texture.height : 0;
                })
                .ToList();
        }

        /// <summary>Toggles read/write on the given textures; returns those that were changed.</summary>
        public static List<string> SetReadable(IEnumerable<string> paths, bool readable)
        {
            var changed = new List<string>();
            foreach (string path in paths)
            {
                if (AssetImporter.GetAtPath(path) is TextureImporter importer && importer.isReadable != readable)
                {
                    importer.isReadable = readable;
                    importer.SaveAndReimport();
                    changed.Add(path);
                }
            }

            return changed;
        }

        /// <summary>
        /// Searches for the smallest-area canvas the sprites fit in: several widths are
        /// tried, and for each one the minimum workable height is binary-searched, so no
        /// row of empty pixels survives at the top. Unless <paramref name="forcePowerOfTwo"/>
        /// is set the result is a 4px-aligned size rather than a power of two, which avoids
        /// doubling a dimension just to gain a few hundred pixels.
        /// </summary>
        public static bool TryPack(
            List<AtlasSpriteEntry> entries, int padding, int maxSize, bool forcePowerOfTwo,
            out int atlasWidth, out int atlasHeight)
        {
            atlasWidth = 0;
            atlasHeight = 0;
            if (entries.Count == 0)
            {
                return false;
            }

            List<List<AtlasSpriteEntry>> orderings = BuildOrderings(entries);
            long requiredArea = entries.Sum(entry => (long)(entry.Width + padding) * (entry.Height + padding));
            int minWidth = entries.Max(entry => entry.Width + padding);
            int minHeight = entries.Max(entry => entry.Height + padding);
            if (minWidth > maxSize || minHeight > maxSize)
            {
                return false;
            }

            long bestArea = long.MaxValue;
            int squareWidth = Mathf.CeilToInt(Mathf.Sqrt(requiredArea));
            foreach (int width in CandidateWidths(minWidth, maxSize, squareWidth, forcePowerOfTwo))
            {
                if (!TryMinimiseHeight(orderings, width, padding, minHeight, maxSize, requiredArea, forcePowerOfTwo, out int height))
                {
                    continue;
                }

                long area = (long)width * height;
                if (area >= bestArea)
                {
                    continue;
                }

                bestArea = area;
                atlasWidth = width;
                atlasHeight = height;
            }

            // The search leaves the entries holding whatever size was tried last, so the
            // winning layout has to be replayed before the coordinates can be used.
            return bestArea != long.MaxValue && Fits(orderings, atlasWidth, atlasHeight, padding);
        }

        /// <summary>Fraction of the atlas covered by sprite pixels, ignoring padding (0..1).</summary>
        public static float FillRatio(List<AtlasSpriteEntry> entries, int atlasWidth, int atlasHeight)
        {
            long used = entries.Sum(entry => (long)entry.Width * entry.Height);
            return atlasWidth > 0 && atlasHeight > 0 ? used / (float)((long)atlasWidth * atlasHeight) : 0f;
        }

        /// <summary>Renders the packed entries into a transparent RGBA32 atlas texture.</summary>
        public static Texture2D Render(List<AtlasSpriteEntry> entries, int width, int height)
        {
            var atlas = new Texture2D(width, height, TextureFormat.RGBA32, false);
            atlas.SetPixels32(new Color32[width * height]);
            foreach (AtlasSpriteEntry entry in entries)
            {
                Color32[] pixels = entry.Texture.GetPixels32();
                atlas.SetPixels32(entry.X, entry.Y, entry.Width, entry.Height, pixels);
            }

            atlas.Apply();
            return atlas;
        }

        /// <summary>Encodes the atlas to a PNG inside the source folder and imports it.</summary>
        public static string WritePng(Texture2D atlas, string folderPath, string atlasName)
        {
            string path = $"{folderPath}/{atlasName}.png";
            File.WriteAllBytes(path, atlas.EncodeToPNG());
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return path;
        }

        /// <summary>
        /// Decodes an image file straight from disk into a new readable RGBA32 texture,
        /// bypassing import settings. Returns null if the file is missing or not PNG/JPG.
        /// The caller owns the texture and must destroy it.
        /// </summary>
        public static Texture2D LoadFromFile(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (texture.LoadImage(File.ReadAllBytes(path)))
            {
                return texture;
            }

            Object.DestroyImmediate(texture);
            return null;
        }

        /// <summary>
        /// Insertion orders worth trying. Big pieces have to go down while the canvas is
        /// empty, but whether "big" means longest side or largest area depends on the set.
        /// </summary>
        private static List<List<AtlasSpriteEntry>> BuildOrderings(List<AtlasSpriteEntry> entries)
        {
            return new List<List<AtlasSpriteEntry>>
            {
                entries
                    .OrderByDescending(entry => Mathf.Max(entry.Width, entry.Height))
                    .ThenByDescending(entry => (long)entry.Width * entry.Height)
                    .ToList(),
                entries
                    .OrderByDescending(entry => (long)entry.Width * entry.Height)
                    .ThenByDescending(entry => Mathf.Max(entry.Width, entry.Height))
                    .ToList()
            };
        }

        /// <summary>Finds the smallest height that still holds everything at the given width.</summary>
        private static bool TryMinimiseHeight(
            List<List<AtlasSpriteEntry>> orderings, int width, int padding, int minHeight, int maxSize,
            long requiredArea, bool forcePowerOfTwo, out int height)
        {
            height = 0;

            // Nothing below the total sprite area divided by the width can ever fit.
            int lowest = Mathf.Max(minHeight, (int)((requiredArea + width - 1) / width));
            List<int> candidates = CandidateSizes(lowest, maxSize, forcePowerOfTwo);
            if (candidates.Count == 0 || !Fits(orderings, width, candidates[candidates.Count - 1], padding))
            {
                return false;
            }

            // Invariant: candidates[high] is known to fit, candidates[low - 1] is known not to.
            int low = 0;
            int high = candidates.Count - 1;
            while (low < high)
            {
                int mid = (low + high) / 2;
                if (Fits(orderings, width, candidates[mid], padding))
                {
                    high = mid;
                }
                else
                {
                    low = mid + 1;
                }
            }

            height = candidates[low];
            return true;
        }

        /// <summary>Canvas widths worth trying, from square-ish to increasingly wide.</summary>
        private static List<int> CandidateWidths(int minWidth, int maxSize, int squareWidth, bool forcePowerOfTwo)
        {
            if (forcePowerOfTwo)
            {
                return CandidateSizes(minWidth, maxSize, true);
            }

            int lowest = Mathf.Max(AlignUp(minWidth), MinAtlasSize);
            var widths = new List<int>();
            foreach (float scale in WidthScales)
            {
                int width = Mathf.Clamp(AlignUp(Mathf.RoundToInt(squareWidth * scale)), lowest, maxSize);
                if (!widths.Contains(width))
                {
                    widths.Add(width);
                }
            }

            return widths;
        }

        /// <summary>Ascending list of legal canvas sizes from <paramref name="minSize"/> up.</summary>
        private static List<int> CandidateSizes(int minSize, int maxSize, bool forcePowerOfTwo)
        {
            var sizes = new List<int>();
            if (forcePowerOfTwo)
            {
                for (int size = MinAtlasSize; size <= maxSize; size <<= 1)
                {
                    if (size >= minSize)
                    {
                        sizes.Add(size);
                    }
                }

                return sizes;
            }

            for (int size = Mathf.Max(AlignUp(minSize), MinAtlasSize); size <= maxSize; size += SizeAlignment)
            {
                sizes.Add(size);
            }

            return sizes;
        }

        /// <summary>Tries every ordering/heuristic combination; the first that fits wins.</summary>
        private static bool Fits(List<List<AtlasSpriteEntry>> orderings, int width, int height, int padding)
        {
            foreach (List<AtlasSpriteEntry> ordered in orderings)
            {
                foreach (FitHeuristic heuristic in Heuristics)
                {
                    if (MaxRectsFits(ordered, width, height, padding, heuristic))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// MaxRects bin packing: the canvas is described as a list of overlapping free
        /// rectangles, so the space left above a short sprite stays available to later
        /// ones instead of being lost to a shelf. Returns false if anything doesn't fit.
        /// </summary>
        private static bool MaxRectsFits(
            List<AtlasSpriteEntry> ordered, int width, int height, int padding, FitHeuristic heuristic)
        {
            var free = new List<RectInt> { new(0, 0, width, height) };
            var slabs = new List<RectInt>();
            var kept = new List<RectInt>();
            foreach (AtlasSpriteEntry entry in ordered)
            {
                // Padding is reserved as part of the cell, keeping a gutter to the right of
                // and above each sprite; neighbours therefore never touch.
                var cell = new Vector2Int(entry.Width + padding, entry.Height + padding);
                if (!TryFindBestFit(free, cell, heuristic, out Vector2Int origin))
                {
                    return false;
                }

                entry.X = origin.x;
                entry.Y = origin.y;
                SplitFreeRects(free, new RectInt(origin.x, origin.y, cell.x, cell.y), slabs, kept);
            }

            return true;
        }

        /// <summary>Picks the free rectangle that wastes the least by the given measure.</summary>
        private static bool TryFindBestFit(
            List<RectInt> free, Vector2Int cell, FitHeuristic heuristic, out Vector2Int origin)
        {
            origin = Vector2Int.zero;
            bool found = false;
            int bestPrimary = 0;
            int bestSecondary = 0;
            foreach (RectInt candidate in free)
            {
                if (candidate.width < cell.x || candidate.height < cell.y)
                {
                    continue;
                }

                int leftoverX = candidate.width - cell.x;
                int leftoverY = candidate.height - cell.y;
                int shortSide = Mathf.Min(leftoverX, leftoverY);
                int primary = heuristic == FitHeuristic.AreaFit
                    ? candidate.width * candidate.height - cell.x * cell.y
                    : shortSide;
                int secondary = heuristic == FitHeuristic.AreaFit
                    ? shortSide
                    : Mathf.Max(leftoverX, leftoverY);

                if (found && (primary > bestPrimary || (primary == bestPrimary && secondary >= bestSecondary)))
                {
                    continue;
                }

                origin = new Vector2Int(candidate.x, candidate.y);
                bestPrimary = primary;
                bestSecondary = secondary;
                found = true;
            }

            return found;
        }

        /// <summary>Carves the newly occupied rect out of every free rect it overlaps.</summary>
        private static void SplitFreeRects(List<RectInt> free, RectInt placed, List<RectInt> slabs, List<RectInt> kept)
        {
            slabs.Clear();

            // Backwards so removals don't disturb the indices still to be visited.
            for (int i = free.Count - 1; i >= 0; i--)
            {
                RectInt candidate = free[i];
                if (!candidate.Overlaps(placed))
                {
                    continue;
                }

                free.RemoveAt(i);

                if (placed.x > candidate.x)
                {
                    slabs.Add(new RectInt(candidate.x, candidate.y, placed.x - candidate.x, candidate.height));
                }

                if (placed.xMax < candidate.xMax)
                {
                    slabs.Add(new RectInt(placed.xMax, candidate.y, candidate.xMax - placed.xMax, candidate.height));
                }

                if (placed.y > candidate.y)
                {
                    slabs.Add(new RectInt(candidate.x, candidate.y, candidate.width, placed.y - candidate.y));
                }

                if (placed.yMax < candidate.yMax)
                {
                    slabs.Add(new RectInt(candidate.x, placed.yMax, candidate.width, candidate.yMax - placed.yMax));
                }
            }

            // Only the new slabs can be redundant: each is a subset of a rectangle that was
            // just removed, and no surviving rectangle was contained in one of those.
            kept.Clear();
            foreach (RectInt slab in slabs)
            {
                if (IsContained(free, slab) || IsContained(kept, slab))
                {
                    continue;
                }

                for (int i = kept.Count - 1; i >= 0; i--)
                {
                    if (Contains(slab, kept[i]))
                    {
                        kept.RemoveAt(i);
                    }
                }

                kept.Add(slab);
            }

            free.AddRange(kept);
        }

        private static bool IsContained(List<RectInt> rects, RectInt rect)
        {
            foreach (RectInt candidate in rects)
            {
                if (Contains(candidate, rect))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool Contains(RectInt outer, RectInt inner) =>
            outer.x <= inner.x && outer.y <= inner.y &&
            outer.xMax >= inner.xMax && outer.yMax >= inner.yMax;

        private static int AlignUp(int value) => (value + SizeAlignment - 1) / SizeAlignment * SizeAlignment;
    }
}
