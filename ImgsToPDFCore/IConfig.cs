using XLua;

namespace ImgsToPDFCore {
    /// <summary>
    /// Lua内定义的配置属性及方法
    /// </summary>
    [CSharpCallLua]
    public interface IConfig {
        string PathToSave();
        iText.Kernel.Geom.Rectangle PageSizeToSave { get; set; }
        int FilePathComparer(string a, string b);
        void PreProcess(string directoryPath, Layout layout, bool fastFlag, bool merge);
        void PostProcess();
        int FastQuality { get; set; }
        /// <summary>
        /// 非 --fast 模式下，因 EXIF 方向属于镜像类（2/3/4）而必须重编码时使用的 JPEG 质量。
        /// 旋转类方向（5~8）由排版期仿射矩阵处理，不消耗此项。
        /// </summary>
        int RotatedJpegQuality { get; set; }
    }
}
