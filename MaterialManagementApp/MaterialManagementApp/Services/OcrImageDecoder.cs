using System;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace MaterialManagementApp.Services;

/// <summary>
/// OCR 图片解码器：把任意图片流解码成引擎所需的 BGRA32 交错像素，
/// 超过最长边上限的图先等比缩小再解码，控制识别耗时。
/// 单张识别窗口与批量扫描窗口共用，保证解码行为一致。
/// </summary>
public static class OcrImageDecoder
{
    /// <summary>图片最长边上限，超过则等比缩小后再识别，避免超大图拖慢推理。</summary>
    public const uint MaxImageDimension = 2400;

    /// <summary>解码结果：BGRA32 像素数据与宽高（stride = 宽度 × 4）。</summary>
    public sealed record DecodedImage(byte[] Bgra, int Width, int Height);

    /// <summary>
    /// 把图片流解码成 BGRA32 像素。流会被回卷到起点后读取，调用前后流位置不保证保留。
    /// </summary>
    /// <param name="stream">图片数据流（文件、剪贴板位图等）。</param>
    /// <param name="maxDimension">最长边上限，超出部分等比缩小。</param>
    /// <returns>像素数据与尺寸。</returns>
    public static async Task<DecodedImage> DecodeAsync(IRandomAccessStream stream, uint maxDimension = MaxImageDimension)
    {
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);

        SoftwareBitmap bitmap;
        if (decoder.PixelWidth > maxDimension || decoder.PixelHeight > maxDimension)
        {
            // 等比缩小到上限内：既控制识别耗时，也保留单据文字的可读性
            var scale = Math.Min(
                (double)maxDimension / decoder.PixelWidth,
                (double)maxDimension / decoder.PixelHeight);

            bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform
                {
                    ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * scale),
                    ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * scale)
                },
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
        }
        else
        {
            bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }

        using (bitmap)
        {
            var width = bitmap.PixelWidth;
            var height = bitmap.PixelHeight;

            // SoftwareBitmap 的 BGRA8 缓冲区紧密排列，步长固定为 宽度 × 4，正好匹配引擎的交错像素输入
            var bytes = new byte[width * height * 4];
            var buffer = CryptographicBuffer.CreateFromByteArray(bytes);
            bitmap.CopyToBuffer(buffer);
            CryptographicBuffer.CopyToByteArray(buffer, out var bgra);

            return new DecodedImage(bgra, width, height);
        }
    }
}
