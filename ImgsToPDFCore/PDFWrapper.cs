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
        public static readonly string[] SupportedImageExtensions = [
            ".png", ".apng", ".jpg", ".jpeg", ".jfif", ".pjpeg",
            ".pjp", ".bmp", ".tif", ".tiff", ".gif", ".webp"
        ];

        private static SKBitmap? LoadImageFromFile(string path) {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
            // SKBitmap.Decode 可能返回 null，方法返回可空类型以避免 CS8600
            return SKBitmap.Decode(stream);
        }

        // 将 SKBitmap 转换为 iText ImageData
        private static ImageData GetImageData(SKBitmap bitmap, bool fastFlag) {
            using var image = SKImage.FromBitmap(bitmap);
            SKEncodedImageFormat format = fastFlag ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png;
            int quality = fastFlag ? 80 : 100;

            using var data = image.Encode(format, quality);
            return data == null
                ? throw new InvalidOperationException($"Failed to encode image to {format}.")
                : ImageDataFactory.Create(data.ToArray());
        }

        // 合并两张图片
        private static SKBitmap CombineBitmap(SKBitmap bm1, SKBitmap bm2, int margin) {
            var width = bm1.Width + bm2.Width + margin;
            var height = Math.Max(bm1.Height, bm2.Height);

            var surface = SKSurface.Create(new SKImageInfo(width, height));
            var canvas = surface.Canvas;

            // 白色背景
            canvas.Clear(SKColors.White);

            // 绘制第一张图
            canvas.DrawBitmap(bm1, 0, 0);

            // 绘制第二张图
            canvas.DrawBitmap(bm2, bm1.Width + margin, 0);

            var result = SKBitmap.FromImage(surface.Snapshot());

            bm1.Dispose();
            bm2.Dispose();
            surface.Dispose();

            return result;
        }

        // 添加页面到文档
        private static void AddPage(Document document, PdfDocument pdfDoc, SKBitmap bitmap, bool fastFlag) {
            PageSize pageSize;

            if (CSGlobal.luaConfig!.PageSizeToSave != null) {
                pageSize = new PageSize(
                    (float)CSGlobal.luaConfig.PageSizeToSave.GetWidth(),
                    (float)CSGlobal.luaConfig.PageSizeToSave.GetHeight()
                );
            }
            else {
                pageSize = new PageSize(bitmap.Width, bitmap.Height);
            }

            document.SetMargins(0, 0, 0, 0);

            var imageData = GetImageData(bitmap, fastFlag);
            var image = new iText.Layout.Element.Image(imageData);

            if (CSGlobal.luaConfig.PageSizeToSave != null) {
                image.ScaleToFit(pageSize.GetWidth(), pageSize.GetHeight());
                image.SetFixedPosition(
                    (pageSize.GetWidth() - image.GetImageScaledWidth()) / 2,
                    (pageSize.GetHeight() - image.GetImageScaledHeight()) / 2
                );
            }

            pdfDoc.AddNewPage(pageSize);
            document.Add(image);
            bitmap.Dispose();
        }

        public static void ImagesToPDF(string directoryPath, Layout layout = Layout.Single, bool fastFlag = false) {
            if (!Directory.Exists(directoryPath)) return;

            var imagePaths = Directory.EnumerateFiles(directoryPath)
                .Where(p => SupportedImageExtensions.Any(e => System.IO.Path.GetExtension(p)?.ToLower() == e))
                .OrderBy(p => p, new StringLenComparer());

            using var ms = new MemoryStream();
            var writer = new PdfWriter(ms);
            using var pdfDoc = new PdfDocument(writer);
            pdfDoc.SetFlushUnusedObjects(true);
            var document = new Document(pdfDoc);

            try {
                if (layout != Layout.DuplexLeftToRight && layout != Layout.DuplexRightToLeft) {
                    foreach (var imagePath in imagePaths) {
                        try {
                            var bitmap = LoadImageFromFile(imagePath);
                            if (bitmap != null) {
                                AddPage(document, pdfDoc, bitmap, fastFlag);
                            }
                        }
                        catch (Exception ex) {
                            Console.Error.WriteLine($"[ImgsToPDFCore] Failed to load image '{imagePath}': {ex.GetType().Name}: {ex.Message}");
                            if (ex.InnerException != null) {
                                Console.Error.WriteLine($"  Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                            }
                        }
                    }
                }
                else {
                    using var enumerator = imagePaths.GetEnumerator();
                    while (enumerator.MoveNext()) {
                        SKBitmap? bm1;
                        try {
                            bm1 = LoadImageFromFile(enumerator.Current);
                        }
                        catch (Exception ex) {
                            Console.Error.WriteLine($"[ImgsToPDFCore] Failed to load image '{enumerator.Current}': {ex.GetType().Name}: {ex.Message}");
                            continue;
                        }
                        if (bm1 == null) continue;

                        if (bm1.Width >= bm1.Height) {
                            AddPage(document, pdfDoc, bm1, fastFlag);
                            continue;
                        }
                        else if (!enumerator.MoveNext()) {
                            AddPage(document, pdfDoc, bm1, fastFlag);
                            break;
                        }

                        SKBitmap? bm2;
                        try {
                            bm2 = LoadImageFromFile(enumerator.Current);
                        }
                        catch (Exception ex) {
                            Console.Error.WriteLine($"[ImgsToPDFCore] Failed to load image '{enumerator.Current}': {ex.GetType().Name}: {ex.Message}");
                            AddPage(document, pdfDoc, bm1, fastFlag);
                            continue;
                        }

                        if (bm2 == null) {
                            AddPage(document, pdfDoc, bm1, fastFlag);
                            continue;
                        }

                        if (bm1.Height >= bm1.Width && bm2.Height >= bm2.Width) {
                            SKBitmap picAtLeft = layout == Layout.DuplexLeftToRight ? bm1 : bm2;
                            SKBitmap picAtRight = layout == Layout.DuplexLeftToRight ? bm2 : bm1;
                            using var combined = CombineBitmap(picAtLeft, picAtRight, 10);
                            AddPage(document, pdfDoc, combined, fastFlag);
                        }
                        else {
                            AddPage(document, pdfDoc, bm1, fastFlag);
                            AddPage(document, pdfDoc, bm2, fastFlag);
                        }
                    }
                }

                if (pdfDoc.GetNumberOfPages() == 0) {
                    pdfDoc.AddNewPage();
                }
            }
            finally {
                document.Close();
            }

            string? pathToSave = CSGlobal.luaConfig!.PathToSave();
            if (string.IsNullOrEmpty(pathToSave)) {
                throw new InvalidOperationException("PathToSave returned null or empty.");
            }

            File.WriteAllBytes(pathToSave, ms.ToArray());
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

                if (!string.IsNullOrEmpty(folderName) && folderName != "abc") {
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
                string relativePath = GetRelativePath(rootPath, file);
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
                var fileAction = PdfAction.CreateGoTo(
                    PdfExplicitDestination.CreateFitH(
                        outputPdf.GetPage(startPage), 0
                    )
                );
                var fileNode = parent.AddOutline(fileName);
                fileNode.AddAction(fileAction);

                currentPage += pageCount;
            }
        }

        private static string GetRelativePath(string rootPath, string fullPath) {
            if (!rootPath.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString())) {
                rootPath += System.IO.Path.DirectorySeparatorChar;
            }

            Uri rootUri = new(rootPath);
            Uri fullUri = new(fullPath);
            Uri relativeUri = rootUri.MakeRelativeUri(fullUri);

            return Uri.UnescapeDataString(relativeUri.ToString())
                .Replace('/', System.IO.Path.DirectorySeparatorChar);
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