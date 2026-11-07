using GTK_ImgsToPDF.Localization;
using ImgsToPDF.Shared;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GTK_ImgsToPDF {
    /// <summary>
    /// ImgsToPDFCore 子进程调度与跨进程通信（IPC）服务。
    /// 负责按可用物理内存及 CPU 核心动态并发拉起 Core 进程、注入父进程看门狗标记、
    /// 拦截密码输入请求并通过标准输入/输出完成 IPC 交互。
    /// </summary>
    internal static class CoreProcessRunner {
        /// <summary>
        /// 同时运行的 Core 进程数硬上限：每个进程都是完整 .NET 运行时，
        /// 高核数机器上不设上限会一次拉起几十个进程。
        /// </summary>
        public const int MaxCoreProcessConcurrency = 8;

        /// <summary>
        /// 单个 Core 进程的峰值内存预算（含 .NET 运行时、SkiaSharp、
        /// 最大单张图片解码及编码缓冲）。按大图最坏情况估算，
        /// 实际峰值通常低于此值。
        /// </summary>
        public const long PerCoreProcessMemoryBudget = 1L << 30; // 1 GB

        /// <summary>
        /// 按当前可用内存估算允许同时运行的 Core 进程数，
        /// 图片很大时避免同时解码过多大图导致内存耗尽。
        /// </summary>
        public static int GetConcurrencyLimitByMemory() {
            try {
                // .NET 10 的跨平台替代方案，无需再 P/Invoke GlobalMemoryStatusEx
                long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                if (available <= 0) {
                    return int.MaxValue; // 查询不到时不额外限制
                }
                return (int)Math.Max(1, available / PerCoreProcessMemoryBudget);
            }
            catch {
                return int.MaxValue;
            }
        }

        /// <summary>
        /// 综合 CPU 核心数、任务数与可用内存计算最大并发度
        /// </summary>
        public static int CalculateMaxConcurrency(int taskCount) {
            return Math.Max(1, Math.Min(
                Math.Min(Environment.ProcessorCount, taskCount),
                Math.Min(GetConcurrencyLimitByMemory(), MaxCoreProcessConcurrency)));
        }

        /// <summary>
        /// 构造传给 Core 进程的命令行参数
        /// </summary>
        public static string[] BuildCoreArgs(string path, bool fastMode, Layout layout, bool mergePdfs = false) {
            if (mergePdfs) {
                return ["-d", path, "--merge-pdfs"];
            }
            var args = new List<string> { "-d", path, "-l", layout.ToString() };
            if (fastMode) {
                args.Add("--fast");
            }
            return [.. args];
        }

        /// <summary>
        /// 归类一次 Core 进程的执行结果。
        /// 失败（退出码非 0）：stderr 为空时补上退出码，避免弹出内容为空的对话框；
        /// 警告（退出码为 0 但 stderr 有内容）：例如个别图片解码失败被跳过，PDF 本身已生成。
        /// 两种情况都会带上对应的目录/压缩包路径，并发处理多个目录时才能定位是谁出的问题。
        /// </summary>
        public static void CollectProcessResult(
            string targetPath,
            string? stderr,
            int exitCode,
            ConcurrentQueue<string> failureQueue,
            ConcurrentQueue<string> warningQueue) {
            string detail = stderr == null ? string.Empty : stderr.Trim();
            if (exitCode != 0) {
                if (detail.Length == 0) {
                    // 退出码非 0 却没有输出：必须仍然报失败，否则会被当成生成成功
                    detail = string.Format(Strings.Msg_NoErrorOutput, exitCode);
                }
                failureQueue.Enqueue(targetPath + Environment.NewLine + detail);
            }
            else if (detail.Length > 0) {
                warningQueue.Enqueue(targetPath + Environment.NewLine + detail);
            }
        }

        /// <summary>
        /// 在后台生成 PDF；结果通过返回值收集，统一回到 UI 线程展示。
        /// 判定标准：Core 进程退出码非 0 才算失败；退出码为 0 但 stderr 有内容
        /// （例如个别图片无法解码被跳过）只算警告，不再把成功当成失败上报。
        /// 所有界面状态都由调用方在主线程快照后传入，本方法内部不得直接访问任何 UI 控件。
        /// </summary>
        public static async Task<(List<string> failures, List<string> warnings)> ExecuteBatchAsync(
            string directoryPath,
            bool recursive,
            bool fastMode,
            bool merge,
            Layout layout,
            string displayTargetPath,
            Func<string, Task<string?>> promptPasswordAsync,
            Func<bool> isCancelled) {

            // 根据平台动态决定文件名
            string coreName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "ImgsToPDFCore.exe"
                : "ImgsToPDFCore";
            string fileName = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Core", coreName);
            var failureQueue = new ConcurrentQueue<string>();
            var warningQueue = new ConcurrentQueue<string>();
            // 报错时展示用户能认出的路径：清单模式下传入的是临时 txt，不能让用户看到那个
            string reportedTarget = string.IsNullOrEmpty(displayTargetPath) ? directoryPath : displayTargetPath;

            if (recursive && Directory.Exists(directoryPath)) {
                // 递归收集子目录可能较慢（大量文件夹），放到后台执行，
                // 否则含上千个子目录时点"开始"会让窗口出现"无响应"
                var dirs = await Task.Run(() => FolderScanner.RecursiveFolder(directoryPath));
                if (dirs.Count == 0) {
                    failureQueue.Enqueue(reportedTarget + Environment.NewLine + Strings.Hint_NoImagesFound);
                    return ([.. failureQueue], [.. warningQueue]);
                }

                int maxConcurrency = CalculateMaxConcurrency(dirs.Count);
                using var semaphore = new SemaphoreSlim(maxConcurrency);
                var tasks = dirs.Select(async dirPath => {
                    await semaphore.WaitAsync();
                    try {
                        if (isCancelled()) return;
                        var (_, stderr, exitCode) = await RunProcessAsync(fileName, BuildCoreArgs(dirPath, fastMode, layout), promptPasswordAsync);
                        CollectProcessResult(dirPath, stderr, exitCode, failureQueue, warningQueue);
                    }
                    finally {
                        semaphore.Release();
                    }
                }).ToList();
                await Task.WhenAll(tasks);

                if (merge && !isCancelled()) {
                    var (_, stderr, exitCode) = await RunProcessAsync(fileName, BuildCoreArgs(directoryPath, fastMode: false, layout: Layout.Single, mergePdfs: true), promptPasswordAsync);
                    CollectProcessResult(directoryPath, stderr, exitCode, failureQueue, warningQueue);
                }
            }
            else if (!isCancelled()) {
                var (_, stderr, exitCode) = await RunProcessAsync(fileName, BuildCoreArgs(directoryPath, fastMode, layout), promptPasswordAsync);
                CollectProcessResult(reportedTarget, stderr, exitCode, failureQueue, warningQueue);
            }

            return ([.. failureQueue], [.. warningQueue]);
        }

        /// <summary>
        /// 运行给定的命令，返回标准输出、标准错误与退出码。
        /// 监听标准输出并拦截密码请求指令（::__REQ_PASSWORD__::），
        /// 通过回调在界面线程弹出密码框，再把结果经标准输入回传。
        /// stdout 与 stderr 必须同时异步读取，否则管道缓冲写满时会互相阻塞导致死锁。
        /// </summary>
        public static async Task<(string stdout, string stderr, int exitCode)> RunProcessAsync(
            string fileName,
            string[] args,
            Func<string, Task<string?>> promptPasswordAsync) {

            using Process p = new();
            p.StartInfo.FileName = fileName;
            p.StartInfo.ArgumentList.Clear();
            foreach (var arg in args) {
                p.StartInfo.ArgumentList.Add(arg);
            }
            p.StartInfo.UseShellExecute = false;        // Shell的使用
            p.StartInfo.RedirectStandardInput = true;   // 重定向输入
            p.StartInfo.RedirectStandardOutput = true;  // 重定向输出
            p.StartInfo.RedirectStandardError = true;   // 重定向输出错误
            // 子进程在 Program.Main 中把 Console 输出编码固定为 UTF-8（无 BOM），
            // 这里必须配套以 UTF-8 解码，否则中文路径或错误信息在 GBK 等系统代码页下会变乱码。
            var utf8NoBom = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            p.StartInfo.StandardOutputEncoding = utf8NoBom;
            p.StartInfo.StandardErrorEncoding = utf8NoBom;
            p.StartInfo.CreateNoWindow = true;          // 设置不显示窗口
            p.StartInfo.WorkingDirectory = System.IO.Path.GetDirectoryName(fileName);

            // 显式告知 Core「我是 GUI 拉起的」，它才会开启父进程看门狗：
            // 该看门狗把 stdin 当作父进程存活管道，一旦读到 EOF 就取消任务并在宽限期后自毁，
            // 避免 GUI 退出后残留孤儿进程。命令行/脚本调用不会带这个标志。
            p.StartInfo.Environment[IpcContracts.WatchParentEnvVar] = "1";

            p.Start();

            // 逐行读 stdout：密码请求指令会被拦截并在界面线程弹窗，其余行照常累积。
            // stderr 同时异步读，否则管道缓冲写满时会互相阻塞导致死锁。
            var stdoutSb = new System.Text.StringBuilder();
            var stdoutTask = Task.Run(async () => {
                try {
                    while (true) {
                        string? line = await p.StandardOutput.ReadLineAsync();
                        if (line == null) {
                            break;
                        }

                        if (line.StartsWith(IpcContracts.ReqPasswordPrefix, StringComparison.Ordinal)) {
                            string archivePath = line[IpcContracts.ReqPasswordPrefix.Length..];
                            string? password = promptPasswordAsync != null ? await promptPasswordAsync(archivePath) : null;
                            try {
                                if (password != null) {
                                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(password);
                                    string base64 = Convert.ToBase64String(bytes);
                                    await p.StandardInput.WriteLineAsync($"{IpcContracts.RespPasswordPrefix}{base64}");
                                    await p.StandardInput.FlushAsync();
                                }
                                else {
                                    await p.StandardInput.WriteLineAsync(IpcContracts.CancelToken);
                                    await p.StandardInput.FlushAsync();
                                }
                            }
                            catch {
                                // 进程已退出或管道已关闭
                            }
                        }
                        else {
                            stdoutSb.AppendLine(line);
                        }
                    }
                }
                catch {
                    // 读取异常（如子进程被强制终止导致管道损坏）
                }
                return stdoutSb.ToString();
            });

            var stderrTask = p.StandardError.ReadToEndAsync();
            var outputs = await Task.WhenAll(stdoutTask, stderrTask);

            await p.WaitForExitAsync();

            return (outputs[0], outputs[1], p.ExitCode);
        }
    }
}
