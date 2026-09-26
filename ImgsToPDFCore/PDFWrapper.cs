using iText.IO.Image;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Action;
using iText.Kernel.Pdf.Navigation;
using iText.Layout;
using iText.Kernel.Geom;
using SkiaSharp;

namespace ImgsToPDFCore {
    public enum Layout {
        Single,
        DuplexLeftToRight,
        DuplexRightToLeft
    }

    internal class PDFWrapper {
        private static readonly string[] SupportedImageExtensions = [
            ".png", ".apng", ".jpg", ".jpeg", ".jfif", ".pjpeg",
            ".pjp", ".bmp", ".tif", ".tiff", ".gif", ".webp"
        ];

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
        /// 可能带 EXIF 方向标记的格式。只有这些格式需要额外探测是否必须重编码，
        /// 其余格式（PNG/BMP/GIF/WebP）的方向由像素本身决定。
        /// </summary>
        private static readonly HashSet<string> ExifOrientationExtensions = new(StringComparer.OrdinalIgnoreCase) {
            ".jpg", ".jpeg", ".jfif", ".pjpeg", ".pjp"
        };

        /// <summary>
        /// 非 --fast 模式下，为纠正 EXIF 方向而不得不重编码的 JPEG 所使用的质量。
        /// 取高值以尽量贴近"不牺牲画质"的语义。
        /// </summary>
        private const int RotatedJpegQuality = 90;

        /// <summary>
        /// 用 SKCodec 只读元数据判断是否需要按 EXIF 方向旋转，避免为了判断而整图解码。
        /// </summary>
        private static bool NeedsExifRotation(byte[] raw, string extension) {
            if (!ExifOrientationExtensions.Contains(extension)) {
                return false;
            }
            try {
                using var stream = new MemoryStream(raw, writable: false);
                using var codec = SKCodec.Create(stream);
                // 探测不出方向时按"需要重编码"处理：解码阶段会给出明确错误，
                // 好过悄悄输出一张方向错误的页面
                return codec == null || codec.EncodedOrigin != SKEncodedOrigin.TopLeft;
            }
            catch {
                return true;
            }
        }

        /// <summary>
        /// 用 SkiaSharp 解码。SKImage.FromEncodedData 会自动应用 EXIF 方向
        /// （注意 SKBitmap.Decode 不会，不要换成那个重载）。
        /// </summary>
        private static SKBitmap? DecodeBitmap(byte[] raw) {
            using var image = SKImage.FromEncodedData(raw);
            return image == null ? null : SKBitmap.FromImage(image);
        }

        // 开启 --fast 时使用的 JPEG 压缩质量，取自 config.lua 的 Config.FastQuality；
        // 取值非法或读取异常时回落到 75
        private static long GetFastJpegQuality() {
            try {
                int q = CSGlobal.luaConfig != null ? CSGlobal.luaConfig.FastQuality : 0;
                if (q >= 1 && q <= 100) {
                    return q;
                }
            }
            catch {
                // Lua 配置读取异常时安全降级
            }
            return 75L;
        }

