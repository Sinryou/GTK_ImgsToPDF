using iText.IO.Image;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Action;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Navigation;
using iText.Kernel.Geom;
using SkiaSharp;

namespace ImgsToPDFCore {
    public enum Layout {
        Single,
        DuplexLeftToRight,
        DuplexRightToLeft
    }

    internal class PDFWrapper {
        /// <summary>双页模式下两张图之间的中缝宽度（点）。</summary>
        private const float DuplexPageGap = 10f;

        /// <summary>写出 PDF 时使用的临时文件后缀：先写它，成功后再替换目标文件，失败则删除。</summary>
        private const string TempFileSuffix = ".tmp";

        private static readonly StringComparer PathComparer =
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        private static readonly StringComparison PathComparison =
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        /// <summary>
        /// 支持的图片扩展名。用 OrdinalIgnoreCase 比较而不是 ToLower()：
        /// 扩展名来自文件系统，与当前区域设置无关 —— tr-TR 下 "I".ToLower() 是无点 "ı"，
        /// 会把 ".TIF" 判成不支持而静默丢图；顺带省掉每个文件一次 ToLower 分配。
        /// </summary>
        private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase) {
            ".png", ".apng", ".jpg", ".jpeg", ".jfif", ".pjpeg",
            ".pjp", ".bmp", ".tif", ".tiff", ".gif", ".webp"
        };

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
        /// 非 --fast 模式下，方向属于"镜像类"（2/3/4）而不得不重编码时使用的 JPEG 质量。
        /// 旋转类（5~8）可以用仿射矩阵表达，不需要重编码。
        /// 取值来自 config.lua 的 Config.RotatedJpegQuality，缺省 90。
        /// </summary>
        private const int DefaultRotatedJpegQuality = 90;

        /// <summary>
        /// 待排入 PDF 的一张图片：iText 图像对象 + 仍需在排版期应用的 EXIF 方向。
        /// 方向留到排版期而不是解码期，是为了让原始字节（尤其是 JPEG）能无损直通 PDF：
        /// 解码期旋转必然要重新编码，会带来画质损失与体积膨胀。
        /// </summary>
        private sealed class PageImage {
            public required ImageData Image;
            /// <summary>EXIF Orientation，1 表示无需变换（方向已在解码期烘焙进位图时同样为 1）。</summary>
            public ushort Orientation = 1;

            /// <summary>应用 EXIF 方向之后的显示宽度。</summary>
            public float Width => ExifSwapsAxes(Orientation) ? Image.GetHeight() : Image.GetWidth();

            /// <summary>应用 EXIF 方向之后的显示高度。</summary>
            public float Height => ExifSwapsAxes(Orientation) ? Image.GetWidth() : Image.GetHeight();
        }

        /// <summary>
        /// EXIF Orientation 中 5~8 属于转置类变换，显示时宽高互换。
        /// </summary>
        private static bool ExifSwapsAxes(ushort orientation) => orientation is 5 or 6 or 7 or 8;

        /// <summary>
        /// 需要把方向"烘焙"进像素的方向值（2/3/4）。
        /// 这几种变换含镜像或 180° 旋转，无法用轴对齐的 "a b c d e f" 矩阵表达为
        /// 缩放矩形，因此必须在解码期完成。
        /// </summary>
        private static bool RequiresPixelBake(ushort orientation) => orientation is 2 or 3 or 4;

        /// <summary>
        /// 计算把图片按 EXIF 方向放进目标矩形 (x, y, width, height) 的仿射矩阵。
        /// 返回值对应 PDF 的 "a b c d e f cm"：x' = a*u + c*v + e，y' = b*u + d*v + f，
        /// 其中 (u, v) 为图片单位方格坐标（u 向右、v 向上，与 PDF 图像坐标一致）。
        /// 各方向由 EXIF 规范里"第 0 行/第 0 列"的定义推导：1 原样、2 水平镜像、3 旋转 180°、
        /// 4 垂直镜像、5 转置、6 顺时针 90°、7 反转置、8 顺时针 270°。
        /// </summary>
        private static float[] GetOrientationMatrix(ushort orientation, float x, float y, float width, float height) {
            return orientation switch {
                2 => [-width, 0, 0, height, x + width, y],
                3 => [-width, 0, 0, -height, x + width, y + height],
                4 => [width, 0, 0, -height, x, y + height],
                5 => [0, -height, -width, 0, x + width, y + height],
                6 => [0, -height, width, 0, x, y + height],
                7 => [0, height, width, 0, x, y],
                8 => [0, height, -width, 0, x + width, y],
                _ => [width, 0, 0, height, x, y],
            };
        }

