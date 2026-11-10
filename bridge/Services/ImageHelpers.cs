using Microsoft.ML.OnnxRuntime.Tensors;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace LocalAIShowroom.Bridge.Services;

public static class ImageHelpers
{
    /// <summary>Decodes PNG/JPEG/BMP bytes into a BGRA8 SoftwareBitmap (the format the imaging APIs expect).</summary>
    public static async Task<SoftwareBitmap> DecodeAsync(byte[] bytes)
    {
        using var memory = new MemoryStream(bytes, writable: false);
        using IRandomAccessStream stream = memory.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }

    /// <summary>
    /// ImageNet-style preprocessing: resize the shorter side to size/0.875, center-crop to the model's
    /// input size, convert to RGB float and normalize with mean/std. Supports NCHW and NHWC inputs.
    /// </summary>
    public static async Task<DenseTensor<float>> ToNormalizedTensorAsync(
        byte[] bytes, int width, int height, bool nchw, IReadOnlyList<float> mean, IReadOnlyList<float> std)
    {
        using var memory = new MemoryStream(bytes, writable: false);
        using IRandomAccessStream stream = memory.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream);

        double srcW = decoder.PixelWidth, srcH = decoder.PixelHeight;
        double scale = (Math.Min(width, height) / 0.875) / Math.Min(srcW, srcH);
        var scaledW = (uint)Math.Max(width, Math.Round(srcW * scale));
        var scaledH = (uint)Math.Max(height, Math.Round(srcH * scale));

        var transform = new BitmapTransform
        {
            ScaledWidth = scaledW,
            ScaledHeight = scaledH,
            InterpolationMode = BitmapInterpolationMode.Fant,
            Bounds = new BitmapBounds
            {
                X = (scaledW - (uint)width) / 2,
                Y = (scaledH - (uint)height) / 2,
                Width = (uint)width,
                Height = (uint)height,
            },
        };

        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        var px = pixelData.DetachPixelData();

        var tensor = nchw
            ? new DenseTensor<float>(new[] { 1, 3, height, width })
            : new DenseTensor<float>(new[] { 1, height, width, 3 });

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4; // BGRA
                var r = (px[i + 2] / 255f - mean[0]) / std[0];
                var g = (px[i + 1] / 255f - mean[1]) / std[1];
                var b = (px[i] / 255f - mean[2]) / std[2];
                if (nchw)
                {
                    tensor[0, 0, y, x] = r;
                    tensor[0, 1, y, x] = g;
                    tensor[0, 2, y, x] = b;
                }
                else
                {
                    tensor[0, y, x, 0] = r;
                    tensor[0, y, x, 1] = g;
                    tensor[0, y, x, 2] = b;
                }
            }
        }

        return tensor;
    }
}
