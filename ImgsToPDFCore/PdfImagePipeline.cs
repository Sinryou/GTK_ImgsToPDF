using ImgsToPDF.Shared;
using iText.IO.Image;
using SkiaSharp;
using System.IO.Compression;

namespace ImgsToPDF.Core {
    /// <summary>
    /// 图像加载、解码、EXIF 方向探测与 PDF 格式重编码管道。
    /// 负责将磁盘文件安全转换为适合写入 PDF 的 PageImage 对象。
    /// </summary>
    internal static class PdfImagePipeline {
        /// <summary>
        /// iText 能直接嵌入 PDF 的格式：这些格式在非 --fast 模式下走"原始字节直通"，
        /// 由 iText 按 DCTDecode / FlateDecode 原样写入，不经过解码与重编码。
        /// WebP 不在此列 —— iText 不认 WebP 字节，必须先用 SkiaSharp 解码再转码。
        /// </summary>
        private static readonly HashSet<string> PassthroughExtensions = new(StringComparer.OrdinalIgnoreCase) {
            ".png", ".apng", ".jpg", ".jpeg", ".jfif", ".pjpeg", ".pjp", ".bmp", ".tif", ".tiff", ".gif"
        };

        /// <summary>
        /// SkiaSharp 没有 TIFF 解码器，而 iText 自带 TIFF 读取，
        /// 因此 TIFF 只能走直通，不参与任何重编码（--fast 下也不例外）。
        /// </summary>
        private static readonly HashSet<string> TiffExtensions = new(StringComparer.OrdinalIgnoreCase) {
            ".tif", ".tiff"
        };

        /// <summary>
        /// 可能带 EXIF 方向标记的格式。只有这些格式需要额外探测方向，
        /// 其余格式（PNG/BMP/GIF/WebP）的方向由像素本身决定。
        /// </summary>
        private static readonly HashSet<string> ExifOrientationExtensions = new(StringComparer.OrdinalIgnoreCase) {
            ".jpg", ".jpeg", ".jfif", ".pjpeg", ".pjp"
        };

        /// <summary>
        /// 待排入 PDF 的一张图片：iText 图像对象 + 仍需在排版期应用的 EXIF 方向。
        /// 方向留到排版期而不是解码期，是为了让原始字节（尤其是 JPEG）能无损直通 PDF：
        /// 解码期旋转必然要重新编码，会带来画质损失与体积膨胀。
        /// </summary>
        internal sealed class PageImage {
            public required ImageData Image;
            /// <summary>EXIF Orientation，1 表示无需变换（方向已在解码期烘焙进位图时同样为 1）。</summary>
            public ushort Orientation = 1;

            /// <summary>应用 EXIF 方向之后的显示宽度。</summary>
            public float Width => ExifSwapsAxes(Orientation) ? Image.GetHeight() : Image.GetWidth();

            /// <summary>应用 EXIF 方向之后的显示高度。</summary>
            public float Height => ExifSwapsAxes(Orientation) ? Image.GetWidth() : Image.GetHeight();
        }

        /// <summary>
        /// 判断文件是否为受支持的图片（供目录枚举过滤，扩展名集合与 GUI 的 ImageContracts 保持一致）。
        /// </summary>
        public static bool IsSupportedImage(string path) =>
            ImageContracts.ImageExtensions.Contains(System.IO.Path.GetExtension(path));

        /// <summary>
        /// EXIF Orientation 中 5~8 属于转置类变换，显示时宽高互换。
        /// </summary>
        public static bool ExifSwapsAxes(ushort orientation) => orientation is 5 or 6 or 7 or 8;

        /// <summary>
        /// 从 Lua 配置读取一项 JPEG 质量（1~100）；取值非法或读取异常时回落到
        /// <paramref name="fallback"/>。
        /// </summary>
        private static long ReadConfiguredQuality(Func<IConfig, int> selector, long fallback) {
            try {
                if (CSGlobal.luaConfig is IConfig config) {
                    int q = selector(config);
                    if (q >= 1 && q <= 100) {
                        return q;
                    }
                }
            }
            catch {
                // Lua 配置读取异常时安全降级
            }
            return fallback;
        }

        /// <summary>开启 --fast 时使用的 JPEG 压缩质量，取自 config.lua 的 Config.FastQuality。</summary>
        public static long GetFastJpegQuality() =>
            ReadConfiguredQuality(static c => c.FastQuality, 75);