        /// <summary>
        /// 载入图片并生成可直接写入 PDF 的 ImageData。
        /// 非 --fast 且无需纠正方向时走原始字节直通；其余情况才解码重编码。
        /// </summary>
        private static ImageData LoadImageData(string imagePath, bool fastFlag) {
            string extension = System.IO.Path.GetExtension(imagePath);
            byte[] raw = File.ReadAllBytes(imagePath);

            // TIFF：SkiaSharp 解不了，交给 iText 直通（--fast 下同样不重编码）
            if (TiffExtensions.Contains(extension)) {
                return ImageDataFactory.Create(raw);
            }

            bool canPassThrough = PassthroughExtensions.Contains(extension)
                                  && !NeedsExifRotation(raw, extension);

            if (!fastFlag && canPassThrough) {
                return ImageDataFactory.Create(raw);
            }

            using var bitmap = DecodeBitmap(raw)
                ?? throw new InvalidOperationException("Unsupported or corrupt image data.");

            if (fastFlag) {
                return ImageDataFactory.Create(EncodeJpegOnWhite(bitmap, GetFastJpegQuality()));
            }

            // 非 --fast 但必须按 EXIF 方向重排像素：JPEG 继续输出 JPEG（避免体积暴涨），
            // 其余格式用 PNG 无损输出
            return ExifOrientationExtensions.Contains(extension)
                ? ImageDataFactory.Create(EncodeJpegOnWhite(bitmap, RotatedJpegQuality))
                : ImageDataFactory.Create(EncodePng(bitmap));
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

        /// <summary>双页拼版等已经在内存中合成过的位图，只能重新编码，没有直通的可能</summary>
        private static ImageData EncodeBitmap(SKBitmap bitmap, bool fastFlag) {
            return fastFlag
                ? ImageDataFactory.Create(EncodeJpegOnWhite(bitmap, GetFastJpegQuality()))
                : ImageDataFactory.Create(EncodePng(bitmap));
        }

        /// <summary>
        /// 供双页拼版使用的解码入口：这里必须拿到像素才能拼接
        /// </summary>
        private static SKBitmap LoadBitmapForCombine(string imagePath) {
            using var image = SKImage.FromEncodedData(imagePath);
            return image == null
                ? throw new InvalidOperationException("Unsupported or corrupt image.")
                : SKBitmap.FromImage(image);
        }

        // 合并两张图片
        private static SKBitmap CombineBitmap(SKBitmap bm1, SKBitmap bm2, int margin) {
            var width = bm1.Width + bm2.Width + margin;
            var height = Math.Max(bm1.Height, bm2.Height);

            var surface = SKSurface.Create(new SKImageInfo(width, height));
            var canvas = surface.Canvas;

            // 白色背景
            canvas.Clear(SKColors.White);

            // 图片不需要缩放，因此使用默认采样即可
            var sampling = new SKSamplingOptions(SKFilterMode.Linear);

            // 绘制第一张图
            canvas.DrawBitmap(bm1, 0, 0, sampling);

            // 绘制第二张图
            canvas.DrawBitmap(bm2, bm1.Width + margin, 0, sampling);

            var result = SKBitmap.FromImage(surface.Snapshot());

            bm1.Dispose();
            bm2.Dispose();
            surface.Dispose();

            return result;
        }

        // 添加页面到文档
        private static void AddPage(Document document, PdfDocument pdfDoc, ImageData imageData) {
            var pageSizeToSave = CSGlobal.luaConfig!.PageSizeToSave;

            PageSize pageSize = pageSizeToSave != null
                ? new PageSize((float)pageSizeToSave.GetWidth(), (float)pageSizeToSave.GetHeight())
                : new PageSize(imageData.GetWidth(), imageData.GetHeight());

            document.SetMargins(0, 0, 0, 0);

            var image = new iText.Layout.Element.Image(imageData);

            if (pageSizeToSave != null) {
                image.ScaleToFit(pageSize.GetWidth(), pageSize.GetHeight());
                image.SetFixedPosition(
                    (pageSize.GetWidth() - image.GetImageScaledWidth()) / 2,
                    (pageSize.GetHeight() - image.GetImageScaledHeight()) / 2
                );
            }

            pdfDoc.AddNewPage(pageSize);
            document.Add(image);
        }

        public static void ImagesToPDF(string directoryPath, Layout layout = Layout.Single, bool fastFlag = false) {
            if (!Directory.Exists(directoryPath)) return;   // 不存在文件夹则直接结束执行

            var imagePaths = Directory.EnumerateFiles(directoryPath)
                .Where(p => SupportedImageExtensions.Any(e => System.IO.Path.GetExtension(p)?.ToLower() == e))
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
                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                using (var writer = new PdfWriter(fs))
                using (var pdfDoc = new PdfDocument(writer)) {
                    pdfDoc.SetFlushUnusedObjects(true);
                    var document = new Document(pdfDoc);

                    try {
                        WritePages(document, pdfDoc, imagePaths, layout, fastFlag);

                        // 如果零页，添加一页空页
                        if (pdfDoc.GetNumberOfPages() == 0) {
                            pdfDoc.AddNewPage();
                        }
                    }
                    finally {
                        document.Close();
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
        private static void WritePages(Document document, PdfDocument pdfDoc, IEnumerable<string> imagePaths,
                                       Layout layout, bool fastFlag) {
            if (layout != Layout.DuplexLeftToRight && layout != Layout.DuplexRightToLeft) {
                foreach (var imagePath in imagePaths) {
                    try {
                        AddPage(document, pdfDoc, LoadImageData(imagePath, fastFlag));
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
                        AddPage(document, pdfDoc, LoadImageData(enumerator.Current, fastFlag));
                    }
                    catch (Exception ex) {
                        ReportImageFailure(enumerator.Current, ex);
                    }
                    continue;
                }

                SKBitmap bm1;
                try {
                    bm1 = LoadBitmapForCombine(enumerator.Current);
                }
                catch (Exception ex) {
                    ReportImageFailure(enumerator.Current, ex);
                    continue;
                }

                // 横向（长插图页）单独成页
                if (bm1.Width >= bm1.Height) {
                    AddPage(document, pdfDoc, EncodeBitmap(bm1, fastFlag));
                    continue;
                }
                if (!enumerator.MoveNext()) {
                    AddPage(document, pdfDoc, EncodeBitmap(bm1, fastFlag));
                    break;
                }

                SKBitmap bm2;
                try {
                    bm2 = LoadBitmapForCombine(enumerator.Current);
                }
                catch (Exception ex) {
                    ReportImageFailure(enumerator.Current, ex);
                    AddPage(document, pdfDoc, EncodeBitmap(bm1, fastFlag));
                    continue;
                }

                if (bm1.Height >= bm1.Width && bm2.Height >= bm2.Width) {
                    // 两张都是纵向图：拼成一页
                    SKBitmap picAtLeft = layout == Layout.DuplexLeftToRight ? bm1 : bm2;
                    SKBitmap picAtRight = layout == Layout.DuplexLeftToRight ? bm2 : bm1;
                    using var combined = CombineBitmap(picAtLeft, picAtRight, 10);
                    AddPage(document, pdfDoc, EncodeBitmap(combined, fastFlag));
                }
                else {
                    AddPage(document, pdfDoc, EncodeBitmap(bm1, fastFlag));
                    AddPage(document, pdfDoc, EncodeBitmap(bm2, fastFlag));
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

                if (fileName != folderName) {
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

                // 只有文件名与父文件夹名不同时，才创建文件书签
                if (fileName != parentFolderName) {
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
