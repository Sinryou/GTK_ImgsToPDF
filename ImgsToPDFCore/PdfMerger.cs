using ImgsToPDF.Shared;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Action;
using iText.Kernel.Pdf.Navigation;

namespace ImgsToPDF.Core {
    /// <summary>
    /// PDF 文件合并与书签树生成引擎。
    /// 支持基础合并、单层级书签合并及基于目录结构的多层级深度书签树合并。
    /// </summary>
    internal static class PdfMerger {
        // 路径比较语义统一取自 PathContracts：Windows 下文件系统不区分大小写，
        // 路径字典必须用同一语义，否则 "D:\A" 与 "d:\a" 会被当成两个文件夹，
        // 书签树里出现重复的同名节点。

        /// <summary>
        /// 合并PDF文件
        /// </summary>
        /// <param name="inFiles">待合并文件列表</param>
        /// <param name="outFile">合并生成的文件名称</param>
        public static void PdfMerge(List<string> inFiles, string outFile) {
            CSGlobal.ThrowIfCancelled();
            inFiles.Sort(StringLenComparer.Instance);

            AtomicFileWriter.WriteToFile(outFile, stream => {
                using var writer = new PdfWriter(stream);
                using var outputPdf = new PdfDocument(writer);
                foreach (var file in inFiles) {
                    CSGlobal.ThrowIfCancelled();
                    if (!File.Exists(file)) continue;

                    using var inputPdf = new PdfDocument(new PdfReader(file));
                    inputPdf.CopyPagesTo(1, inputPdf.GetNumberOfPages(), outputPdf);
                }
            });
        }

        /// <summary>
        /// 带单层级文件夹书签的 PDF 合并
        /// </summary>
        public static void PdfMergeWithHierarchicalOutlines(List<string> inFiles, string outFile) {
            CSGlobal.ThrowIfCancelled();
            inFiles.Sort(StringLenComparer.Instance);

            var folderOutlineCache = new Dictionary<string, PdfOutline>();

            AtomicFileWriter.WriteToFile(outFile, stream => {
                using var writer = new PdfWriter(stream);
                using var outputPdf = new PdfDocument(writer);
                int currentPage = 1;

                foreach (var file in inFiles) {
                    CSGlobal.ThrowIfCancelled();
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
        public static string NormalizeDir(string? path) {
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
            return trimmed.EndsWith(':') ? trimmed + System.IO.Path.DirectorySeparatorChar : trimmed;
        }

        /// <summary>
        /// 列出文件所在文件夹到 rootPath 之间（不含 rootPath 本身）的所有上级文件夹，从上层到下层。
        /// 文件不在 rootPath 之下时返回空列表（不为它建文件夹书签）。
        /// </summary>
        public static List<string> AncestorFolders(string rootPath, string filePath) {
            var folders = new List<string>();
            string root = NormalizeDir(rootPath);
            string dir = NormalizeDir(System.IO.Path.GetDirectoryName(filePath));
            while (!string.IsNullOrEmpty(dir) && !string.Equals(dir, root, PathContracts.Comparison)) {
                folders.Add(dir);
                string parent = NormalizeDir(System.IO.Path.GetDirectoryName(dir));
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, dir, PathContracts.Comparison)) {
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
            CSGlobal.ThrowIfCancelled();
            inFiles.Sort(StringLenComparer.Instance);
            string root = NormalizeDir(rootPath);

            // 1. 预扫描：算出每个源文件在合并结果中的起始页；读不出来的 PDF 直接跳过（单个坏文件不该毁掉整次合并）
            var plan = new List<(string File, int StartPage, int PageCount)>();
            int cursor = 1;
            foreach (var file in inFiles) {
                CSGlobal.ThrowIfCancelled();
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
            var folderAnchor = new Dictionary<string, int>(PathContracts.Comparer);
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
            AtomicFileWriter.WriteToFile(outFile, stream => {
                using var writer = new PdfWriter(stream);
                using var outputPdf = new PdfDocument(writer);

                // 拷贝页面
                foreach (var (file, _, pageCount) in plan) {
                    CSGlobal.ThrowIfCancelled();
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
                    .GroupBy(p => NormalizeDir(System.IO.Path.GetDirectoryName(p.File)), PathContracts.Comparer)
                    .ToDictionary(g => g.Key, g => g.OrderBy(p => p.StartPage).ToList(), PathContracts.Comparer);
                var subFoldersByDir = folderAnchor.Keys
                    .Where(f => !string.Equals(f, root, PathContracts.Comparison))
                    .GroupBy(f => NormalizeDir(System.IO.Path.GetDirectoryName(f)), PathContracts.Comparer)
                    .ToDictionary(g => g.Key, g => g.ToList(), PathContracts.Comparer);

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
                    bool isRootLevel = string.Equals(dir, root, PathContracts.Comparison);
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
    }
}
