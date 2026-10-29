using UnityEngine;

namespace AtlasForge.Editor
{
    /// <summary>
    /// Detects 9-slice borders for a sprite by locating the central band of
    /// near-identical columns and rows (the region that is safe to stretch).
    /// Whatever remains on each side becomes the fixed border.
    /// </summary>
    public static class NineSliceBorderDetector
    {
        /// <summary>Default color-difference tolerance (0..1) used for auto-detection.</summary>
        public const float DefaultTolerance = 0.02f;

        /// <summary>
        /// Returns a (left, bottom, right, top) 9-slice border, in pixels, for the
        /// given texture. Requires the texture to be readable.
        /// </summary>
        public static Vector4 Detect(Texture2D texture, float tolerance = DefaultTolerance)
        {
            int width = texture.width;
            int height = texture.height;
            Color32[] pixels = texture.GetPixels32();

            int centerX = width / 2;
            int firstStretchX = centerX;
            int lastStretchX = centerX;
            while (firstStretchX > 0 && LinesSimilar(pixels, width, height, firstStretchX - 1, firstStretchX, tolerance, true))
                firstStretchX--;
            while (lastStretchX < width - 1 && LinesSimilar(pixels, width, height, lastStretchX, lastStretchX + 1, tolerance, true))
                lastStretchX++;

            int centerY = height / 2;
            int firstStretchY = centerY;
            int lastStretchY = centerY;
            while (firstStretchY > 0 && LinesSimilar(pixels, width, height, firstStretchY - 1, firstStretchY, tolerance, false))
                firstStretchY--;
            while (lastStretchY < height - 1 && LinesSimilar(pixels, width, height, lastStretchY, lastStretchY + 1, tolerance, false))
                lastStretchY++;

            var border = new Vector4(
                firstStretchX,
                firstStretchY,
                width - 1 - lastStretchX,
                height - 1 - lastStretchY);
            return Clamp(border, width, height);
        }

        /// <summary>
        /// Clamps opposing borders so they never overlap, always leaving at least a
        /// one-pixel stretchable strip in the middle.
        /// </summary>
        public static Vector4 Clamp(Vector4 border, int width, int height)
        {
            if (border.x + border.z >= width)
            {
                float half = Mathf.Floor((width - 1) * 0.5f);
                border.x = Mathf.Min(border.x, half);
                border.z = Mathf.Min(border.z, half);
            }

            if (border.y + border.w >= height)
            {
                float half = Mathf.Floor((height - 1) * 0.5f);
                border.y = Mathf.Min(border.y, half);
                border.w = Mathf.Min(border.w, half);
            }

            return border;
        }

        /// <summary>Compares two adjacent columns (vertical) or rows (horizontal) for similarity.</summary>
        private static bool LinesSimilar(Color32[] pixels, int width, int height, int indexA, int indexB, float tolerance, bool vertical)
        {
            int threshold = Mathf.RoundToInt(tolerance * 255f);
            int count = vertical ? height : width;
            for (int k = 0; k < count; k++)
            {
                int pixelA = vertical ? k * width + indexA : indexA * width + k;
                int pixelB = vertical ? k * width + indexB : indexB * width + k;
                Color32 p = pixels[pixelA];
                Color32 q = pixels[pixelB];
                if (Mathf.Abs(p.r - q.r) > threshold ||
                    Mathf.Abs(p.g - q.g) > threshold ||
                    Mathf.Abs(p.b - q.b) > threshold ||
                    Mathf.Abs(p.a - q.a) > threshold)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
