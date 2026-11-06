namespace NexusWindows.Tests;

// Shared Nexus image rule (NexusShared/images.md, fallback of 2026-11-06), one engine for the three editions:
// lossless WebP; over the limit, lossless WebP with 2 then 3 low bits dropped (never 4); then JPEG 82, 72, 60 at the
// same pixel size; only then a smaller image.
public class ImageFallbackTests
{
    // Smooth gradients plus noise of a given amplitude: the more noise, the more bytes each step costs.
    static byte[] Picture(int width, int height, int noise, int seed = 7)
    {
        var random = new Random(seed);
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                bgra[i] = (byte)Math.Clamp(x * 255 / width + random.Next(-noise, noise + 1), 0, 255);
                bgra[i + 1] = (byte)Math.Clamp(y * 255 / height + random.Next(-noise, noise + 1), 0, 255);
                bgra[i + 2] = (byte)Math.Clamp((x + y) * 255 / (width + height) + random.Next(-noise, noise + 1), 0, 255);
                bgra[i + 3] = 255;
            }
        return bgra;
    }

    static bool IsWebP(byte[] b) => b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P';

    // JPEG frame size from its SOF marker (baseline or progressive).
    static (int Width, int Height) JpegSize(byte[] b)
    {
        Assert.True(b[0] == 0xFF && b[1] == 0xD8, "JPEG starts with SOI");
        Assert.True(b[^2] == 0xFF && b[^1] == 0xD9, "JPEG ends with EOI: no trailing bytes from the HGLOBAL");
        for (var i = 2; i + 9 < b.Length;)
        {
            if (b[i] != 0xFF) { i++; continue; }
            var marker = b[i + 1];
            var length = (b[i + 2] << 8) | b[i + 3];
            if (marker is 0xC0 or 0xC1 or 0xC2) return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
            i += 2 + length;
        }
        throw new Xunit.Sdk.XunitException("No SOF marker");
    }

    [Fact]
    public void A_screen_that_fits_stays_lossless_webp()
    {
        var pixels = Picture(320, 200, 0);
        var encoded = ImageEncoding.Fit(pixels, 320, 200);
        Assert.NotNull(encoded);
        Assert.Equal("webp-lossless", encoded.Value.How);
        Assert.Equal("image/webp", encoded.Value.MimeType);
        Assert.Equal(".webp", encoded.Value.Extension);
        Assert.True(IsWebP(encoded.Value.Bytes));
        Assert.Equal(WebP.Lossless(pixels, 320, 200), encoded.Value.Bytes);
    }

    [Fact]
    public void Each_step_is_taken_in_order_as_the_limit_tightens()
    {
        const int w = 256, h = 192;
        var pixels = Picture(w, h, 24);
        var lossless = WebP.Lossless(pixels, w, h).Length;
        var two = WebP.Lossless(ImageEncoding.DropLowBits(pixels, 2), w, h).Length;
        var three = WebP.Lossless(ImageEncoding.DropLowBits(pixels, 3), w, h).Length;
        var jpeg82 = ImageEncoding.Jpeg(pixels, w, h, 82).Length;
        var jpeg60 = ImageEncoding.Jpeg(pixels, w, h, 60).Length;
        Assert.True(lossless > two && two > three && three > jpeg82 && jpeg82 > jpeg60, $"{lossless} {two} {three} {jpeg82} {jpeg60}");

        Assert.Equal("webp-lossless", ImageEncoding.Fit(pixels, w, h, lossless)!.Value.How);
        var near2 = ImageEncoding.Fit(pixels, w, h, lossless - 1)!.Value;
        Assert.Equal("webp-near-lossless-2bits", near2.How);
        Assert.Equal(WebP.Lossless(ImageEncoding.DropLowBits(pixels, 2), w, h), near2.Bytes);
        Assert.Equal("webp-near-lossless-3bits", ImageEncoding.Fit(pixels, w, h, two - 1)!.Value.How);
        var jpeg = ImageEncoding.Fit(pixels, w, h, three - 1)!.Value;
        Assert.Equal("jpeg-q82", jpeg.How);
        Assert.Equal("image/jpeg", jpeg.MimeType);
        Assert.Equal(".jpg", jpeg.Extension);
        Assert.Equal((w, h), JpegSize(jpeg.Bytes)); // same pixel size: no shrinking before the lossy steps
        Assert.Equal("jpeg-q72", ImageEncoding.Fit(pixels, w, h, jpeg82 - 1)!.Value.How);
        Assert.Null(ImageEncoding.Fit(pixels, w, h, jpeg60 - 1)); // nothing fits: the caller shrinks
    }

    [Fact]
    public void Never_more_than_three_bits_are_dropped()
    {
        Assert.Equal([2, 3], ImageEncoding.DroppedBits);
        Assert.Equal(82, ImageEncoding.JpegQualities[0]);
        const int w = 128, h = 96;
        var pixels = Picture(w, h, 40);
        var three = WebP.Lossless(ImageEncoding.DropLowBits(pixels, 3), w, h).Length;
        // A limit that 4 dropped bits would meet goes straight to JPEG.
        var encoded = ImageEncoding.Fit(pixels, w, h, three - 1)!.Value;
        Assert.StartsWith("jpeg-", encoded.How);
    }

    [Fact]
    public void Dropping_bits_masks_colour_channels_and_keeps_alpha()
    {
        byte[] pixels = [0xFF, 0x0F, 0x07, 0x8B];
        Assert.Equal(new byte[] { 0xFC, 0x0C, 0x04, 0x8B }, ImageEncoding.DropLowBits(pixels, 2));
        Assert.Equal(new byte[] { 0xF8, 0x08, 0x00, 0x8B }, ImageEncoding.DropLowBits(pixels, 3));
        Assert.Equal(new byte[] { 0xFF, 0x0F, 0x07, 0x8B }, pixels); // the source is untouched
    }

    [Fact]
    public void Jpeg_ignores_a_transparent_alpha_left_by_PrintWindow()
    {
        var opaque = Picture(64, 48, 10);
        var transparent = (byte[])opaque.Clone();
        for (var i = 3; i < transparent.Length; i += 4) transparent[i] = 0;
        Assert.Equal(ImageEncoding.Jpeg(opaque, 64, 48, 82), ImageEncoding.Jpeg(transparent, 64, 48, 82));
    }

    [Fact]
    public void A_capture_over_the_limit_keeps_its_size_and_goes_jpeg_before_any_shrinking()
    {
        // 1600x1000 of noise: lossless and near-lossless WebP all exceed 500 KB, a JPEG step fits at full size.
        const int w = 1600, h = 1000;
        var pixels = Picture(w, h, 60);
        Assert.True(WebP.Lossless(ImageEncoding.DropLowBits(pixels, 3), w, h).Length > ImageEncoding.Limit);
        var (image, width, height, how) = Capture.Encode(pixels, w, h);
        Assert.Equal((w, h), (width, height));
        Assert.Matches("^jpeg-q(82|72|60)$", how);
        Assert.True(image.Bytes.Length <= ImageEncoding.Limit);
        Assert.Equal((w, h), JpegSize(image.Bytes));
    }

    [Fact]
    public void A_capture_shrinks_only_when_no_step_fits()
    {
        // Pure noise over the whole range: even JPEG 60 exceeds 500 KB at full size.
        const int w = 2000, h = 1125;
        var random = new Random(3);
        var pixels = new byte[w * h * 4];
        random.NextBytes(pixels);
        var (image, width, height, how) = Capture.Encode(pixels, w, h);
        Assert.True(width < w && height < h);
        Assert.EndsWith($"-{width}x{height}", how);
        Assert.True(image.Bytes.Length <= ImageEncoding.Limit);
    }

    [Theory]
    [InlineData(@"C:\out\shot.webp", @"C:\c\a.jpg", @"C:\out\shot.jpg")]
    [InlineData(@"C:\out\shot.webp", @"C:\c\a.webp", @"C:\out\shot.webp")]
    [InlineData(@"C:\out\shot.jpeg", @"C:\c\a.jpg", @"C:\out\shot.jpeg")]
    [InlineData(@"C:\out\shot.png", @"C:\c\a.webp", @"C:\out\shot.webp")]
    [InlineData(@"C:\out\shot", @"C:\c\a.jpg", @"C:\out\shot.jpg")]
    public void An_export_carries_the_extension_of_the_format_sent(string requested, string capture, string expected) =>
        Assert.Equal(expected, McpServer.ExportPath(requested, capture));
}
