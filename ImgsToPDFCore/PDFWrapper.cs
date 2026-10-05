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

            // TIFF：SkiaSharp 解不了，交给 iText 直通（--fast 下同样不重编码）
            if (TiffExtensions.Contains(extension)) {
                return new PageImage { Image = ImageDataFactory.Create(raw) };
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
        /// 将指定文件夹下的图片合并为PDF文件
        /// </summary>
        /// <param name="directoryPath">文件夹路径</param>
        /// <param name="layout">合并方式</param>
        /// <param name="fastFlag">是否以图片质量换取生成速度</param>
        public static void ImagesToPDF(string directoryPath, Layout layout = Layout.Single, bool fastFlag = false) {
            if (!Directory.Exists(directoryPath)) return;   // 不存在文件夹则直接结束执行

            var imagePaths = Directory.EnumerateFiles(directoryPath)
                .Where(p => SupportedImageExtensions.Contains(System.IO.Path.GetExtension(p)))
                .OrderBy(p => p, new StringLenComparer());

            string? pathToSave = CSGlobal.luaConfig!.PathToSave();
            if (string.IsNullOrEmpty(pathToSave)) {
                throw new InvalidOperationException("PathToSave returned null or empty.");
            }

            // 直接流式写入临时文件，成功后原子替换：
            // 1) 不再把整本 PDF 攒在内存里（画集大时内存占用与图片总量同阶，
            //    而 GUI 会并发起多个 Core 进程）；
            // 2) 中途失败不会用半个文件覆盖掉上一次的正常产物。
            string tempPath = pathToSave + ".tmp";
            try {
                // 全压缩（对象流 + 交叉引用流），对应原 .NET Framework 版的 writer.SetFullCompression()
                var writerProperties = new WriterProperties().SetFullCompressionMode(true);
                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                using (var writer = new PdfWriter(fs, writerProperties))
                using (var pdfDoc = new PdfDocument(writer)) {
                    WritePages(pdfDoc, imagePaths, layout, fastFlag);

                    // 如果零页，添加一页空页
                    if (pdfDoc.GetNumberOfPages() == 0) {
                        pdfDoc.AddNewPage();
                    }
                }

                File.Move(tempPath, pathToSave, overwrite: true);
            }
            catch {
                // 失败时清掉临时文件，避免在用户目录里留下 .tmp 垃圾
                try {
                    if (File.Exists(tempPath)) {
                        File.Delete(tempPath);
                    }
                }
                catch {
                    // 清理失败不应掩盖原始异常
                }
                throw;
            }
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
                // TIFF 无法参与拼接：SkiaSharp 没有 TIFF 解码器，拿不到像素。
                // 这里退化为"单独成页"，而不是把整张图丢掉。
                if (TiffExtensions.Contains(System.IO.Path.GetExtension(enumerator.Current))) {
                    try {
                        AddPage(pdfDoc, LoadPageImage(enumerator.Current, fastFlag));
                    }
                    catch (Exception ex) {
                        ReportImageFailure(enumerator.Current, ex);
                    }
                    continue;
                }

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

            using var writer = new PdfWriter(outFile);
            using var outputPdf = new PdfDocument(writer);
            foreach (var file in inFiles) {
                if (!File.Exists(file)) continue;

                using var inputPdf = new PdfDocument(new PdfReader(file));
                inputPdf.CopyPagesTo(1, inputPdf.GetNumberOfPages(), outputPdf);
            }
        }

        // 带层级书签的 PDF 合并
        public static void PdfMergeWithHierarchicalOutlines(List<string> inFiles, string outFile) {
            var comparer = new StringLenComparer();
            inFiles.Sort(comparer);

            var folderOutlineCache = new Dictionary<string, PdfOutline>();

            using var writer = new PdfWriter(outFile);
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
        }

        // 深层级书签合并
        public static void PdfMergeWithDeepOutlines(List<string> inFiles, string outFile, string rootPath) {
            inFiles.Sort(new StringLenComparer());
            var outlineCache = new Dictionary<string, PdfOutline>();

            using var writer = new PdfWriter(outFile);
            using var outputPdf = new PdfDocument(writer);
            int currentPage = 1;

            // 第一遍：先合并所有页面并记录页码范围
            var pageRanges = new List<(string file, int startPage, int pageCount)>();

            foreach (var file in inFiles) {
                if (!File.Exists(file)) continue;

                using var inputPdf = new PdfDocument(new PdfReader(file));
                int pageCount = inputPdf.GetNumberOfPages();
                pageRanges.Add((file, currentPage, pageCount));
                inputPdf.CopyPagesTo(1, pageCount, outputPdf);
                currentPage += pageCount;
            }

            // 第二遍：添加书签（现在可以安全地引用页面）
            currentPage = 1;
            foreach (var (file, startPage, pageCount) in pageRanges) {
                string relativePath = System.IO.Path.GetRelativePath(rootPath, file);
                string[] pathParts = relativePath.Split([System.IO.Path.DirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

                PdfOutline root = outputPdf.GetOutlines(false);
                PdfOutline parent = root;
                string currentPathAccumulator = rootPath;

                // 创建文件夹层级书签
                for (int i = 0; i < pathParts.Length - 1; i++) {
                    string folderName = pathParts[i];
                    currentPathAccumulator = System.IO.Path.Combine(currentPathAccumulator, folderName);

                    if (!outlineCache.TryGetValue(currentPathAccumulator, out PdfOutline? currentPathAccumulatorOutline)) {
                        var action = PdfAction.CreateGoTo(
                            PdfExplicitDestination.CreateFitH(
                                outputPdf.GetPage(startPage), 0
                            )
                        );
                        var folderNode = parent.AddOutline(folderName);
                        folderNode.AddAction(action);
                        currentPathAccumulatorOutline = folderNode;
                        outlineCache[currentPathAccumulator] = currentPathAccumulatorOutline;
                    }
                    parent = currentPathAccumulatorOutline;
                }

                // 创建文件书签
                string fileName = System.IO.Path.GetFileNameWithoutExtension(file) ?? string.Empty;

                // 获取父文件夹名（pathParts 的最后一个文件夹层级）
                string? parentFolderName = pathParts.Length >= 2 ? pathParts[^2] : null;

                // 只有文件名与父文件夹名不同时，才创建文件书签；仅大小写不同视为同一个名字
                if (!string.Equals(fileName, parentFolderName, StringComparison.OrdinalIgnoreCase)) {
                    var fileAction = PdfAction.CreateGoTo(
                        PdfExplicitDestination.CreateFitH(
                            outputPdf.GetPage(startPage), 0
                        )
                    );
                    var fileNode = parent.AddOutline(fileName);
                    fileNode.AddAction(fileAction);
                }

                currentPage += pageCount;
            }
        }

        //private static string GetRelativePath(string rootPath, string fullPath) {
        //    if (!rootPath.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString())) {
        //        rootPath += System.IO.Path.DirectorySeparatorChar;
        //    }

        //    Uri rootUri = new(rootPath);
        //    Uri fullUri = new(fullPath);
        //    Uri relativeUri = rootUri.MakeRelativeUri(fullUri);

        //    return Uri.UnescapeDataString(relativeUri.ToString())
        //        .Replace('/', System.IO.Path.DirectorySeparatorChar);
        //}

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
