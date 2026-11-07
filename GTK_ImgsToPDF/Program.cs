using Gtk;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GTK_ImgsToPDF {
    internal static class Program {
        public const string RestartArgument = "--restarted";
        private const int RestartWaitMilliseconds = 5000;
        private static CancellationTokenSource? _pipeServerCts;

        private static string GetActivationPipeName() {
            // 带上当前用户名，避免 Linux 多用户环境下 /tmp/CoreFxPipe_* 权限冲突
            return $"GTK_ImgsToPDF_Activate_{Environment.UserName}";
        }

        public static void RestartApplication() {
            string? fileName = Environment.ProcessPath;
            if (string.IsNullOrEmpty(fileName)) {
                return;
            }

            _pipeServerCts?.Cancel();

            // 不用 using：Process.Start 返回后新进程仍在运行，立即 Dispose 语义不正确
            Process.Start(new ProcessStartInfo {
                FileName = fileName,
                Arguments = RestartArgument,
                UseShellExecute = true
            });

            Application.Quit();
            Environment.Exit(0);
        }

        [SupportedOSPlatform("windows")]
        private static void ActivateExistingInstanceWindows() {
            const int SW_RESTORE = 9;

            [DllImport("user32.dll")]
            static extern bool ShowWindowAsync(IntPtr hWnd, int cmdShow);

            [DllImport("user32.dll")]
            static extern bool SetForegroundWindow(IntPtr hWnd);

            [DllImport("user32.dll")]
            static extern bool IsIconic(IntPtr hWnd);

            try {
                using var current = Process.GetCurrentProcess();
                foreach (var process in Process.GetProcessesByName(current.ProcessName)) {
                    using (process) {
                        if (process.Id == current.Id) {
                            continue;
                        }
                        IntPtr hWnd = process.MainWindowHandle;
                        if (hWnd != IntPtr.Zero) {
                            if (IsIconic(hWnd)) {
                                ShowWindowAsync(hWnd, SW_RESTORE);
                            }
                            SetForegroundWindow(hWnd);
                            break;
                        }
                    }
                }
            }
            catch {
                // 忽略窗口激活异常
            }
        }

        /// <summary>
        /// 唤醒已运行的单实例窗口：
        /// - Windows：新启动的进程持有外壳赋予的前台焦点权限，先用 Win32 API 直接恢复并前置旧窗口；
        /// - Linux（X11 / Wayland）及 Windows 兜底：通过跨平台命名管道（Linux 下基于 Unix Domain Socket）
        ///   向主实例发送唤醒信号，由主实例在 GTK 主线程调用 <see cref="Window.Deiconify"/> 与 <see cref="Window.Present"/>。
        /// </summary>
        private static void ActivateExistingInstance() {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                ActivateExistingInstanceWindows();
            }

            try {
                using var client = new NamedPipeClientStream(".", GetActivationPipeName(), PipeDirection.Out);
                client.Connect(timeout: 500);
                client.WriteByte(1);
                client.Flush();
            }
            catch {
                // 忽略连接超时或管道异常
            }
        }

        /// <summary>
        /// 在主实例后台监听单实例唤醒管道，收到新实例连接后切回 GTK 主线程恢复并前置窗口。
        /// </summary>
        private static CancellationTokenSource StartActivationPipeServer(MainWindow window) {
            var cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;
            string pipeName = GetActivationPipeName();

            _ = Task.Run(async () => {
                while (!token.IsCancellationRequested) {
                    try {
                        using var server = new NamedPipeServerStream(
                            pipeName,
                            PipeDirection.In,
                            NamedPipeServerStream.MaxAllowedServerInstances,
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous);

                        await server.WaitForConnectionAsync(token);
                        _ = server.ReadByte();

                        // 切回 GTK 主线程执行窗口唤醒，避免跨线程直接操作 UI 控件
                        Application.Invoke((_, _) => {
                            try {
                                window.Deiconify();
                                window.Present();
                            }
                            catch {
                                // 忽略已关闭或正在销毁的窗口异常
                            }
                        });
                    }
                    catch (OperationCanceledException) {
                        break;
                    }
                    catch {
                        try {
                            await Task.Delay(200, token);
                        }
                        catch (OperationCanceledException) {
                            break;
                        }
                    }
                }
            }, token);

            return cts;
        }

        [STAThread]
        public static void Main(string[] args) {
            // 命令行开关按 Windows 惯例不区分大小写
            bool restarted = args.Contains(RestartArgument, StringComparer.OrdinalIgnoreCase);

            using Mutex mutex = new(true, @"GTK_ImgsToPDF", out bool isFirstInstance);

            if (!isFirstInstance) {
                // 普通重复启动：唤醒已有窗口并退出
                if (!restarted) {
                    ActivateExistingInstance();
                    return;
                }

                // 重启出来的实例：旧实例正在退出，给它一点时间，否则会表现为
                // "切换语言后程序关掉了、却没有重启"
                bool acquired;
                try {
                    acquired = mutex.WaitOne(RestartWaitMilliseconds);
                }
                catch (AbandonedMutexException) {
                    // 旧实例被强杀，互斥体所有权已转移给本进程
                    acquired = true;
                }

                if (!acquired) {
                    return;
                }
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
                static extern bool SetDllDirectory(string lpPathName);
                string runtimeBin = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtime", "bin");
                string runtimeRoot = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtime");

                if (Directory.Exists(runtimeBin)) {
                    SetDllDirectory(runtimeBin);
                }
                else if (Directory.Exists(runtimeRoot)) {
                    SetDllDirectory(runtimeRoot);
                }
            }

            Application.Init();
            var mainWindow = new MainWindow();
            using var pipeServerCts = StartActivationPipeServer(mainWindow);
            _pipeServerCts = pipeServerCts;
            Application.Run();
        }
    }
}
