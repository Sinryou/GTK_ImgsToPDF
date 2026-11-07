using Gdk;
using SkiaSharp;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace GTK_ImgsToPDF {
    /// <summary>
    /// UI 图像预览与缩略图处理服务。
    /// 负责按需解码常规图片/WebP、纠正 EXIF 旋转、生成高质量缩略图 Pixbuf 并安全管理非托管资源。
    /// </summary>
    internal static class ImagePreviewHelper {
        static ImagePreviewHelper() {
            NativeLibrary.SetDllImportResolver(typeof(SKImage).Assembly, ResolveSkiaSharpNativeLibrary);
        }

        /// <summary>
        /// 将 GUI 的 SkiaSharp 原生库解析重定向到 Core 子目录，直接复用 Core\libSkiaSharp，
        /// 避免 GUI 根目录与 Core\ 下各存一份完全相同的原生库。
        /// </summary>
        public static IntPtr ResolveSkiaSharpNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) {
            if (libraryName == "libSkiaSharp") {
                string fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? "libSkiaSharp.dll"
                    : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                        ? "libSkiaSharp.dylib"
                        : "libSkiaSharp.so";
                string candidatePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Core", fileName);
                if (File.Exists(candidatePath) && NativeLibrary.TryLoad(candidatePath, out IntPtr handle)) {
                    return handle;
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 从候选图片列表中按顺序尝试加载第一张能成功解码的图片作为缩略图。
        /// </summary>
        public static Pixbuf? LoadFirstUsablePreview(IEnumerable<string> candidateFiles, int maxWidth, int maxHeight) {
            foreach (var file in candidateFiles) {
                var preview = TryLoadPreviewPixbuf(file, maxWidth, maxHeight);
                if (preview != null) {
                    return preview;
                }
            }
            return null;
        }

        /// <summary>
        /// 尝试把单张图片加载为指定最大尺寸的 Pixbuf：
        /// 优先尝试 GdkPixbuf 原生加载，若不受支持（如 WebP 等）则通过 SkiaSharp 解码并转为 Pixbuf。
        /// </summary>
        public static Pixbuf? TryLoadPreviewPixbuf(string imagePath, int maxWidth, int maxHeight) {
            // 1. 尝试使用 GdkPixbuf 加载
            try {
                var pixbuf = new Pixbuf(imagePath, maxWidth, maxHeight, true);
                if (pixbuf != null) {
                    // 如果 GdkPixbuf 没有自动处理 EXIF，可以使用 ApplyOrientation() 修正
                    var oriented = pixbuf.ApplyEmbeddedOrientation();
                    if (oriented != pixbuf) {
                        pixbuf.Dispose();
                    }
                    return oriented;
                }
            }
            catch {
                // GdkPixbuf 不支持此格式，尝试 SkiaSharp
            }

            // 2. 尝试使用 SkiaSharp 加载并处理 EXIF 旋转
            try {
                // SKImage.FromEncodedData 内部会自动读取 EXIF 并修正像素方向
                using var image = SKImage.FromEncodedData(imagePath);
                if (image == null) return null;

                // 这里的 image.Width 和 image.Height 已经是修正过方向后的物理宽高
                double scale = Math.Min(1.0, Math.Min(
                    (double)maxWidth / image.Width,
                    (double)maxHeight / image.Height));

                int targetWidth = (int)(image.Width * scale);
                int targetHeight = (int)(image.Height * scale);

                // 从自动旋转好的 SKImage 提取出 SKBitmap
                using var sourceBitmap = SKBitmap.FromImage(image);
                if (sourceBitmap == null) return null;

                // 执行缩放（如果不需要缩小，直接使用 sourceBitmap）
                using var targetBitmap = (scale < 1.0)
                    ? sourceBitmap.Resize(new SKImageInfo(targetWidth, targetHeight), SKSamplingOptions.Default)
                    : sourceBitmap;

                if (targetBitmap == null) return null;

                // 3. 导出为 PNG 并转为 Gdk.Pixbuf
                using var data = targetBitmap.Encode(SKEncodedImageFormat.Png, 100);
                using var loader = new Gdk.PixbufLoader();
                loader.Write(data.ToArray());
                loader.Close();
                return loader.Pixbuf?.Copy();
            }
            catch {
                return null;
            }
        }

        /// <summary>
        /// 从嵌入资源加载并调整程序图标大小为指定长宽的 Pixbuf
        /// </summary>
        public static Pixbuf? GetAppIcon(int targetWidth = 64, int targetHeight = 64) {
            // 1. 从资源类获取字节数组
            byte[] iconBytes = Properties.Resources.appIcon;

            if (iconBytes == null || iconBytes.Length == 0)
                return null;

            // 2. 将字节数组加载为原始 Pixbuf
            using Pixbuf original = new(iconBytes);
            // 3. 计算等比例缩放尺寸
            // 取 目标宽度/原始宽度 和 目标高度/原始高度 中的最小值，确保图片完全适应框内且不拉伸
            double ratio = Math.Min((double)targetWidth / original.Width, (double)targetHeight / original.Height);

            int finalWidth = (int)(original.Width * ratio);
            int finalHeight = (int)(original.Height * ratio);

            // 4. 返回缩放后的 Pixbuf
            return original.ScaleSimple(finalWidth, finalHeight, InterpType.Bilinear);
        }
    }
}