        /// <summary>
        /// WebP 原始 RGB 快路径使用的 Flate 级别（1~9），取自 config.lua 的 Config.FlateLevel。
        /// 返回 0 表示未配置或非法，交由 iText 用库默认级别压缩。
        /// 0 必须视为「未配置」：XLua 的 CSharpCallLua 接口在 Lua 表缺少该字段时返回 default(int)，
        /// 若把 0 传给 ZLib 会被当成「不压缩」，PDF 体积会突然膨胀到接近原始像素大小。
        /// </summary>
        public static int GetFlateLevel() {
            try {
                if (CSGlobal.luaConfig is IConfig config) {
                    int level = config.FlateLevel;
                    return level is >= 1 and <= 9 ? level : 0;
                }
            }
            catch {
                // Lua 配置读取异常时安全降级
            }
            return 0;
        }

        /// <summary>
        /// 用 SkiaSharp 解码。SKImage.FromEncodedData 会自动应用 EXIF 方向
        /// （注意 SKBitmap.Decode 不会，不要换成那个重载）。
        /// </summary>
        private static SKBitmap? DecodeBitmap(byte[] raw) {
            using var image = SKImage.FromEncodedData(raw);
            return image == null ? null : SKBitmap.FromImage(image);
        }

        /// <summary>
        /// 载入图片并生成可直接写入 PDF 的 PageImage。
        ///
        /// 非 --fast 模式：
        /// 1. iText 能直接嵌入的格式（JPEG/PNG/GIF/BMP/TIFF）→ 原始字节直通，零重编码；
        /// 2. EXIF 方向无论 2~4 还是 5~8，一律留到排版期由仿射矩阵应用 ——
        ///    不再把镜像/180° 烘焙进像素，因此不会产生任何有损重编码；
        /// 3. WebP → iText 不认，解码后把不透明像素作为原始 RGB 交给 iText 做一次 Flate 写入
        ///    （带 alpha 的仍编码 PNG 以保留 /SMask）。
        ///
        /// --fast 模式下一律解码并按 FastQuality 重编码为 JPEG，以最大化减小体积。
        /// </summary>
        public static PageImage LoadPageImage(string imagePath, bool fastFlag) {
            string extension = System.IO.Path.GetExtension(imagePath);

            // TIFF：SkiaSharp 没有解码器，任何模式下都只能由 iText 直通（--fast 下也不重编码）。
            // 必须排在 fast 分支之前，否则会掉进"解码再压缩"的路径并因无解码器而失败。
            if (TiffExtensions.Contains(extension)) {
                return new PageImage {
                    Image = ImageDataFactory.Create(imagePath),
                    Orientation = JpegExif.GetTiffOrientation(imagePath)
                };
            }

            // --- 开启 fastFlag：常规格式均压缩为指定质量的 JPEG ---
            if (fastFlag) {
                // 解码与重编码都必须看到完整像素，这里只能整读；
                // SKImage.FromEncodedData 会把方向烘焙进像素，故无需再传 Orientation
                using var fastBitmap = DecodeBitmap(File.ReadAllBytes(imagePath))
                    ?? throw new InvalidOperationException("Unsupported or corrupt image data.");
                return new PageImage { Image = ImageDataFactory.Create(EncodeJpegOnWhite(fastBitmap, GetFastJpegQuality())) };
            }

            // --- 未开启 fastFlag：原始字节无损直通 ---
            // 交给 iText 从文件流读取，避免在托管堆上再复制一整份图片数据；
            // EXIF 方向只探测文件头（128 KB），同样不需要整读。
            if (PassthroughExtensions.Contains(extension)) {
                ushort orientation = ExifOrientationExtensions.Contains(extension)
                    ? JpegExif.GetOrientation(imagePath)
                    : (ushort)1;
                return new PageImage {
                    Image = ImageDataFactory.Create(imagePath),
                    Orientation = orientation
                };
            }

            // WebP：iText 不认其字节，必须解码。解码后不再编码 PNG 让 iText 重新解析
            // （那是「解码 → PNG 编码 → iText 再解析 PNG」两次全像素往返），
            // 而是把像素直接交给 iText 做一次 Flate 写入。
            using var bitmap = DecodeBitmap(File.ReadAllBytes(imagePath))
                ?? throw new InvalidOperationException("Unsupported or corrupt image data.");

            // 带 alpha 的图仍走 PNG：只有 PNG 能让 iText 生成 /SMask 保留透明背景
            if (bitmap.AlphaType != SKAlphaType.Opaque) {
                return new PageImage { Image = ImageDataFactory.Create(EncodePng(bitmap)) };
            }
            return new PageImage { Image = CreateRgbImage(bitmap) };
        }

