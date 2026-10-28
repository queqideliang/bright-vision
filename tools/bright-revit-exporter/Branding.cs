using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BrightVision.Revit
{
    internal static class Branding
    {
        internal static BitmapSource Icon(int size)
        {
            BitmapSource image = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, BrandPixels.Create(size), size * 4);
            image.Freeze();
            return image;
        }
    }
}