        /// <summary>
        /// 用仿射矩阵把一张图片放到指定页的指定矩形内（EXIF 方向在此一并应用）。
        /// 注意 width/height 必须是 <b>显示尺寸</b>（已按方向互换过宽高），
        /// 因为矩阵作用在图像的单位方格上。
        /// </summary>
        private static void DrawImage(PdfCanvas canvas, PageImage pageImage, float x, float y, float width, float height) {
            float[] m = GetOrientationMatrix(pageImage.Orientation, x, y, width, height);
            canvas.AddImageWithTransformationMatrix(pageImage.Image, m[0], m[1], m[2], m[3], m[4], m[5]);
        }

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
        private static long GetFastJpegQuality() =>
            ReadConfiguredQuality(static c => c.FastQuality, 75);

        /// <summary>
        /// 方向为镜像类（2/3/4）而不得不重编码时使用的 JPEG 压缩质量，
        /// 取自 config.lua 的 Config.RotatedJpegQuality。取高值以尽量贴近"不牺牲画质"的语义。
        /// </summary>
        private static long GetRotatedJpegQuality() =>
            ReadConfiguredQuality(static c => c.RotatedJpegQuality, DefaultRotatedJpegQuality);

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
        /// 非 --fast 模式下的三条路径：
        /// 1. 无 EXIF 方向，或方向为 1 → 原始字节直通，方向留到排版期（零重编码）；
        /// 2. 方向为 5~8（转置类）→ 原始字节直通，方向用排版期的仿射矩阵应用（零重编码）；
        /// 3. 方向为 2~4（镜像/180°）→ 矩阵无法表达，只能解码后烘焙进像素再编码。
        ///
        /// --fast 模式下一律解码并按 FastQuality 重编码为 JPEG，以最大化减小体积。
        /// </summary>
        private static PageImage LoadPageImage(string imagePath, bool fastFlag) {
            string extension = System.IO.Path.GetExtension(imagePath);
            byte[] raw = File.ReadAllBytes(imagePath);

            // TIFF：SkiaSharp 解不了，交给 iText 直通（--fast 下同样不重编码）；
            // EXIF 方向直接从 TIFF 文件头解析，排版期由仿射矩阵应用。
            if (TiffExtensions.Contains(extension)) {
                return new PageImage {
                    Image = ImageDataFactory.Create(raw),
                    Orientation = JpegExif.GetTiffOrientation(raw)
                };
            }

            bool isJpeg = ExifOrientationExtensions.Contains(extension);

            // --- 开启 fastFlag：常规格式均压缩为指定质量的 JPEG ---
            if (fastFlag) {
                using var fastBitmap = DecodeBitmap(raw)
                    ?? throw new InvalidOperationException("Unsupported or corrupt image data.");
                // SKImage.FromEncodedData 已把方向烘焙进像素，这里无需再传 Orientation
                return new PageImage { Image = ImageDataFactory.Create(EncodeJpegOnWhite(fastBitmap, GetFastJpegQuality())) };
            }

            // --- 未开启 fastFlag：优先走原始字节无损直通 ---
            ushort orientation = isJpeg ? JpegExif.GetOrientation(raw) : (ushort)1;

            // 方向为 1（绝大多数素材）或 5~8（可用矩阵表达）→ 直接直通
            bool canPassThrough = PassthroughExtensions.Contains(extension)
                                  && !RequiresPixelBake(orientation);
            if (canPassThrough) {
                return new PageImage {
                    Image = ImageDataFactory.Create(raw),
                    Orientation = orientation
                };
            }

            // 剩余情况必须解码：方向为 2~4 的 JPEG，以及 iText 不认的 WebP
            using var bitmap = DecodeBitmap(raw)
                ?? throw new InvalidOperationException("Unsupported or corrupt image data.");

            if (isJpeg) {
                // 方向 2~4：SKImage 已按方向重排过像素，直接用较高质量重编码
                return new PageImage { Image = ImageDataFactory.Create(EncodeJpegOnWhite(bitmap, GetRotatedJpegQuality())) };
            }

            // WebP 等：无损 PNG 输出
            return new PageImage { Image = ImageDataFactory.Create(EncodePng(bitmap)) };
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

        /// <summary>
        /// 把一张图片排成一页。图片通过 PdfCanvas 以仿射矩阵直接放置：
        /// 未开启 --fast 时原始字节（JPEG 等）无损直通，EXIF 方向也只在此处应用一次，
        /// 无需重新编码。
        /// </summary>
        private static void AddPage(PdfDocument pdfDoc, PageImage pageImage) {
            // 注意：配置项声明为 Lua 可构造的基类型 Rectangle，需要转成 PageSize 使用。
            // 置 nil（默认）表示页尺寸跟随图片本身 —— 两版 config.lua 用的 iPageSize.NoResize
            // 实际并不存在，求值即为 nil，正是这个分支。
            Rectangle? pageSizeToSave = CSGlobal.luaConfig?.PageSizeToSave;

            float imageWidth = pageImage.Width;
            float imageHeight = pageImage.Height;

            float drawWidth = imageWidth;
            float drawHeight = imageHeight;
            float x = 0f;
            float y = 0f;
            PageSize pageSize;

            if (pageSizeToSave != null) {
                pageSize = new PageSize(pageSizeToSave.GetWidth(), pageSizeToSave.GetHeight());
                float scale = Math.Min(pageSize.GetWidth() / imageWidth, pageSize.GetHeight() / imageHeight);
                drawWidth = imageWidth * scale;
                drawHeight = imageHeight * scale;
                x = (pageSize.GetWidth() - drawWidth) / 2;
                y = (pageSize.GetHeight() - drawHeight) / 2;
            }
            else {
                pageSize = new PageSize(imageWidth, imageHeight);
            }

            var canvas = new PdfCanvas(pdfDoc.AddNewPage(pageSize));
            DrawImage(canvas, pageImage, x, y, drawWidth, drawHeight);
        }

        /// <summary>
        /// 把两张竖图并排排入同一页（小说模式的双页）。
        /// 不再把两张图拼成一张位图：拼图会额外占用一整幅画布的 Skia 内存，
        /// 且必然经历一次重新编码（画质损失 + 体积膨胀）。这里两张图各自按自身编码放置。
        /// </summary>
        private static void AddDuplexPage(PdfDocument pdfDoc, PageImage left, PageImage right) {
            Rectangle? pageSizeToSave = CSGlobal.luaConfig?.PageSizeToSave;

            float leftWidth = left.Width;
            float leftHeight = left.Height;
            float rightWidth = right.Width;
            float rightHeight = right.Height;
            float contentWidth = leftWidth + DuplexPageGap + rightWidth;
            float contentHeight = Math.Max(leftHeight, rightHeight);

            PageSize pageSize;
            float scale = 1f;
            float offsetX = 0f;
            float offsetY = 0f;

            if (pageSizeToSave != null) {
                pageSize = new PageSize(pageSizeToSave.GetWidth(), pageSizeToSave.GetHeight());
                scale = Math.Min(pageSize.GetWidth() / contentWidth, pageSize.GetHeight() / contentHeight);
                offsetX = (pageSize.GetWidth() - contentWidth * scale) / 2;
                offsetY = (pageSize.GetHeight() - contentHeight * scale) / 2;
            }
            else {
                pageSize = new PageSize(contentWidth, contentHeight);
            }

            var canvas = new PdfCanvas(pdfDoc.AddNewPage(pageSize));

            // 两张图顶部对齐，与旧版拼接位图时的画法保持一致
            DrawImage(canvas, left,
                offsetX,
                offsetY + (contentHeight - leftHeight) * scale,
                leftWidth * scale,
                leftHeight * scale);
            DrawImage(canvas, right,
                offsetX + (leftWidth + DuplexPageGap) * scale,
                offsetY + (contentHeight - rightHeight) * scale,
                rightWidth * scale,
                rightHeight * scale);
        }

        /// <summary>
        /// 输出文件的统一写法：先写同目录下的临时文件，全部成功后再替换目标文件。好处：
        /// - 中途失败（磁盘满、编码异常等）不会在目标路径留下半成品 PDF，也不会破坏上一次生成的结果；
        /// - "无法写入"（被别的程序占用、只读、无权限）会变成一行可读的上报，
        ///   而不是让进程直接崩掉、用户只看到一句无意义的异常信息。
        /// </summary>
        /// <param name="pathToSave">最终输出路径</param>
        /// <param name="write">真正写文件的回调（内部需自行关闭 PdfDocument 以刷新数据）</param>
        private static bool WriteToFile(string pathToSave, Action<FileStream> write) {
            string tempPath = pathToSave + TempFileSuffix;
            FileStream fs;
            try {
                fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write);
            }
            catch (Exception ex) {
                CommonUtils.ReportFailure($"Failed to create the output file '{pathToSave}': {ex.GetType().Name}: {ex.Message}");
                return false;
            }
            bool committed = false;
            try {
                write(fs);
                CommitOutputFile(tempPath, pathToSave);
                committed = true;
                return true;
            }
            catch (Exception ex) {
                CommonUtils.ReportFailure($"Failed to write the output file '{pathToSave}': {ex.GetType().Name}: {ex.Message}");
                return false;
            }
            finally {
                // iText 的 PdfDocument/PdfWriter 关闭时会一并关闭流，这里重复 Dispose 无副作用
                fs.Dispose();
                if (!committed) {
                    DiscardOutputFile(tempPath);
                }
            }
        }

