using iText.IO.Image;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Action;
using iText.Kernel.Pdf.Navigation;
using iText.Layout;
using iText.Layout.Element;
using iText.Kernel.Geom;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ImgsToPDFCore {
    public enum Layout {
        Single,
        DuplexLeftToRight,
        DuplexRightToLeft
    }

    internal class PDFWrapper {
        // 使用 SkiaSharp 替代 System.Drawing.Bitmap
        private static SKBitmap LoadImageFromFile(string path) {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read)) {
                return SKBitmap.Decode(stream);
            }
        }

        // 将 SKBitmap 转换为 iText ImageData
        private static ImageData GetImageData(SKBitmap bitmap, bool fastFlag) {
            using (var image = SKImage.FromBitmap(bitmap)) {
                SKEncodedImageFormat format = fastFlag ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png;
                int quality = fastFlag ? 80 : 100;

                using (var data = image.Encode(format, quality)) {
                    return ImageDataFactory.Create(data.ToArray());
                }
            }
        }

        // 获取图片尺寸
        private static (int width, int height) GetImageSize(SKBitmap bitmap) {
            return (bitmap.Width, bitmap.Height);
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

            if (CSGlobal.luaConfig.PageSizeToSave != null) {
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

        // 核心转换方法
        private static void ImagesToPdf(List<SKBitmap> imageList, Layout layout = Layout.Single, bool fastFlag = false) {
            using (var ms = new MemoryStream()) {
                var writer = new PdfWriter(ms);
                var pdfDoc = new PdfDocument(writer);
                pdfDoc.SetFlushUnusedObjects(true);

                var document = new Document(pdfDoc);

                if (layout != Layout.DuplexLeftToRight && layout != Layout.DuplexRightToLeft) {
                    foreach (var image in imageList) {
                        AddPage(document, pdfDoc, image, fastFlag);
                    }
                }
                else {
                    for (int i = 0; i < imageList.Count; i++) {
                        if (i + 1 >= imageList.Count ||
                            !(imageList[i].Height >= imageList[i].Width && imageList[i + 1].Height >= imageList[i + 1].Width)) {
                            AddPage(document, pdfDoc, imageList[i], fastFlag);
                        }
                        else {
                            SKBitmap picAtLeft = layout == Layout.DuplexLeftToRight ? imageList[i] : imageList[i + 1];
                            SKBitmap picAtRight = layout == Layout.DuplexLeftToRight ? imageList[i + 1] : imageList[i];

                            using (var combinedBitmap = CombineBitmap(picAtLeft, picAtRight, 10)) {
                                AddPage(document, pdfDoc, combinedBitmap, fastFlag);
                            }

                            imageList[i]?.Dispose();
                            imageList[i + 1]?.Dispose();
                            i++;
                        }
                    }
                }

                // 如果零页，添加空页
                if (pdfDoc.GetNumberOfPages() == 0) {
                    pdfDoc.AddNewPage();
                }

                document.Close();
                pdfDoc.Close();

                string pathToSave = CSGlobal.luaConfig.PathToSave();
                File.WriteAllBytes(pathToSave, ms.ToArray());
            }
        }
        /// <summary>
        /// 将指定文件夹下的图片合并为PDF文件
        /// </summary>
        /// <param name="directoryPath">文件夹路径</param>
        /// <param name="layout">合并方式</param>
        /// <param name="fastFlag">是否以图片质量换取生成速度</param>
        public static void ImagesToPDF(string directoryPath, Layout layout = Layout.Single, bool fastFlag = false) {
            if (!Directory.Exists(directoryPath)) return;

            List<string> imageExtensions = new List<string> {
                ".png", ".apng", ".jpg", ".jpeg", ".jfif", ".pjpeg",
                ".pjp", ".bmp", ".tif", ".tiff", ".gif", ".webp"
            };

            var imagepaths = Directory.EnumerateFiles(directoryPath)
                .Where(p => imageExtensions.Any(e => System.IO.Path.GetExtension(p)?.ToLower() == e))
                .OrderBy(p => p, new StringLenComparer());

            List<SKBitmap> imageBitmapList = new List<SKBitmap>();

            foreach (var imagepath in imagepaths) {
                try {
                    var bitmap = LoadImageFromFile(imagepath);
                    if (bitmap != null) {
                        imageBitmapList.Add(bitmap);
                    }
                }
                catch (Exception ex) {
                    Console.Error.WriteLine($"[ImgsToPDFCore] Failed to load image '{imagepath}': {ex.GetType().Name}: {ex.Message}");
                    if (ex.InnerException != null) {
                        Console.Error.WriteLine($"  Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                    }
                    continue;
                }
            }

            ImagesToPdf(imageBitmapList, layout, fastFlag);

            foreach (var bitmap in imageBitmapList) {
                bitmap?.Dispose();
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

            using (var writer = new PdfWriter(outFile)) {
                using (var outputPdf = new PdfDocument(writer)) {
                    foreach (var file in inFiles) {
                        if (!File.Exists(file)) continue;

                        using (var inputPdf = new PdfDocument(new PdfReader(file))) {
                            inputPdf.CopyPagesTo(1, inputPdf.GetNumberOfPages(), outputPdf);
                        }
                    }
                }
            }
        }

        // 带层级书签的 PDF 合并
        public static void PdfMergeWithHierarchicalOutlines(List<string> inFiles, string outFile) {
            var comparer = new StringLenComparer();
            inFiles.Sort(comparer);

            var folderOutlineCache = new Dictionary<string, PdfOutline>();

            using (var writer = new PdfWriter(outFile)) {
                using (var outputPdf = new PdfDocument(writer)) {
                    int currentPage = 1;

                    foreach (var file in inFiles) {
                        if (!File.Exists(file)) continue;

                        using (var inputPdf = new PdfDocument(new PdfReader(file))) {
                            int pageCount = inputPdf.GetNumberOfPages();

                            // 先复制页面到输出PDF
                            inputPdf.CopyPagesTo(1, pageCount, outputPdf);

                            // 现在可以安全地创建书签，因为页面已经存在
                            string folderName = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(file));
                            string fileName = System.IO.Path.GetFileNameWithoutExtension(file);

                            PdfOutline root = outputPdf.GetOutlines(false);
                            PdfOutline parentNode = root;

                            if (!string.IsNullOrEmpty(folderName) && folderName != "abc") {
                                if (!folderOutlineCache.ContainsKey(folderName)) {
                                    var action = PdfAction.CreateGoTo(
                                        PdfExplicitDestination.CreateFitH(
                                            outputPdf.GetPage(currentPage), 0
                                        )
                                    );
                                    var folderNode = root.AddOutline(folderName);
                                    folderNode.AddAction(action);
                                    folderOutlineCache[folderName] = folderNode;
                                }
                                parentNode = folderOutlineCache[folderName];
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
                }
            }
        }

        // 深层级书签合并
        public static void PdfMergeWithDeepOutlines(List<string> inFiles, string outFile, string rootPath) {
            inFiles.Sort(new StringLenComparer());
            var outlineCache = new Dictionary<string, PdfOutline>();

            using (var writer = new PdfWriter(outFile)) {
                using (var outputPdf = new PdfDocument(writer)) {
                    int currentPage = 1;

                    // 第一遍：先合并所有页面并记录页码范围
                    var pageRanges = new List<(string file, int startPage, int pageCount)>();

                    foreach (var file in inFiles) {
                        if (!File.Exists(file)) continue;

                        using (var inputPdf = new PdfDocument(new PdfReader(file))) {
                            int pageCount = inputPdf.GetNumberOfPages();
                            pageRanges.Add((file, currentPage, pageCount));
                            inputPdf.CopyPagesTo(1, pageCount, outputPdf);
                            currentPage += pageCount;
                        }
                    }

                    // 第二遍：添加书签（现在可以安全地引用页面）
                    currentPage = 1;
                    foreach (var (file, startPage, pageCount) in pageRanges) {
                        string relativePath = GetRelativePath(rootPath, file);
                        string[] pathParts = relativePath.Split(
                            new[] { System.IO.Path.DirectorySeparatorChar },
                            StringSplitOptions.RemoveEmptyEntries
                        );

                        PdfOutline root = outputPdf.GetOutlines(false);
                        PdfOutline parent = root;
                        string currentPathAccumulator = rootPath;

                        // 创建文件夹层级书签
                        for (int i = 0; i < pathParts.Length - 1; i++) {
                            string folderName = pathParts[i];
                            currentPathAccumulator = System.IO.Path.Combine(currentPathAccumulator, folderName);

                            if (!outlineCache.ContainsKey(currentPathAccumulator)) {
                                var action = PdfAction.CreateGoTo(
                                    PdfExplicitDestination.CreateFitH(
                                        outputPdf.GetPage(startPage), 0
                                    )
                                );
                                var folderNode = parent.AddOutline(folderName);
                                folderNode.AddAction(action);
                                outlineCache[currentPathAccumulator] = folderNode;
                            }
                            parent = outlineCache[currentPathAccumulator];
                        }

                        // 创建文件书签
                        string fileName = System.IO.Path.GetFileNameWithoutExtension(file);
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
            }
        }

        private static string GetRelativePath(string rootPath, string fullPath) {
            if (!rootPath.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString())) {
                rootPath += System.IO.Path.DirectorySeparatorChar;
            }

            Uri rootUri = new Uri(rootPath);
            Uri fullUri = new Uri(fullPath);
            Uri relativeUri = rootUri.MakeRelativeUri(fullUri);

            return Uri.UnescapeDataString(relativeUri.ToString())
                .Replace('/', System.IO.Path.DirectorySeparatorChar);
        }
        /// <summary>
        /// 给文件名排序的方法，不使用默认的排序方法，在lua里重写
        /// </summary>
        class StringLenComparer : IComparer<string> {
            int IComparer<string>.Compare(string x, string y) {
                return CSGlobal.luaConfig.FilePathComparer(x, y);
            }
        }
    }
}