        /// <summary>
        /// 把不透明的位图作为原始 RGB 像素交给 iText：iText 只做一次 Flate 写入。
        /// Flate 级别取自 config.lua 的 <c>Config.FlateLevel</c>（1~9）；未配置或非法时用库默认级别。
        ///
        /// 为什么要自己压缩：ImageData 没有逐图压缩级别入口，图像 XObject 由 PdfCanvas 内部创建，
        /// 拿不到 PdfStream 实例；但实测 SetDeflated(true) 会让 iText 原样写入字节并保留
        /// /Filter/FlateDecode，因此「先用 ZLib 按指定级别压好、再声明已压缩」即可控制级别。
        /// ZLibStream 而非 DeflateStream：PDF 的 FlateDecode 要求 zlib 封装。
        /// </summary>
        private static ImageData CreateRgbImage(SKBitmap bitmap) {
            byte[] rgb = ToTightRgb(bitmap);
            int level = GetFlateLevel();
            if (level == 0) {
                return ImageDataFactory.Create(bitmap.Width, bitmap.Height, 3, 8, rgb, null);
            }

            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(
                       compressed,
                       new ZLibCompressionOptions { CompressionLevel = level },
                       leaveOpen: true)) {
                zlib.Write(rgb, 0, rgb.Length);
            }

            ImageData data = ImageDataFactory.Create(bitmap.Width, bitmap.Height, 3, 8, compressed.ToArray(), null);
            data.SetDeflated(true);
            return data;
        }

        /// <summary>
        /// 把位图转成行间无 padding 的 3 字节/像素 RGB。
        /// 命中 Rgba8888/Bgra8888 时直接按字节重排，不再额外拷贝一整幅图；
        /// 其它颜色类型（如索引色）先转 Rgba8888 再处理。
        /// </summary>
        private static byte[] ToTightRgb(SKBitmap bitmap) {
            SKBitmap? converted = null;
            try {
                SKBitmap source = bitmap;
                bool packed32 = source.ColorType == SKColorType.Rgba8888 || source.ColorType == SKColorType.Bgra8888;

                // 32bpp 的行宽恒为 width*4，这里仍显式校验：一旦出现行尾 padding，
                // 按 width*4 线性读会把下一行的像素错位搬到本行末尾（静默的画面错位），
                // 因此宁可先转一份行宽紧凑的副本。
                if (!packed32 || source.RowBytes != source.Width * 4) {
                    converted = source.Copy(SKColorType.Rgba8888)
                        ?? throw new InvalidOperationException("Failed to convert image to Rgba8888.");
                    source = converted;
                }

                bool bgra = source.ColorType == SKColorType.Bgra8888;
                ReadOnlySpan<byte> pixels = source.GetPixelSpan();
                byte[] rgb = new byte[checked(source.Width * source.Height * 3)];

                // 平铺单层循环：s 与 d 同步单调递增，JIT 能证明 rgb[d..d+2]（上界即 rgb.Length）
                // 与 pixels[s..s+2] 都在界内，从而消掉逐字节边界检查。
                // 改成按行取像素（每行重算起点）会让边界检查无法消除，实测整批 WebP 慢 26%，
                // 所以这里宁可保留一层循环，靠上面的 RowBytes 校验来保证正确性。
                int d = 0;
                if (bgra) {
                    for (int s = 0; d < rgb.Length; s += 4, d += 3) {
                        rgb[d] = pixels[s + 2];
                        rgb[d + 1] = pixels[s + 1];
                        rgb[d + 2] = pixels[s];
                    }
                }
                else {
                    for (int s = 0; d < rgb.Length; s += 4, d += 3) {
                        rgb[d] = pixels[s];
                        rgb[d + 1] = pixels[s + 1];
                        rgb[d + 2] = pixels[s + 2];
                    }
                }
                return rgb;
            }
            finally {
                converted?.Dispose();
            }
        }

        /// <summary>
        /// 把 SKBitmap 编码为 JPEG。JPEG 无 alpha 通道，透明源必须先铺白底，
        /// 否则透明区域转 JPEG 后会变黑。
        /// </summary>
        private static byte[] EncodeJpegOnWhite(SKBitmap bitmap, long quality) {
            var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
            using var surface = SKSurface.Create(info);
            surface.Canvas.Clear(SKColors.White);
            using (var image = SKImage.FromBitmap(bitmap)) {
                surface.Canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
            }
            using var snapshot = surface.Snapshot();
            using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, (int)quality);
            return data?.ToArray()
                ?? throw new InvalidOperationException("Failed to encode image to Jpeg.");
        }

        /// <summary>把 SKBitmap 无损编码为 PNG</summary>
        private static byte[] EncodePng(SKBitmap bitmap) {
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data?.ToArray()
                ?? throw new InvalidOperationException("Failed to encode image to Png.");
        }
    }
}
