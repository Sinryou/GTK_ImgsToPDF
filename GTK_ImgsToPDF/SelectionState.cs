using GTK_ImgsToPDF.Localization;
using ImgsToPDF.Shared;

namespace GTK_ImgsToPDF {
    /// <summary>
    /// 目标选择的互斥形态：把原先由「路径标签文本 + 布尔标志」共同隐式表达的
    /// 状态收敛为单一可判别来源。
    /// 目录/压缩包/失效路径不作为枚举值：它们是「同一路径读取时对文件系统的实时分类」，
    /// 可能随外部变化（目录被删、网络盘断开），读取时重判才不会出现第二真相源。
    /// </summary>
    internal enum SelectionMode {
        /// <summary>无选择（启动初始态、清除后）。</summary>
        None,
        /// <summary>路径模式：TargetPath 指向目录/压缩包/其它，有效性读取时判定。</summary>
        Path,
        /// <summary>离散图片合并模式：Files 为已排序的非空列表。</summary>
        ImageMerge,
        /// <summary>PDF 合并模式：Files 为已排序的非空列表。</summary>
        PdfMerge
    }

    /// <summary>
    /// 目标选择的完整状态：模式 + 载荷。不可变，经窗口的 SetSelection 整体替换；
    /// 路径子形态（目录/压缩包/失效）的判定集中在此，且每次读取都实时重判文件系统。
    /// </summary>
    internal sealed class SelectionState {
        /// <summary>无选择状态（启动初始态、清除后）。</summary>
        public static readonly SelectionState Empty = new(SelectionMode.None, null, null);

        private SelectionState(SelectionMode mode, string? targetPath, IReadOnlyList<string>? files) {
            Mode = mode;
            TargetPath = targetPath;
            Files = files;
        }

        /// <summary>当前形态。</summary>
        public SelectionMode Mode { get; }

        /// <summary>路径模式下的目标路径；合并模式下为 null。</summary>
        public string? TargetPath { get; }

        /// <summary>合并模式下的文件列表（非空）；其它模式为 null。</summary>
        public IReadOnlyList<string>? Files { get; }

        public static SelectionState FromPath(string path) => new(SelectionMode.Path, path, null);

        /// <summary>离散图片合并状态。文件列表由调用方保证非空，构造后不再修改。</summary>
        public static SelectionState FromImageFiles(List<string> files) => new(SelectionMode.ImageMerge, null, files);

        /// <summary>PDF 合并状态。文件列表由调用方保证非空，构造后不再修改。</summary>
        public static SelectionState FromPdfFiles(List<string> files) => new(SelectionMode.PdfMerge, null, files);

        public bool IsImageMerge => Mode == SelectionMode.ImageMerge;

        public bool IsPdfMerge => Mode == SelectionMode.PdfMerge;

        /// <summary>
        /// 路径模式且目标是真实存在的文件夹。每次读取都实时重判（目录可能已被删除、
        /// 网络盘可能已断开）。先判目录再判扩展名，避免恰以 .zip 等结尾命名的真实目录被误判为压缩包。
        /// </summary>
        public bool IsRealDirectory => Mode == SelectionMode.Path && Directory.Exists(TargetPath);

        /// <summary>路径模式且目标是受支持的压缩包文件（非目录、扩展名匹配）。</summary>
        public bool IsArchiveFile => Mode == SelectionMode.Path && !IsRealDirectory &&
            ImageContracts.ArchiveExtensions.Contains(Path.GetExtension(TargetPath) ?? string.Empty);

        /// <summary>
        /// 当前选择是否可作为生成目标直接开始：
        /// 两种合并模式选中即有效；路径模式要求目标真实存在（目录或压缩包）。
        /// </summary>
        public bool IsStartable => IsImageMerge || IsPdfMerge || IsRealDirectory || IsArchiveFile;

        /// <summary>
        /// 计算路径标签的显示文本与悬停提示（纯派生视图，不写任何控件）。
        /// 路径模式原样显示路径；合并模式单文件显示完整路径，多文件显示「[N 项] 目录名」。
        /// </summary>
        public (string? Text, string? ToolTip) Describe() {
            return Mode switch {
                SelectionMode.Path => (TargetPath, TargetPath),
                SelectionMode.ImageMerge => DescribeFileList(Strings.Path_SelectedImages, "[{0} Images] {1}", "image"),
                SelectionMode.PdfMerge => DescribeFileList(Strings.Path_SelectedPdfs, "[{0} PDFs] {1}", "pdf"),
                _ => (null, null)
            };
        }

        /// <summary>合并模式的标签与悬停提示文案。</summary>
        private (string Text, string ToolTip) DescribeFileList(string countPattern, string fallbackPattern, string kind) {
            if (Files == null || Files.Count == 0) {
                return (string.Empty, string.Empty);
            }

            string firstFile = Files[0];
            string dir = Path.GetDirectoryName(firstFile) ?? string.Empty;
            string dirName = Path.GetFileName(dir);

            if (Files.Count == 1) {
                return (firstFile, firstFile);
            }

            // 资源缺失（自定义/裁剪的资源集）时退回英文模板，不能因为缺一条文案就抛异常
            string pattern = string.IsNullOrEmpty(countPattern) ? fallbackPattern : countPattern;
            string displayDir = string.IsNullOrEmpty(dirName) ? firstFile : dirName;
            string toolTip = string.Format(
                "Selected {0} {1}(s){2}Directory: {3}{2}First file: {4}",
                Files.Count, kind, Environment.NewLine, dir, Path.GetFileName(firstFile));
            return (string.Format(pattern, Files.Count, displayDir), toolTip);
        }
    }
}
