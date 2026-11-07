using ImgsToPDF.Shared;
using System.Diagnostics;

namespace GTK_ImgsToPDF {
    /// <summary>
    /// 文件系统与目录树扫描服务。
    /// 负责按自然排序规则进行路径比较，以非递归 DFS 显式栈遍历文件夹并过滤重解析点与受支持的图片类型。
    /// </summary>
    internal static class FolderScanner {
        /// <summary>
        /// 遵循 Windows 资源管理器自然排序规范的字符串比较器
        /// （"img2.jpg" 排在 "img10.jpg" 之前，而不是按字符序把 "10" 排到 "2" 前面）。
        ///
        /// Windows 上直接复用系统实现 StrCmpLogicalW，与资源管理器逐字节一致；
        /// 其它平台没有该 API，退化为等价语义的托管实现。
        /// </summary>
        public sealed class NaturalPathComparer : IComparer<string> {
            public static readonly NaturalPathComparer Instance = new();

            private NaturalPathComparer() { }

            public int Compare(string? x, string? y) {
                if (ReferenceEquals(x, y)) return 0;
                if (x == null) return -1;
                if (y == null) return 1;
                return OperatingSystem.IsWindows()
                    ? NativeMethods.StrCmpLogicalW(x, y)
                    : CompareNatural(x, y);
            }

            /// <summary>
            /// 托管版自然排序：逐段比较，连续数字段按数值大小而不是字符序比较。
            /// 语义对齐 StrCmpLogicalW —— 大小写不敏感、数字段整体比较、位数多者更大、
            /// 同位置下数字排在非数字之前（"a1" &lt; "ab"）。
            ///
            /// 声明为 internal 供单元测试直接覆盖：Windows 上 Compare 会走系统 API，
            /// 托管分支在开发机上根本执行不到。
            /// </summary>
            internal static int CompareNatural(string x, string y) {
                int i = 0;
                int j = 0;

                while (i < x.Length && j < y.Length) {
                    bool xIsDigit = char.IsAsciiDigit(x[i]);
                    bool yIsDigit = char.IsAsciiDigit(y[j]);

                    if (xIsDigit && yIsDigit) {
                        int byNumber = CompareNumberRun(x, ref i, y, ref j);
                        if (byNumber != 0) {
                            return byNumber;
                        }
                        continue;
                    }

                    // 同一位次上，数字段排在非数字段之前（与资源管理器一致）
                    if (xIsDigit != yIsDigit) {
                        return xIsDigit ? -1 : 1;
                    }

                    char cx = char.ToUpperInvariant(x[i]);
                    char cy = char.ToUpperInvariant(y[j]);
                    if (cx != cy) {
                        return cx < cy ? -1 : 1;
                    }
                    i++;
                    j++;
                }

                // 前缀相同：短的那个更小（"a" < "ab"）
                return (x.Length - i).CompareTo(y.Length - j);
            }

            /// <summary>
            /// 比较从 i / j 开始的连续数字段，并把两个游标推进到各自数字段的末尾。
            /// 先比有效位数（前导零不算），位数相同再逐字符比较，避免超长数字串溢出。
            /// </summary>
            private static int CompareNumberRun(string x, ref int i, string y, ref int j) {
                int xStart = i;
                int yStart = j;
                while (xStart < x.Length && x[xStart] == '0') xStart++;
                while (yStart < y.Length && y[yStart] == '0') yStart++;

                int xEnd = xStart;
                while (xEnd < x.Length && char.IsAsciiDigit(x[xEnd])) xEnd++;
                int yEnd = yStart;
                while (yEnd < y.Length && char.IsAsciiDigit(y[yEnd])) yEnd++;

                int xRunLength = xEnd - i;
                int yRunLength = yEnd - j;
                int xDigits = xEnd - xStart;
                int yDigits = yEnd - yStart;

                int result = xDigits != yDigits
                    ? (xDigits < yDigits ? -1 : 1)
                    : string.CompareOrdinal(x, xStart, y, yStart, xDigits);

                // 数值相同（"01" 与 "1"）时前导零多的一侧更小。这不是随手定的规则：
                // StrCmpLogicalW 在该情形下同样由前导零个数拍板（实测 "a01z" &lt; "a1b"，
                // 后续字符并不参与），所以这里直接返回，不延后裁决。
                if (result == 0 && xRunLength != yRunLength) {
                    result = yRunLength.CompareTo(xRunLength);
                }

                // 游标始终推进到整段末尾（含前导零），保证外层按原字符位置继续
                i = xEnd;
                j = yEnd;
                return result;
            }
        }

