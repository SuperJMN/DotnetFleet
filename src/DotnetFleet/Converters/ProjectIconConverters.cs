using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace DotnetFleet.Converters;

public static class ProjectIconConverters
{
    public static readonly IValueConverter ByteArrayToBitmap =
        new FuncValueConverter<byte[]?, IImage?>(bytes =>
        {
            if (bytes is null || bytes.Length == 0)
                return null;

            try
            {
                using var stream = new MemoryStream(bytes);
                return new Bitmap(stream);
            }
            catch
            {
                return null;
            }
        });
}
