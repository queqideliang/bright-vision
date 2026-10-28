using System;

namespace BrightVision.Revit
{
    // Original B/V mark, rasterized from simple curves/segments. No font,
    // third-party image, resource DLL or UI runtime is needed for the pixels.
    internal static class BrandPixels
    {
        internal static byte[] Create(int size)
        {
            if (size != 16 && size != 32) throw new ArgumentOutOfRangeException("size");
            byte[] pixels = new byte[size * size * 4];
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            {
                int red = 0, green = 0, blue = 0, samples = 0;
                for (int sy = 0; sy < 4; sy++) for (int sx = 0; sx < 4; sx++)
                {
                    double px = (x + (sx + 0.5) / 4) * 32 / size, py = (y + (sy + 0.5) / 4) * 32 / size;
                    double cornerX = Math.Max(Math.Abs(px - 16) - 8, 0), cornerY = Math.Max(Math.Abs(py - 16) - 8, 0);
                    if (cornerX * cornerX + cornerY * cornerY > 49) continue;
                    int r = 16, g = 37, b = 63;
                    bool stem = px >= 5.5 && px <= 8.5 && py >= 6 && py <= 26;
                    bool upper = px >= 8 && Ellipse(px, py, 10.7, 11.1, 6.2, 5.1) && !Ellipse(px, py, 10.4, 11.1, 3.1, 2.25);
                    bool lower = px >= 8 && Ellipse(px, py, 10.7, 20.7, 6.2, 5.3) && !Ellipse(px, py, 10.4, 20.7, 3.1, 2.4);
                    if (stem || upper || lower) { r = 64; g = 219; b = 215; }
                    if (Distance(px, py, 18.3, 7.5, 22.5, 24.5) < 1.55 || Distance(px, py, 22.5, 24.5, 27.0, 7.5) < 1.55)
                    { r = 68; g = 228; b = 163; }
                    red += r; green += g; blue += b; samples++;
                }
                if (samples == 0) continue;
                int offset = (y * size + x) * 4;
                pixels[offset] = (byte)(blue / samples); pixels[offset + 1] = (byte)(green / samples);
                pixels[offset + 2] = (byte)(red / samples); pixels[offset + 3] = (byte)(255 * samples / 16);
            }
            return pixels;
        }

        private static bool Ellipse(double x, double y, double cx, double cy, double rx, double ry)
        { double dx = (x - cx) / rx, dy = (y - cy) / ry; return dx * dx + dy * dy <= 1; }

        private static double Distance(double x, double y, double ax, double ay, double bx, double by)
        {
            double dx = bx - ax, dy = by - ay, t = Math.Max(0, Math.Min(1, ((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy)));
            dx = x - ax - t * dx; dy = y - ay - t * dy; return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