        /// <summary>
        /// 检查指定文件夹中是否包含至少一张受支持的图片文件。
        /// 使用 EnumerateFiles 惰性枚举，遇到第一张有效图片即返回 true，避免全量遍历产生的性能损耗。
        /// </summary>
        public static bool ContainsSupportedImage(string directoryPath) {
            try {
                return Directory.EnumerateFiles(directoryPath)
                    .Any(f => ImageContracts.ImageExtensions.Contains(Path.GetExtension(f)));
            }
            catch (Exception ex) {
                Debug.WriteLine($"[ImgsToPDF] Failed to enumerate files in '{directoryPath}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 递归遍历目录树，收集所有包含受支持图片的文件夹路径。
        /// 具备以下防御与优化能力：
        /// 1. 采用栈式迭代（DFS）代替方法递归，彻底规避极端目录层级下的栈溢出（StackOverflowException 不可捕获，会直接崩进程）；
        /// 2. 检查并跳过重解析点（ReparsePoint，如 NTFS Junction、符号链接），同时维护规范化路径的已访问集合，杜绝死循环；
        /// 3. 逐层捕获无权限/路径过长/IO 异常，遇到受限文件夹时跳过，避免整次扫描崩溃；
        /// 4. 仅收集实际包含图片的文件夹，过滤空目录与纯子目录容器 ——
        ///    否则每个空目录都会被拉起一个 Core 进程并生成一页空白 PDF。
        /// </summary>
        public static List<string> RecursiveFolder(string rootPath, CancellationToken cancellationToken = default) {
            var imageDirs = new List<string>();
            if (string.IsNullOrWhiteSpace(rootPath)) {
                return imageDirs;
            }

            DirectoryInfo rootDirInfo;
            try {
                rootDirInfo = new DirectoryInfo(Path.GetFullPath(rootPath));
                if (!rootDirInfo.Exists) {
                    return imageDirs;
                }
            }
            catch (Exception ex) {
                Debug.WriteLine($"[ImgsToPDF] Invalid root path '{rootPath}': {ex.Message}");
                return imageDirs;
            }

            // 去重语义必须与文件系统一致（见 PathContracts）：
            // Windows 上 "D:\Pics" 与 "d:\pics" 是同一目录，忽略大小写才不会重复处理；
            // Linux 上它们是两个真实目录，忽略大小写会把其中一份的图片静默丢掉。
            var visited = new HashSet<string>(PathContracts.Comparer);
            var stack = new Stack<DirectoryInfo>();
            stack.Push(rootDirInfo);

            while (stack.Count > 0) {
                cancellationToken.ThrowIfCancellationRequested();
                DirectoryInfo currentDir = stack.Pop();

                // 统一去除末尾路径分隔符，防止 "C:\Dir\" 与 "C:\Dir" 在 HashSet 中被视为不同路径
                string normalizedPath = currentDir.FullName.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);

                if (!visited.Add(normalizedPath)) {
                    continue;
                }

                if (ContainsSupportedImage(currentDir.FullName)) {
                    imageDirs.Add(currentDir.FullName);
                }

                try {
                    foreach (DirectoryInfo childFolder in currentDir.EnumerateDirectories()) {
                        try {
                            // 跳过重解析点（符号链接、Junction 联接点等），防止死循环
                            if ((childFolder.Attributes & FileAttributes.ReparsePoint) != 0) {
                                Debug.WriteLine($"[ImgsToPDF] Skipped reparse point directory: '{childFolder.FullName}'");
                                continue;
                            }
                            stack.Push(childFolder);
                        }
                        catch (Exception ex) {
                            Debug.WriteLine($"[ImgsToPDF] Failed to inspect child directory '{childFolder.FullName}': {ex.Message}");
                        }
                    }
                }
                catch (Exception ex) {
                    Debug.WriteLine($"[ImgsToPDF] Skipped inaccessible subdirectories of '{currentDir.FullName}': {ex.Message}");
                }
            }

            // 排序只为让并发任务的启动顺序稳定；每个目录各出一份 PDF，顺序不影响产物内容
            imageDirs.Sort(NaturalPathComparer.Instance);
            return imageDirs;
        }
    }
}