        /// <summary>
        /// 用临时文件替换最终输出。目标已存在时优先用 File.Replace（原子替换），
        /// 个别文件系统/网络共享不支持时退化为带 overwrite 的 File.Move。
        /// </summary>
        private static void CommitOutputFile(string tempPath, string finalPath) {
            if (File.Exists(finalPath)) {
                try {
                    File.Replace(tempPath, finalPath, null);
                    return;
                }
                catch (IOException) {
                    // 交给下面的 File.Move(overwrite: true) 兜底（例如部分网络共享不支持 Replace）
                }
                catch (PlatformNotSupportedException) {
                }
            }
            File.Move(tempPath, finalPath, overwrite: true);
        }

        /// <summary>
        /// 删除失败时残留的临时文件（删除本身再失败也不抛异常，以免盖掉真正要上报的错误）
        /// </summary>
        private static void DiscardOutputFile(string tempPath) {
            try {
                if (File.Exists(tempPath)) {
                    File.Delete(tempPath);
                }
            }
            catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine($"[ImgsToPDFCore] Failed to delete temp file '{tempPath}': {ex.Message}");
            }
        }

        /// <summary>
        /// 按配置的排序规则列出目录下的图片（目录不存在或读取失败时返回空列表）。
        /// OrderBy 会立即物化，因此连枚举期间抛出的异常也一并被兜住。
        /// </summary>
        private static List<string> CollectSortedImagePaths(string directoryPath) {
            var result = new List<string>();
            if (string.IsNullOrEmpty(directoryPath) || !Directory.Exists(directoryPath)) {
                return result;
            }
            try {
                result.AddRange(Directory.EnumerateFiles(directoryPath)
                    .Where(p => SupportedImageExtensions.Contains(System.IO.Path.GetExtension(p)))
                    .OrderBy(p => p, new StringLenComparer()));
            }
            catch (Exception ex) {
                Console.Error.WriteLine($"[ImgsToPDFCore] Failed to enumerate '{directoryPath}': {ex.GetType().Name}: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// 将指定文件夹下的图片合并为PDF文件
        /// </summary>
        /// <param name="directoryPath">文件夹路径</param>
        /// <param name="layout">合并方式</param>
        /// <param name="fastFlag">是否以图片质量换取生成速度</param>
        public static void ImagesToPDF(string directoryPath, Layout layout = Layout.Single, bool fastFlag = false) {
            if (string.IsNullOrEmpty(directoryPath) || !Directory.Exists(directoryPath)) {
                return;   // 不存在文件夹则直接结束执行（保持原行为：不生成任何文件）
            }
            // 零页时补一页空白页，保持原有行为
            WriteDocument(CSGlobal.luaConfig?.PathToSave(), layout, fastFlag, [CollectSortedImagePaths(directoryPath)], blankPageWhenEmpty: true);
        }

        /// <summary>
        /// 把多个文件夹（按给定顺序）里的图片依次写入同一个 PDF，返回实际写入的页数。
        /// 每个文件夹内部单独排序，双页模式下也不跨文件夹配对，因此章节之间不会串页。
        /// 返回 0 表示这些文件夹里一张图片都没有——此时不会创建输出文件，
        /// 由调用方（config.lua）负责上报失败。压缩包路径就靠这个返回值判断"整包无图片"。
        /// </summary>
        public static int ImagesToPDFFromFolders(string[] directoryPaths, Layout layout = Layout.Single, bool fastFlag = false) {
            if (directoryPaths == null || directoryPaths.Length == 0) {
                CommonUtils.ReportFailure("No source folder was provided; no PDF was generated.");
                return 0;
            }
            var folders = new List<List<string>>();
            foreach (var directoryPath in directoryPaths) {
                var images = CollectSortedImagePaths(directoryPath);
                if (images.Count > 0) {
                    folders.Add(images);
                }
            }
            if (folders.Count == 0) {
                return 0;
            }
            return WriteDocument(CSGlobal.luaConfig?.PathToSave(), layout, fastFlag, folders, blankPageWhenEmpty: false);
        }

        /// <summary>
        /// 写出一份 PDF 并返回写入的页数（失败返回 0）。
        /// folders 的每个元素是一个文件夹内已排好序的图片列表，按顺序逐页写入。
        /// </summary>
        private static int WriteDocument(string? pathToSave, Layout layout, bool fastFlag, List<List<string>> folders, bool blankPageWhenEmpty) {
            if (string.IsNullOrEmpty(pathToSave)) {
                CommonUtils.ReportFailure("PathToSave returned null or empty; no PDF was generated.");
                return 0;
            }
            int pages = 0;
            bool ok = WriteToFile(pathToSave, fs => {
                // 全压缩（对象流 + 交叉引用流），对应原 .NET Framework 版的 writer.SetFullCompression()
                var writerProperties = new WriterProperties().SetFullCompressionMode(true);
                using var writer = new PdfWriter(fs, writerProperties);
                using var pdfDoc = new PdfDocument(writer);
                foreach (var images in folders) {
                    WritePages(pdfDoc, images, layout, fastFlag);
                }

                // 如果零页，添加一页空页
                if (pdfDoc.GetNumberOfPages() == 0 && blankPageWhenEmpty) {
                    pdfDoc.AddNewPage();
                }
                pages = pdfDoc.GetNumberOfPages();
            });
            return ok ? pages : 0;
        }

        /// <summary>
        /// 逐页写入。单页模式按文件顺序逐张直通/转码；
        /// 双页模式按"横向图单独成页、连续两张纵向图左右拼合"的规则配对。
        /// </summary>
        private static void WritePages(PdfDocument pdfDoc, IEnumerable<string> imagePaths,
                                       Layout layout, bool fastFlag) {
            if (layout != Layout.DuplexLeftToRight && layout != Layout.DuplexRightToLeft) {
                foreach (var imagePath in imagePaths) {
                    try {
                        AddPage(pdfDoc, LoadPageImage(imagePath, fastFlag));
                    }
                    catch (Exception ex) {
                        ReportImageFailure(imagePath, ex);
                    }
                }
                return;
            }

            using var enumerator = imagePaths.GetEnumerator();
            while (enumerator.MoveNext()) {
                PageImage bm1;
                try {
                    bm1 = LoadPageImage(enumerator.Current, fastFlag);
                }
                catch (Exception ex) {
                    ReportImageFailure(enumerator.Current, ex);
                    continue;
                }

                // 横向（长插图页）单独成页
                if (bm1.Width >= bm1.Height) {
                    AddPage(pdfDoc, bm1);
                    continue;
                }
                if (!enumerator.MoveNext()) {
                    AddPage(pdfDoc, bm1);
                    break;
                }

                PageImage bm2;
                try {
                    bm2 = LoadPageImage(enumerator.Current, fastFlag);
                }
                catch (Exception ex) {
                    ReportImageFailure(enumerator.Current, ex);
                    AddPage(pdfDoc, bm1);
                    continue;
                }

                if (bm1.Height >= bm1.Width && bm2.Height >= bm2.Width) {
                    // 两张都是纵向图：并排排入同一页，各自保持原有编码
                    PageImage picAtLeft = layout == Layout.DuplexLeftToRight ? bm1 : bm2;
                    PageImage picAtRight = layout == Layout.DuplexLeftToRight ? bm2 : bm1;
                    AddDuplexPage(pdfDoc, picAtLeft, picAtRight);
                }
                else {
                    AddPage(pdfDoc, bm1);
                    AddPage(pdfDoc, bm2);
                }
            }
        }

        /// <summary>
        /// 单张图片处理失败：写 stderr（GUI 据此提示用户）并继续处理其余图片，
        /// 避免一张坏图让整本画集都出不来
        /// </summary>
        private static void ReportImageFailure(string imagePath, Exception ex) {
            Console.Error.WriteLine($"[ImgsToPDFCore] Failed to load image '{imagePath}': {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null) {
                Console.Error.WriteLine($"  Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            }
        }

        /// <summary>
        /// 合并PDF文件
        /// </summary>
        /// <param name="inFiles">待合并文件列表</param>
        /// <param name="outFile">合并生成的文件名称</param>
        public static void PdfMerge(List<string> inFiles, string outFile) {
            var comparer = new StringLenComparer();
            inFiles.Sort(comparer);

            WriteToFile(outFile, stream => {
                using var writer = new PdfWriter(stream);
                using var outputPdf = new PdfDocument(writer);
                foreach (var file in inFiles) {
                    if (!File.Exists(file)) continue;

                    using var inputPdf = new PdfDocument(new PdfReader(file));
                    inputPdf.CopyPagesTo(1, inputPdf.GetNumberOfPages(), outputPdf);
                }
            });
        }

        // 带层级书签的 PDF 合并
        public static void PdfMergeWithHierarchicalOutlines(List<string> inFiles, string outFile) {
            var comparer = new StringLenComparer();
            inFiles.Sort(comparer);

            var folderOutlineCache = new Dictionary<string, PdfOutline>();

            WriteToFile(outFile, stream => {
                using var writer = new PdfWriter(stream);
                using var outputPdf = new PdfDocument(writer);
                int currentPage = 1;

                foreach (var file in inFiles) {
                    if (!File.Exists(file)) continue;

                    using var inputPdf = new PdfDocument(new PdfReader(file));
                    int pageCount = inputPdf.GetNumberOfPages();

                    // 先复制页面到输出PDF
                    inputPdf.CopyPagesTo(1, pageCount, outputPdf);

                    // 现在可以安全地创建书签，因为页面已经存在
                    string folderName = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(file)) ?? string.Empty;
                    string fileName = System.IO.Path.GetFileNameWithoutExtension(file) ?? string.Empty;

                    PdfOutline root = outputPdf.GetOutlines(false);
                    PdfOutline parentNode = root;

                    if (!string.IsNullOrEmpty(folderName)) {
                        if (!folderOutlineCache.TryGetValue(folderName, out PdfOutline? folderNameOutline)) {
                            var action = PdfAction.CreateGoTo(
                                PdfExplicitDestination.CreateFitH(
                                    outputPdf.GetPage(currentPage), 0
                                )
                            );
                            var folderNode = root.AddOutline(folderName);
                            folderNode.AddAction(action);
                            folderNameOutline = folderNode;
                            folderOutlineCache[folderName] = folderNameOutline;
                        }
                        parentNode = folderNameOutline;
                    }

                    // 文件名与所在文件夹同名时不再重复建条目；仅大小写不同视为同一个名字
                    if (!string.Equals(fileName, folderName, StringComparison.OrdinalIgnoreCase)) {
                        var action = PdfAction.CreateGoTo(
                            PdfExplicitDestination.CreateFitH(
                                outputPdf.GetPage(currentPage), 0
                            )
                        );
                        var fileNode = parentNode.AddOutline(fileName);
                        fileNode.AddAction(action);
                    }

                    currentPage += pageCount;
                }
            });
        }

        /// <summary>
        /// 规范化目录路径：统一去掉末尾分隔符，便于作为字典的键（保留 "C:\" 或 "/" 这种根目录）。
        /// </summary>
        private static string NormalizeDir(string? path) {
            if (string.IsNullOrEmpty(path)) {
                return string.Empty;
            }
            string full;
            try {
                full = System.IO.Path.GetFullPath(path);
            }
            catch (Exception) {
                full = path;
            }
            string trimmed = full.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            if (trimmed.Length == 0) {
                return System.IO.Path.DirectorySeparatorChar.ToString();
            }
            return trimmed.EndsWith(":", StringComparison.Ordinal) ? trimmed + System.IO.Path.DirectorySeparatorChar : trimmed;
        }

        /// <summary>
        /// 列出文件所在文件夹到 rootPath 之间（不含 rootPath 本身）的所有上级文件夹，从上层到下层。
        /// 文件不在 rootPath 之下时返回空列表（不为它建文件夹书签）。
        /// </summary>
        private static List<string> AncestorFolders(string rootPath, string filePath) {
            var folders = new List<string>();
            string root = NormalizeDir(rootPath);
            string dir = NormalizeDir(System.IO.Path.GetDirectoryName(filePath));
            while (!string.IsNullOrEmpty(dir) && !string.Equals(dir, root, PathComparison)) {
                folders.Add(dir);
                string parent = NormalizeDir(System.IO.Path.GetDirectoryName(dir));
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, dir, PathComparison)) {
                    return [];   // 一直到根目录都没遇到 root
                }
                dir = parent;
            }
            folders.Reverse();
            return folders;
        }

        /// <summary>
        /// 带多级书签地合并 PDF。
        /// 书签页码先由预扫描统一算好，文件夹书签锚定到"该文件夹自身 PDF 的首页"，
        /// 该文件夹没有同名 PDF 时才退化为"它子树里第一个文件的首页"。
        /// （旧实现用"第一次遇到该文件夹时的当前页"作锚点，排序把无数字目录排到前面时，
        /// 父文件夹书签会指到子文件夹的起始页，点进去完全不是那个章节。）
        /// </summary>
        public static void PdfMergeWithDeepOutlines(List<string> inFiles, string outFile, string rootPath) {
            inFiles.Sort(new StringLenComparer());
            string root = NormalizeDir(rootPath);

            // 1. 预扫描：算出每个源文件在合并结果中的起始页；读不出来的 PDF 直接跳过（单个坏文件不该毁掉整次合并）
            var plan = new List<(string File, int StartPage, int PageCount)>();
            int cursor = 1;
            foreach (var file in inFiles) {
                if (!File.Exists(file)) continue;
                try {
                    using var probe = new PdfDocument(new PdfReader(file));
                    int pageCount = probe.GetNumberOfPages();
                    if (pageCount <= 0) {
                        Console.Error.WriteLine($"[ImgsToPDFCore] Skipped empty PDF '{file}' while merging.");
                        continue;
                    }
                    plan.Add((file, cursor, pageCount));
                    cursor += pageCount;
                }
                catch (Exception ex) {
                    Console.Error.WriteLine($"[ImgsToPDFCore] Failed to read PDF '{file}': {ex.GetType().Name}: {ex.Message}");
                }
            }
            if (plan.Count == 0) {
                CommonUtils.ReportFailure($"No readable PDF was found to merge under '{rootPath}'; no merged PDF was generated.");
                return;
            }

            // 2. 每个文件夹的书签锚点页
            var folderAnchor = new Dictionary<string, int>(PathComparer);
            foreach (var (file, startPage, _) in plan) {
                foreach (var folder in AncestorFolders(root, file)) {
                    if (!folderAnchor.ContainsKey(folder)) {
                        folderAnchor[folder] = startPage;   // 先到先得 = 该子树里第一个文件的起始页
                    }
                }
                // 该文件就是所在文件夹的同名 PDF：以自身起始页为准，覆盖上面的子树推断值
                string dir = NormalizeDir(System.IO.Path.GetDirectoryName(file));
                if (!string.IsNullOrEmpty(dir) &&
                    string.Equals(System.IO.Path.GetFileNameWithoutExtension(file), System.IO.Path.GetFileName(dir), StringComparison.OrdinalIgnoreCase)) {
                    folderAnchor[dir] = startPage;
                }
            }

            // 3. 写文件：先按顺序拷贝页面（iText 9 的 GetPage 要求页面已存在），再按页码顺序构建书签树
            WriteToFile(outFile, stream => {
                using var writer = new PdfWriter(stream);
                using var outputPdf = new PdfDocument(writer);

                // 拷贝页面
                foreach (var (file, _, pageCount) in plan) {
                    PdfDocument inputPdf;
                    try {
                        inputPdf = new PdfDocument(new PdfReader(file));
                    }
                    catch (Exception ex) {
                        // 预扫描之后文件才出问题：页码已经排好，继续写会得到错位的书签，直接中止并丢弃临时文件
                        throw new InvalidOperationException($"Failed to reopen '{file}' while merging: {ex.Message}", ex);
                    }
                    using (inputPdf) {
                        inputPdf.CopyPagesTo(1, pageCount, outputPdf);
                    }
                }

                // 按"所在文件夹"分组，便于按页面顺序递归建树
                var filesByDir = plan
                    .GroupBy(p => NormalizeDir(System.IO.Path.GetDirectoryName(p.File)), PathComparer)
                    .ToDictionary(g => g.Key, g => g.OrderBy(p => p.StartPage).ToList(), PathComparer);
                var subFoldersByDir = folderAnchor.Keys
                    .Where(f => !string.Equals(f, root, PathComparison))
                    .GroupBy(f => NormalizeDir(System.IO.Path.GetDirectoryName(f)), PathComparer)
                    .ToDictionary(g => g.Key, g => g.ToList(), PathComparer);

                PdfOutline CreateOutline(PdfOutline parent, int page, string title) {
                    var action = PdfAction.CreateGoTo(
                        PdfExplicitDestination.CreateFitH(
                            outputPdf.GetPage(Math.Max(1, page)), 0
                        )
                    );
                    var node = parent.AddOutline(title);
                    node.AddAction(action);
                    return node;
                }

                // 同一层内按锚点页排序建节点，书签面板的顺序就与正文顺序一致；
                // 递归进入子文件夹只会往子节点里加内容，不会打乱本层的顺序
                void BuildTree(PdfOutline parentNode, string dir) {
                    bool isRootLevel = string.Equals(dir, root, PathComparison);
                    var entries = new List<(int Page, bool IsFolder, string Path)>();
                    if (subFoldersByDir.TryGetValue(dir, out var subFolders)) {
                        foreach (var folder in subFolders) {
                            entries.Add((folderAnchor[folder], true, folder));
                        }
                    }
                    if (filesByDir.TryGetValue(dir, out var files)) {
                        foreach (var (file, startPage, _) in files) {
                            // 文件名与所在文件夹同名时不再单独建文件书签：该文件夹书签已指向本文件首页
                            if (!isRootLevel &&
                                string.Equals(System.IO.Path.GetFileNameWithoutExtension(file), System.IO.Path.GetFileName(dir), StringComparison.OrdinalIgnoreCase)) {
                                continue;
                            }
                            entries.Add((startPage, false, file));
                        }
                    }
                    foreach (var entry in entries.OrderBy(e => e.Page)) {
                        if (entry.IsFolder) {
                            BuildTree(CreateOutline(parentNode, entry.Page, System.IO.Path.GetFileName(entry.Path)), entry.Path);
                        }
                        else {
                            CreateOutline(parentNode, entry.Page, System.IO.Path.GetFileNameWithoutExtension(entry.Path));
                        }
                    }
                }

                BuildTree(outputPdf.GetOutlines(false), root);
            });
        }

        /// <summary>
        /// 给文件名排序的方法，不使用默认的排序方法，在lua里重写
        /// </summary>
        class StringLenComparer : IComparer<string> {
            int IComparer<string>.Compare(string? x, string? y) {
                return CSGlobal.luaConfig!.FilePathComparer(x ?? string.Empty, y ?? string.Empty);
            }
        }
    }
}
