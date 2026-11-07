using ImgsToPDF.Shared;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;

namespace ImgsToPDF.Core {
    /// <summary>
    /// PDF 页面排版与文档生成引擎。
    /// 负责按单页或双页（小说模式）对图片进行几何变换、页面缩放、中缝处理并输出为 PDF 文档。
    /// </summary>
    internal static class PdfComposer {
        /// <summary>双页模式下两张图之间的中缝宽度（点）。</summary>
        private const float DuplexPageGap = 10f;

        /// <summary>
        /// 计算把图片按 EXIF 方向放进目标矩形 (x, y, width, height) 的仿射矩阵。
        /// 返回值对应 PDF 的 "a b c d e f cm"：x' = a*u + c*v + e，y' = b*u + d*v + f，
        /// 其中 (u, v) 为图片单位方格坐标（u 向右、v 向上，与 PDF 图像坐标一致）。
        /// 各方向由 EXIF 规范里"第 0 行/第 0 列"的定义推导：1 原样、2 水平镜像、3 旋转 180°、
        /// 4 垂直镜像、5 转置、6 顺时针 90°、7 反转置、8 顺时针 270°。
        /// </summary>
        public static float[] GetOrientationMatrix(ushort orientation, float x, float y, float width, float height) {
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
        public static void DrawImage(PdfCanvas canvas, PdfImagePipeline.PageImage pageImage, float x, float y, float width, float height) {
            float[] m = GetOrientationMatrix(pageImage.Orientation, x, y, width, height);
            canvas.AddImageWithTransformationMatrix(pageImage.Image, m[0], m[1], m[2], m[3], m[4], m[5]);
        }

        /// <summary>
        /// 把一张图片排成一页。图片通过 PdfCanvas 以仿射矩阵直接放置：
        /// 未开启 --fast 时原始字节（JPEG 等）无损直通，EXIF 方向也只在此处应用一次，
        /// 无需重新编码。
        /// </summary>
        public static void AddPage(PdfDocument pdfDoc, PdfImagePipeline.PageImage pageImage) {
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
        public static void AddDuplexPage(PdfDocument pdfDoc, PdfImagePipeline.PageImage left, PdfImagePipeline.PageImage right) {
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
        /// 按配置的排序规则列出目录下的图片（目录不存在或读取失败时返回空列表）。
        /// OrderBy 会立即物化，因此连枚举期间抛出的异常也一并被兜住。
        /// </summary>
        public static List<string> CollectSortedImagePaths(string directoryPath) {
            var result = new List<string>();
            if (string.IsNullOrEmpty(directoryPath) || !Directory.Exists(directoryPath)) {
                return result;
            }
            try {
                result.AddRange(Directory.EnumerateFiles(directoryPath)
                    .Where(PdfImagePipeline.IsSupportedImage)
                    .OrderBy(p => p, StringLenComparer.Instance));
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
            CSGlobal.ThrowIfCancelled();
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
            CSGlobal.ThrowIfCancelled();
            if (directoryPaths == null || directoryPaths.Length == 0) {
                CommonUtils.ReportFailure("No source folder was provided; no PDF was generated.");
                return 0;
            }
            // 目录顺序由调用方（config.lua）保证
            var folders = new List<List<string>>();
            foreach (var directoryPath in directoryPaths) {
                CSGlobal.ThrowIfCancelled();
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
        /// 把指定的图片文件列表写入同一个 PDF，返回实际写入的页数。
        /// 用于 GUI 的"选择图片组"模式：清单里的绝对路径已经是用户点选的结果，
        /// 因此只做存在性过滤与排序，不再扫描目录。
        /// 返回 0 表示没有任何有效图片——此时不创建输出文件，由 config.lua 上报失败。
        /// </summary>
        public static int ImagesToPDFFromImageFiles(string[] imageFiles, Layout layout = Layout.Single, bool fastFlag = false) {
            CSGlobal.ThrowIfCancelled();
            if (imageFiles == null || imageFiles.Length == 0) {
                CommonUtils.ReportFailure("No image file was provided; no PDF was generated.");
                return 0;
            }
            var validImages = imageFiles.Where(File.Exists).OrderBy(p => p, StringLenComparer.Instance).ToList();
            if (validImages.Count == 0) {
                CommonUtils.ReportFailure("No valid existing image file was found; no PDF was generated.");
                return 0;
            }
            return WriteDocument(CSGlobal.luaConfig?.PathToSave(), layout, fastFlag, [validImages], blankPageWhenEmpty: false);
        }

        /// <summary>
        /// 写出一份 PDF 并返回写入的页数（失败返回 0）。
        /// folders 的每个元素是一个文件夹内已排好序的图片列表，按顺序逐页写入。
        /// </summary>
        public static int WriteDocument(string? pathToSave, Layout layout, bool fastFlag, List<List<string>> folders, bool blankPageWhenEmpty) {
            if (string.IsNullOrEmpty(pathToSave)) {
                CommonUtils.ReportFailure("PathToSave returned null or empty; no PDF was generated.");
                return 0;
            }
            int pages = 0;
            bool ok = AtomicFileWriter.WriteToFile(pathToSave, fs => {
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
                    CSGlobal.ThrowIfCancelled();
                    try {
                        AddPage(pdfDoc, PdfImagePipeline.LoadPageImage(imagePath, fastFlag));
                    }
                    catch (Exception ex) {
                        ReportImageFailure(imagePath, ex);
                    }
                }
                return;
            }

            using var enumerator = imagePaths.GetEnumerator();
            while (enumerator.MoveNext()) {
                CSGlobal.ThrowIfCancelled();
                PdfImagePipeline.PageImage bm1;
                try {
                    bm1 = PdfImagePipeline.LoadPageImage(enumerator.Current, fastFlag);
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

                CSGlobal.ThrowIfCancelled();
                PdfImagePipeline.PageImage bm2;
                try {
                    bm2 = PdfImagePipeline.LoadPageImage(enumerator.Current, fastFlag);
                }
                catch (Exception ex) {
                    ReportImageFailure(enumerator.Current, ex);
                    AddPage(pdfDoc, bm1);
                    continue;
                }

                if (bm1.Height >= bm1.Width && bm2.Height >= bm2.Width) {
                    // 两张都是纵向图：并排排入同一页，各自保持原有编码
                    PdfImagePipeline.PageImage picAtLeft = layout == Layout.DuplexLeftToRight ? bm1 : bm2;
                    PdfImagePipeline.PageImage picAtRight = layout == Layout.DuplexLeftToRight ? bm2 : bm1;
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
    }

    /// <summary>
    /// 给文件名排序的方法，不使用默认的排序方法，委托在 Lua (config.lua) 里实现
    /// </summary>
    internal sealed class StringLenComparer : IComparer<string> {
        public static readonly StringLenComparer Instance = new();

        private StringLenComparer() { }

        public int Compare(string? x, string? y) {
            return CSGlobal.luaConfig!.FilePathComparer(x ?? string.Empty, y ?? string.Empty);
        }
    }
}
