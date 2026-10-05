using Gdk;
using Gtk;
using GTK_ImgsToPDF.Config;
using GTK_ImgsToPDF.Localization;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace GTK_ImgsToPDF {
    public class ImgsToPDF : Gtk.Window {
        private readonly ConfigService _configService = new();
        // 界面控件引用，用于动态更新
        private Overlay _overlay = null!;
        private EventBox _dropTarget = null!; // 接收拖拽的区域
        private Image _mainImage = null!;       // 显示图片预览（或初始大文件夹）
        private Label _hintLabel = null!;       // "拖入包含图片的文件夹"
        private Label _pathLabel = null!;       // 显示 E:\Temp
        private Image _smallFolderIcon = null!; // 叠加的小文件夹图标
        private Button _startBtn = null!;
        private CheckButton _lossyCheck = null!;
        private CheckButton _recursiveCheck = null!;
        private CheckButton _mergeCheck = null!;
        private ComboBoxText _layoutCombo = null!;

        // 定义支持的文件扩展名。
        // 用 OrdinalIgnoreCase 比较而不是 ToLower()：扩展名与当前区域设置无关
        // （tr-TR 下 "I".ToLower() 是无点 "ı"，".TIF" 会被误判为不支持），
        // 同时省掉每个文件一次 ToLower 分配。
        private readonly HashSet<string> _supportedExtensions = new(StringComparer.OrdinalIgnoreCase) {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".jfif", ".pjpeg", ".pjp", ".apng"
        };
        private readonly HashSet<string> _supportedCompressedExtensions = new(StringComparer.OrdinalIgnoreCase) {
            ".zip", ".rar", ".7z"
        };
        private CssProvider? _hintStyleProvider;

        /// <summary>
        /// 预览图加载的请求序号。预览解码在后台线程进行，用户可能在解码完成前
        /// 又选了别的目录；用自增序号作废过期结果，避免慢加载覆盖新选择。
        /// </summary>
        private int _previewRequestId;

        public ImgsToPDF() : base("ImgsToPDF") {
            // 设置中的语言值可能为空或无效（config.json 被手改/损坏），
            // 构造 CultureInfo 失败时回退到系统当前语言，避免应用无法启动
            string language = _configService.Config.UILocale != "" ? _configService.Config.UILocale : System.Globalization.CultureInfo.CurrentCulture.Name;
            try {
                Thread.CurrentThread.CurrentUICulture = new System.Globalization.CultureInfo(language);
            }
            catch (System.Globalization.CultureNotFoundException) {
                Thread.CurrentThread.CurrentUICulture = System.Globalization.CultureInfo.CurrentCulture;
            }

            SetDefaultSize(800, 600);
            SetPosition(WindowPosition.Center);
            this.DeleteEvent += (s, e) => Application.Quit();

            // 窗口图标：csproj 的 ApplicationIcon 只管可执行文件本身，
            // 标题栏与任务栏图标需要在这里显式设置
            using (var windowIcon = GetAppIcon(48, 48)) {
                if (windowIcon != null) {
                    this.Icon = windowIcon;
                }
            }

            // 主布局：垂直盒子
            Box mainBox = new(Orientation.Vertical, spacing: 0) { Homogeneous = false };
            Add(mainBox);

            // 1. 菜单栏
            mainBox.PackStart(CreateMenuBar(), false, false, 0);

            // 2. 中央区域 (拖放区)
            mainBox.PackStart(CreateCentralDragArea(), true, true, 0);

            // 3. 底部控制栏
            mainBox.PackStart(CreateBottomControls(), false, false, 10);

            ShowAll();

            // 初始状态下隐藏叠加的小图标
            _smallFolderIcon.Hide();
        }

        private MenuBar CreateMenuBar() {
            MenuBar menuBar = [];

            // useUnderline: true 让 GTK 把文案里的下划线解析为 Alt 助记符
            // （对应原版 WinForms 的 &F / &O / &Z …）
            MenuItem fileMenu = new(Strings.Menu_File) { UseUnderline = true };
            Menu fileSub = [];

            MenuItem openFolderItem = new(Strings.Menu_OpenFolder) { UseUnderline = true };
            openFolderItem.Activated += (s, e) => SelectFolder();
            fileSub.Append(openFolderItem);

            MenuItem openArchiveItem = new(Strings.Menu_OpenArchive) { UseUnderline = true };
            openArchiveItem.Activated += (s, e) => SelectArchive();
            fileSub.Append(openArchiveItem);

            MenuItem clearChosenItem = new(Strings.Menu_ClearSelection) { UseUnderline = true };
            clearChosenItem.Activated += (s, e) => {
                SetPathLabel(Strings.Path_Waiting);
                ResetToInitialState();
            };
            fileSub.Append(clearChosenItem);

            fileSub.Append(new SeparatorMenuItem());

            MenuItem quitItem = new(Strings.Menu_Exit) { UseUnderline = true };
            quitItem.Activated += (s, e) => Application.Quit();
            fileSub.Append(quitItem);
            fileMenu.Submenu = fileSub;

            menuBar.Append(fileMenu);

            MenuItem configFileItem = new(Strings.Menu_Config) { UseUnderline = true };
            configFileItem.Activated += (s, e) => {
                string cfgFilePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Core", "config.lua");
                if (!File.Exists(cfgFilePath)) {
                    MsgBox.Show(this, Strings.Msg_ConfigMissing);
                    return;
                }
                try {
                    // 不用 using：Process.Start 返回后记事本仍在运行，立即 Dispose 语义不正确；
                    // 且文件不存在或未关联打开程序时会抛 Win32Exception，需要提示而不是崩溃
                    Process.Start(new ProcessStartInfo(cfgFilePath) { UseShellExecute = true });
                }
                catch (Exception ex) {
                    MsgBox.Show(this, ex.Message);
                }
            };
            menuBar.Append(configFileItem);

            MenuItem langItem = new(Strings.Menu_Lang) { UseUnderline = true };
            Menu langSub = [];

            MenuItem menuItemLangCN = new("中文(CN)");
            menuItemLangCN.Activated += (s, e) => {
                _configService.Config.UILocale = "zh-CN";
                _configService.Save();
                MsgBox.Show(this, "注意：\n语言已切换为中文，程序将立即重启以生效你的语言设置。");
                RestartApplication();
            };
            langSub.Append(menuItemLangCN);

            MenuItem menuItemLangEN = new("English(EN)");
            menuItemLangEN.Activated += (s, e) => {
                _configService.Config.UILocale = "en-US";
                _configService.Save();
                MsgBox.Show(this, "Notice:\nLanguage switched to English, application will restart immediately to take effect your language setting.");
                RestartApplication();
            };
            langSub.Append(menuItemLangEN);

            if (Thread.CurrentThread.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) {
                menuItemLangCN.Sensitive = false;
            }
            else {
                menuItemLangEN.Sensitive = false;
            }

            langItem.Submenu = langSub;
            menuBar.Append(langItem);

            MenuItem aboutItem = new(Strings.Menu_About) { UseUnderline = true };
            aboutItem.Activated += OnAboutClicked;
            menuBar.Append(aboutItem);

            return menuBar;
        }

        private EventBox CreateCentralDragArea() {
            // 1. 使用 EventBox 使整个中央区域可接收事件
            _dropTarget = [];

            // 2. 使用 Overlay 允许元素重叠
            _overlay = [];
            _dropTarget.Add(_overlay);

            // --- 底层：垂直内容布局 ---
            Box contentBox = new(Orientation.Vertical, spacing: 10) {
                Homogeneous = false,
                Valign = Align.Center
            };

            // 初始状态：显示大文件夹图标
            // 这里使用内置 Stock 图标模拟，实际开发可用特定的 PNG 资源
            _mainImage = Image.NewFromIconName("folder", IconSize.Dialog);
            // 调整图标大小（可选，如果 Stock 图标太小）
            //_mainImage.PixelSize = 128;

            _hintLabel = new Label(Strings.Hint_Initial);
            SetLabelColor(_hintLabel, 0, 0, 255); // 蓝色

            _pathLabel = new Label(Strings.Path_Waiting) {
                MarginTop = 10,
                // 超长路径不应把窗口撑变形：限制最大宽度并中间省略
                Ellipsize = Pango.EllipsizeMode.Middle,
                MaxWidthChars = 60,
                TooltipText = Strings.Path_Waiting
            }; // 初始状态

            contentBox.PackStart(_mainImage, false, false, 0);
            contentBox.PackStart(_hintLabel, false, false, 0);
            contentBox.PackStart(_pathLabel, false, false, 0);

            _overlay.Add(contentBox);

            // --- 叠加层：小文件夹图标 ---
            // 实际开发中应加载一个自定义的透明 PNG 文件
            _smallFolderIcon = Image.NewFromIconName("folder", IconSize.Menu);
            //_smallFolderIcon.PixelSize = 32; // 变小

            // 设置在左下角
            _smallFolderIcon.Halign = Align.Start;
            _smallFolderIcon.Valign = Align.End;
            // 设置边距，防止紧贴边缘
            _smallFolderIcon.MarginStart = 10;
            _smallFolderIcon.MarginBottom = 10;

            _overlay.AddOverlay(_smallFolderIcon);


            // --- 配置拖拽目标的接收能力 ---
            // 设置目标类型为 URI 列表（文件浏览器拖拽通常是这个类型）
            TargetEntry[] targets = [
            new TargetEntry("text/uri-list", 0, 0)
        ];
            Gtk.Drag.DestSet(_dropTarget, DestDefaults.All, targets, DragAction.Copy);

            // 连接拖拽接收事件
            _dropTarget.DragMotion += OnDragMotion;
            _dropTarget.DragLeave += OnDragLeave;
            _dropTarget.DragDataReceived += OnDragDataReceived;

            // 高亮样式只需注册一次；AddProviderForScreen 是静态方法，无需保留字段
            Gtk.StyleContext.AddProviderForScreen(Gdk.Screen.Default, CreateDropHighlightProvider(), Gtk.StyleProviderPriority.User);

            return _dropTarget;
        }

        /// <summary>
        /// 拖拽经过时给拖放区加高亮边框，让"可接收"有正反馈
        /// （对应原版把 e.Effect 设为 All 时光标变化的可感知性）。
        /// </summary>
        private void SetDropHighlight(bool active) {
            if (active) {
                _dropTarget.StyleContext.AddClass("dsh-drop-target");
            }
            else {
                _dropTarget.StyleContext.RemoveClass("dsh-drop-target");
            }
        }

        private static CssProvider CreateDropHighlightProvider() {
            var provider = new CssProvider();
            provider.LoadFromData("""
                .dsh-drop-target { border: 2px dashed alpha(currentColor, 0.55); border-radius: 6px; }
                """);
            return provider;
        }

        private void OnDragLeave(object o, DragLeaveArgs args) {
            SetDropHighlight(false);
        }

        private Box CreateBottomControls() {
            // 底部控制栏布局 (与前一个代码示例类似，增加了进度条)
            Box bottomBox = new(Orientation.Vertical, spacing: 10) {
                Homogeneous = false,
                MarginStart = 20,
                MarginEnd = 20,
                MarginBottom = 10
            };

            // 1. 创建 CheckButton 实例并保留引用
            _lossyCheck = new CheckButton(Strings.Check_Lossy);
            _recursiveCheck = new CheckButton(Strings.Check_Recursive);
            _mergeCheck = new CheckButton(Strings.Check_Merge) {
                // 2. 设置初始状态
                Sensitive = false // 默认禁用状态
            };
            _recursiveCheck.Active = false; // 确保初始未勾选
            // 3. 编写联动逻辑：当递归勾选状态改变时触发
            _recursiveCheck.Toggled += (s, e) => {
                // 只有当“递归子文件夹”被勾选时，“合并子PDF”才可用
                _mergeCheck.Sensitive = _recursiveCheck.Active;
                // 可选：如果取消勾选递归，自动也取消勾选合并（防止逻辑冲突）
                if (!_recursiveCheck.Active) {
                    _mergeCheck.Active = false;
                }
            };

            // 4. 将它们添加到布局中
            Box checkBoxes = new(Orientation.Horizontal, spacing: 10) { Homogeneous = true };
            checkBoxes.PackStart(_lossyCheck, false, false, 0);
            checkBoxes.PackStart(_recursiveCheck, false, false, 0);
            checkBoxes.PackStart(_mergeCheck, false, false, 0);
            bottomBox.PackStart(checkBoxes, false, false, 0);

            Box actionBox = new(Orientation.Horizontal, spacing: 10) { Homogeneous = false };
            actionBox.PackStart(new Label(Strings.Layout_Label), false, false, 0);
            _layoutCombo = [];
            _layoutCombo.AppendText(Strings.Layout_Single);
            _layoutCombo.AppendText(Strings.Layout_Duplexlr);
            _layoutCombo.AppendText(Strings.Layout_Duplexrl);
            _layoutCombo.Active = 0;
            actionBox.PackStart(_layoutCombo, false, false, 20);

            // 使用类字段 startBtn
            _startBtn = new Button(Strings.Btn_Start);
            _startBtn.SetSizeRequest(100, -1);
            _startBtn.Sensitive = false;

            ProgressBar progressBar = new() {
                Valign = Align.Center // 设置垂直居中
            };
            progressBar.Hide(); // 关键：初始状态不可见
            progressBar.Fraction = 0.0; // 初始进度为 0

            _startBtn.Clicked += async (s, e) => {
                // ① 先在主线程把控件状态取成局部变量。
                //    GTK 控件不是线程安全的，后台任务只能读这些快照，不能直接访问控件。
                string directoryPath = _pathLabel.Text;
                bool recursive = _recursiveCheck.Active;
                bool fastMode = _lossyCheck.Active;
                bool merge = _mergeCheck.Active;
                int layoutIndex = _layoutCombo.Active;

                _hintLabel.Text = Strings.Hint_Generating;
                // 切换为可见状态
                progressBar.Visible = true;
                progressBar.Fraction = 0.5;
                _startBtn.Sensitive = false;

                try {
                    var (failures, warnings) = await Task.Run(() =>
                        GeneratePdfs(directoryPath, recursive, fastMode, merge, layoutIndex));

                    progressBar.Fraction = 1.0;

                    if (failures.Count > 0) {
                        // 有错误时不能再无条件显示"已输出"，要让用户看到实际结果
                        SetLabelColor(_hintLabel, 200, 100, 0);
                        _hintLabel.Text = string.Format(Strings.Hint_GeneratedWithErrors, failures.Count);
                        // 回到 UI 线程统一展示错误，不再让后台线程逐个弹窗
                        MsgBox.Show(this,
                            string.Join(Environment.NewLine + Environment.NewLine, failures),
                            MessageType.Warning,
                            Strings.Msg_ErrorTitle);
                    }
                    else if (warnings.Count > 0) {
                        // 退出码为 0 说明 PDF 已经生成，这里只是个别图片被跳过，
                        // 不能报成生成失败（否则一张坏图就会让用户以为整本没出来）
                        SetLabelColor(_hintLabel, 200, 100, 0);
                        _hintLabel.Text = string.Format(Strings.Hint_GeneratedWithSkipped, warnings.Count);
                        MsgBox.Show(this,
                            string.Join(Environment.NewLine + Environment.NewLine, warnings),
                            MessageType.Info,
                            Strings.Msg_WarningTitle);
                    }
                    else {
                        SetLabelColor(_hintLabel, 138, 43, 226);
                        _hintLabel.Text = Strings.Hint_Done;
                    }
                }
                catch (Exception ex) {
                    progressBar.Fraction = 1.0;
                    SetLabelColor(_hintLabel, 200, 0, 0);
                    _hintLabel.Text = string.Format(Strings.Hint_Failed, ex.Message);
                    MsgBox.Show(this, ex.Message, MessageType.Error, Strings.Msg_ErrorTitle);
                }
                finally {
                    // 无论成功还是出错都恢复按钮可用状态，避免界面卡死
                    _startBtn.Sensitive = true;
                }
            };
            actionBox.PackStart(_startBtn, false, true, 20);

            actionBox.PackStart(progressBar, true, true, 20);

            bottomBox.PackStart(actionBox, false, false, 0);
            return bottomBox;
        }

        /// <summary>
        /// 同时运行的 Core 进程数硬上限：每个进程都是完整 .NET 运行时，
        /// 高核数机器上不设上限会一次拉起几十个进程。
        /// </summary>
        private const int MaxCoreProcessConcurrency = 8;

        /// <summary>
        /// 单个 Core 进程的峰值内存预算（含 .NET 运行时、SkiaSharp、
        /// 最大单张图片解码及编码缓冲）。按大图最坏情况估算，
        /// 实际峰值通常低于此值。
        /// </summary>
        private const long PerCoreProcessMemoryBudget = 1L << 30; // 1 GB

        /// <summary>
        /// 按当前可用内存估算允许同时运行的 Core 进程数，
        /// 图片很大时避免同时解码过多大图导致内存耗尽。
        /// </summary>
        private static int GetConcurrencyLimitByMemory() {
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
        /// 在后台生成 PDF；结果通过返回值收集，统一回到 UI 线程展示。
        /// 所有界面状态都由调用方在主线程快照后作为参数传入，
        /// 因此本方法内部不得访问任何 GTK 控件。
        /// </summary>
        /// <returns>
        /// (failures, warnings)：
        /// failures = Core 进程退出码非 0（真的没生成出来）；
        /// warnings = 退出码为 0 但 stderr 有内容（PDF 已生成，个别图片被跳过）。
        /// </returns>
        private static async Task<(List<string> failures, List<string> warnings)> GeneratePdfs(
                string directoryPath, bool recursive, bool fastMode, bool merge, int layoutIndex) {
            // 根据平台动态决定文件名
            string coreName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                              ? "ImgsToPDFCore.exe"
                              : "ImgsToPDFCore";
            string fileName = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Core", coreName);
            var failureQueue = new ConcurrentQueue<string>();
            var warningQueue = new ConcurrentQueue<string>();

            if (recursive && Directory.Exists(directoryPath)) {
                // 递归收集子目录可能较慢（大量文件夹），放到后台执行，
                // 否则含上千个子目录时点"开始"会让窗口出现"无响应"
                var dirs = await Task.Run(() => RecursiveFolder(directoryPath, []));

                // 并发 Core 进程数由任务量、CPU 线程数与可用内存综合决定：
                // - 任务很少时按任务数并发，避免白白拉起多余的完整 .NET 进程；
                // - 任务很多时按 CPU 线程数并发，避免进程间争抢 CPU；
                // - 同时按可用内存估算上限，图片很大时避免同时解码过多大图；
                // - MaxCoreProcessConcurrency 兜底，防止高核数机器一次拉起过多进程。
                int maxConcurrency = Math.Max(1, Math.Min(
                    Math.Min(Environment.ProcessorCount, dirs.Count),
                    Math.Min(GetConcurrencyLimitByMemory(), MaxCoreProcessConcurrency)));

                using var semaphore = new SemaphoreSlim(maxConcurrency);
                var tasks = dirs.Select(async dirPath => {
                    await semaphore.WaitAsync();
                    try {
                        var (_, stderr, exitCode) = await RunProcessAsync(fileName, BuildCoreArgs(dirPath, fastMode, layoutIndex));
                        CollectProcessResult(dirPath, stderr, exitCode, failureQueue, warningQueue);
                    }
                    finally {
                        semaphore.Release();
                    }
                }).ToList();
                await Task.WhenAll(tasks);

                if (merge) {
                    var (_, stderr, exitCode) = await RunProcessAsync(fileName, BuildCoreArgs(directoryPath, fastMode: false, layoutIndex: 0, mergePdfs: true));
                    CollectProcessResult(directoryPath, stderr, exitCode, failureQueue, warningQueue);
                }
            }
            else {
                var (_, stderr, exitCode) = await RunProcessAsync(fileName, BuildCoreArgs(directoryPath, fastMode, layoutIndex));
                CollectProcessResult(directoryPath, stderr, exitCode, failureQueue, warningQueue);
            }

            return ([.. failureQueue], [.. warningQueue]);
        }

        /// <summary>
        /// 归类一次 Core 进程的执行结果。
        /// 失败（退出码非 0）：stderr 为空时补上退出码，避免弹出内容为空的对话框；
        /// 警告（退出码为 0 但 stderr 有内容）：例如个别图片解码失败被跳过，PDF 本身已生成。
        /// 两种情况都会带上对应的目录/压缩包路径，并发处理多个目录时才能定位是谁出的问题。
        /// </summary>
        private static void CollectProcessResult(string targetPath, string stderr, int exitCode,
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
        /// 构造传给 Core 进程的命令行参数
        /// </summary>
        private static string[] BuildCoreArgs(string path, bool fastMode, int layoutIndex, bool mergePdfs = false) {
            if (mergePdfs) {
                return ["-d", path, "--merge-pdfs"];
            }
            var args = new List<string> { "-d", path, "-l", layoutIndex.ToString() };
            if (fastMode) {
                args.Add("--fast");
            }
            return [.. args];
        }
        static List<string> RecursiveFolder(string path, List<string> dirs) {
            dirs.Add(path);
            var TheFolder = new DirectoryInfo(path);
            foreach (var childFolder in TheFolder.GetDirectories()) {
                RecursiveFolder(childFolder.FullName, dirs);
            }
            return dirs;
        }
        /// <summary>
        /// 运行给定的命令，返回得到的标准输出及标准错误。
        /// stdout 与 stderr 必须同时异步读取，否则管道缓冲写满时会互相阻塞导致死锁。
        /// </summary>
        /// <param name="fileName">需要运行的指令</param>
        /// <returns>元组：(stdout:标准输出, stderr:标准错误, exitCode:进程退出码)</returns>
        private static async Task<(string stdout, string stderr, int exitCode)> RunProcessAsync(string fileName, string[] args) {
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
            p.StartInfo.CreateNoWindow = true;          // 设置不显示窗口
            p.StartInfo.WorkingDirectory = System.IO.Path.GetDirectoryName(fileName);
            p.Start();

            // stdout 与 stderr 必须同时异步读取，否则管道缓冲写满时会互相阻塞导致死锁
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdoutTask, stderrTask);
            await p.WaitForExitAsync();

            // 退出码用于区分"真的失败"（CommonUtils.ReportFailure 会把它设为 1）
            // 与"PDF 已生成、只是个别图片被跳过"（stderr 有内容但退出码为 0）
            return (stdoutTask.Result, stderrTask.Result, p.ExitCode);
        }

        /// <summary>
        /// 拖拽经过时的反馈：只按"目标类型"判断能否接收。
        /// 拖入内容不是 URI 列表（例如拖一段文字）时光标显示为禁止，
        /// 等价于原版 DragEnter 里把 e.Effect 设为 None。
        /// 具体路径是否支持，在 OnDragDataReceived 里判断并给出文字提示。
        /// </summary>
        private void OnDragMotion(object o, DragMotionArgs args) {
            // target_list 传 null 表示使用控件自己注册的目标列表
            Gdk.Atom target = Gtk.Drag.DestFindTarget(_dropTarget, args.Context, null);
            bool acceptable = target != null && !string.IsNullOrEmpty(target.Name);

            // Gdk# 的 DragAction 没有 0 值成员，但 GTK 里"不接受"就是 0
            Gdk.Drag.Status(args.Context, acceptable ? DragAction.Copy : (DragAction)0, args.Time);

            // 悬停期间用虚线边框给出"可放下"的正反馈；具体路径是否受支持
            // 要等 DragDataReceived 才能判断（悬停阶段拿不到 URI 列表）
            SetDropHighlight(acceptable);
            args.RetVal = true;
        }

        // 处理拖拽接收事件
        private void OnDragDataReceived(object o, DragDataReceivedArgs args) {
            SetDropHighlight(false);

            // 整个处理过程包在 try/catch 里：拖放源提供的内容不受本程序控制，
            // 任何意外异常都不应逃逸到 GTK 主循环（那会表现为界面卡死或崩溃）
            try {
                // 检查数据类型是否正确
                if (args.Info != 0) { args.RetVal = true; return; }

                // 获取拖拽的文件 URI 列表 (file://...)
                string[] uris = args.SelectionData.Uris;
                if (uris == null || uris.Length == 0) { args.RetVal = true; return; }

                // 用 TryCreate 而不是构造函数：从浏览器拖入选中的文本、或拖入
                // 虚拟文件时，uri-list 里可能不是合法 URI，构造函数会抛 UriFormatException
                if (!Uri.TryCreate(uris[0], UriKind.Absolute, out Uri? fileUri) || !fileUri.IsFile) {
                    NotifyInvalidDrop(Strings.Msg_InvalidPath);
                    args.RetVal = true;
                    return;
                }

                string folderPath = fileUri.LocalPath;

                // 检查拖入的是否为文件夹
                if (Directory.Exists(folderPath)) {
                    ProcessFolder(folderPath);
                }
                else if (File.Exists(folderPath)) {
                    string extension = System.IO.Path.GetExtension(folderPath);
                    if (_supportedCompressedExtensions.Contains(extension)) {
                        ProcessArchive(folderPath);
                    }
                    else {
                        // 原实现只写 Console.WriteLine，而 Windows 下 OutputType 是 WinExe（没有控制台），
                        // 用户完全看不到任何反馈
                        NotifyInvalidDrop(Strings.Drop_NotSupported);
                    }
                }
                else {
                    NotifyInvalidDrop(Strings.Drop_NotExist);
                }

                // 与原版一致只处理第一个拖入项，但要明确告知，避免用户以为全部都处理了
                if (uris.Length > 1) {
                    _hintLabel.Text = string.Format(Strings.Msg_MultipleDropped, uris.Length - 1);
                }
            }
            catch (Exception ex) {
                NotifyInvalidDrop(ex.Message);
            }

            args.RetVal = true; // 表示事件已处理
        }

        /// <summary>
        /// 拖入内容不可用时给出可见反馈（提示文字 + 对话框）。
        /// 必须同时清掉上一次的选择状态：否则会出现"提示说非法、路径标签却还是旧目录、
        /// 开始按钮还能点"的矛盾状态 —— 用户点下去会对旧目录生成 PDF。
        /// </summary>
        private void NotifyInvalidDrop(string message) {
            // ResetToInitialState 会把提示改回蓝色初始文案，所以顺序不能颠倒
            ResetToInitialState();
            SetPathLabel(Strings.Path_Waiting);
            SetLabelColor(_hintLabel, 200, 0, 0);
            _hintLabel.Text = message;
            MsgBox.Show(this, message, MessageType.Warning, Strings.Msg_ErrorTitle);
        }
        private void SelectFolder() {
            string selectedPath = null!;

            // 1. 创建文件夹选择对话框
            // 参数：标题, 父窗口, 模式 (SelectFolder), 按钮及其返回码
            using (FileChooserDialog dialog = new(
                Strings.Dialog_FolderTitle,
                this, // 如果在 Window 类内，传入 this；否则传入 null
                FileChooserAction.SelectFolder,
                Strings.Dialog_Cancel, ResponseType.Cancel,
                Strings.Dialog_OK, ResponseType.Accept)) {
                dialog.SetDefaultSize(800, 600);
                // 2. 运行对话框并获取用户操作结果
                if (dialog.Run() == (int)ResponseType.Accept) {
                    // 3. 获取选择的路径
                    selectedPath = dialog.Filename;
                }

                // 4. 对话框在 using 块结束时自动销毁
            }

            // 如果用户取消或未选择，直接返回，避免对 null 调用 Directory.Exists
            if (string.IsNullOrEmpty(selectedPath)) {
                return;
            }

            // 检查拖入的是否为文件夹
            if (Directory.Exists(selectedPath)) {
                ProcessFolder(selectedPath);
            }
        }

        private void SelectArchive() {
            string selectedPath = null!;

            using (FileChooserDialog dialog = new(
                Strings.Dialog_ArchiveTitle,
                this,
                FileChooserAction.Open,
                Strings.Dialog_Cancel, ResponseType.Cancel,
                Strings.Dialog_OK, ResponseType.Accept)) {
                dialog.SetDefaultSize(800, 600);

                // 添加文件过滤器
                FileFilter archiveFilter = new() {
                    Name = Strings.Filter_Archive
                };
                archiveFilter.AddPattern("*.zip");
                archiveFilter.AddPattern("*.rar");
                archiveFilter.AddPattern("*.7z");
                dialog.AddFilter(archiveFilter);

                FileFilter allFilter = new() {
                    Name = Strings.Filter_All
                };
                allFilter.AddPattern("*");
                dialog.AddFilter(allFilter);

                if (dialog.Run() == (int)ResponseType.Accept) {
                    selectedPath = dialog.Filename;
                }

                // 对话框在 using 块结束时自动销毁
            }

            if (string.IsNullOrEmpty(selectedPath)) {
                return;
            }

            if (File.Exists(selectedPath)) {
                string extension = System.IO.Path.GetExtension(selectedPath);
                if (_supportedCompressedExtensions.Contains(extension)) {
                    ProcessArchive(selectedPath);
                }
                else {
                    // "所有文件"过滤器让用户很容易选到非压缩包；不能静默什么都不做，
                    // 否则界面毫无反应，用户会以为程序卡住了
                    NotifyInvalidDrop(Strings.Msg_NotAnArchive);
                }
            }
            else {
                NotifyInvalidDrop(Strings.Drop_NotExist);
            }
        }

        // 处理文件夹：识别图片并更新 UI
        private async void ProcessFolder(string folderPath) {
            SetPathLabel(folderPath);

            try {
                // 关键：只要目录存在就允许开始，不要求本层必须有图片。
                // 递归模式的主要用法正是"选中只有子目录的父目录"，
                // 由 Core 进程逐个处理子目录（对应原版 ImgsToPDF.cs 对任何存在的目录都启用按钮）。
                _startBtn.Sensitive = true;

                var candidates = Directory.EnumerateFiles(folderPath)
                    .Where(file => _supportedExtensions.Contains(System.IO.Path.GetExtension(file)))
                    .OrderBy(file => file, StringComparer.Ordinal)
                    .ToList();

                if (candidates.Count > 0) {
                    // 预览解码是重活，放到后台线程，避免大图卡住界面
                    int requestId = ++_previewRequestId;
                    var preview = await Task.Run(() => LoadFirstUsablePreview(candidates, 420, 420));

                    // 解码期间用户可能又选了别的目录，丢弃过期结果
                    if (requestId != _previewRequestId) {
                        preview?.Dispose();
                        return;
                    }

                    if (preview != null) {
                        var oldPreview = _mainImage.Pixbuf;
                        _mainImage.Pixbuf = preview;
                        oldPreview?.Dispose();
                    }
                    else {
                        var oldGen = _mainImage.Pixbuf;
                        _mainImage.SetFromIconName("image-x-generic", IconSize.Dialog);
                        oldGen?.Dispose();
                    }

                    SetLabelColor(_hintLabel, 138, 43, 226);
                    _hintLabel.Text = Strings.Hint_Ready;
                    _smallFolderIcon.Show();
                }
                else {
                    // 本层没有直属图片。不要再调 ResetToInitialState()：
                    // 那会把刚刚启用的开始按钮又关掉，导致递归功能彻底不可达。
                    var oldIcon = _mainImage.Pixbuf;
                    _mainImage.SetFromIconName("folder", IconSize.Dialog);
                    oldIcon?.Dispose();
                    _smallFolderIcon.Hide();

                    bool hasSubDirs = Directory.EnumerateDirectories(folderPath).Any();
                    if (hasSubDirs) {
                        // 有子目录：引导用户改用递归，而不是断言"这里没东西"
                        SetLabelColor(_hintLabel, 138, 43, 226);
                        _hintLabel.Text = Strings.Hint_NoImagesUseRecursive;
                    }
                    else {
                        SetLabelColor(_hintLabel, 200, 0, 0);
                        _hintLabel.Text = Strings.Hint_NoImagesAtAll;
                    }
                }
            }
            catch (Exception ex) {
                MsgBox.Show(this, $"{Strings.Msg_ErrProcess}{ex.Message}");
                ResetToInitialState();
                SetPathLabel(Strings.Path_Waiting);
            }
        }

        /// <summary>
        /// 依次尝试解码候选图片，返回第一张成功的预览图。
        /// 对应原版 ChooseFileAction 里"坏图就试下一张、成功即 break"的循环 ——
        /// 只看第一张的话，首图损坏就会退化成通用图标，而目录里明明有能预览的图。
        /// 本方法在后台线程运行，不得访问任何 GTK 控件。
        /// </summary>
        private static Pixbuf? LoadFirstUsablePreview(IEnumerable<string> candidateFiles, int maxWidth, int maxHeight) {
            foreach (var file in candidateFiles) {
                var preview = TryLoadPreviewPixbuf(file, maxWidth, maxHeight);
                if (preview != null) {
                    return preview;
                }
            }
            return null;
        }

        /// <summary>
        /// 统一设置路径标签与提示气泡，避免超长路径把窗口撑变形（标签已设中间省略）。
        /// </summary>
        private void SetPathLabel(string text) {
            _pathLabel.Text = text;
            _pathLabel.TooltipText = text;
        }

        // 处理压缩包：更新 UI 状态
        private void ProcessArchive(string archivePath) {
            SetPathLabel(archivePath);
            _startBtn.Sensitive = true;

            // 作废可能仍在后台运行的目录预览，避免它稍后覆盖压缩包图标
            _previewRequestId++;

            // 释放旧的预览图
            var oldArchiveIcon = _mainImage.Pixbuf;
            // 显示归档图标
            _mainImage.SetFromIconName("package-x-generic", IconSize.Dialog);
            oldArchiveIcon?.Dispose();

            SetLabelColor(_hintLabel, 138, 43, 226); // 紫色
            _hintLabel.Text = Strings.Hint_Ready;

            // 压缩包不显示文件夹叠加图标
            _smallFolderIcon.Hide();
        }

        /// <summary>
        /// 回到"未选择任何内容"的初始状态。
        /// 注意：只有"清除选择"与错误回退才应调用它 —— ProcessFolder 在本层找不到
        /// 图片时不能调用，否则会把刚启用的开始按钮又关掉，使递归功能不可达。
        /// </summary>
        private void ResetToInitialState() {
            // 作废可能仍在后台运行的预览加载
            _previewRequestId++;

            // 释放旧的预览图再重置
            var oldResetIcon = _mainImage.Pixbuf;
            _mainImage.SetFromIconName("folder", IconSize.Dialog);
            oldResetIcon?.Dispose();

            SetLabelColor(_hintLabel, 0, 0, 255); // 蓝色
            _hintLabel.Text = Strings.Hint_Initial;
            _smallFolderIcon.Hide();

            // 重置 startBtn 状态（安全检查）
            _startBtn.Sensitive = false;
        }

        // 在“关于”菜单项的 Activated 事件中调用
        private void OnAboutClicked(object? sender, EventArgs e) {
            // 获取程序集信息
            var assembly = Assembly.GetExecutingAssembly();
            var versionStr = assembly.GetName().Version?.ToString() ?? "Unknown";
            var copyrightAttr = assembly
                .GetCustomAttributes(typeof(AssemblyCopyrightAttribute), false)
                .OfType<AssemblyCopyrightAttribute>()
                .FirstOrDefault();
            var copyright = copyrightAttr?.Copyright ?? string.Empty;

            // 创建对话框
            using var logoPixbuf = GetAppIcon();
            AboutDialog ad = new() {
                Logo = logoPixbuf,
                ProgramName = "ImagesToPDF",
                Version = versionStr,
                Copyright = copyright,
                Website = "https://github.com/Sinryou/ImagesToPDF",
                License = "Under MIT License\n\n" + copyright,
                TransientFor = this // 设置父窗口
            };

            ad.Run();
            ad.Destroy();
        }
        private void SetLabelColor(Label label, byte r, byte g, byte b) {
            if (_hintStyleProvider != null) {
                label.StyleContext.RemoveProvider(_hintStyleProvider);
                _hintStyleProvider.Dispose();
            }
            _hintStyleProvider = new CssProvider();
            _hintStyleProvider.LoadFromData($"label {{ color: rgb({r},{g},{b}); }}");
            label.StyleContext.AddProvider(_hintStyleProvider, Gtk.StyleProviderPriority.User);
        }

        private static Pixbuf? TryLoadPreviewPixbuf(string imagePath, int maxWidth, int maxHeight) {
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

        private static Pixbuf? GetAppIcon(int targetWidth = 64, int targetHeight = 64) {
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

        /// <summary>
        /// 语言切换会重启进程，而新实例经常在旧实例还没退出时就启动完毕，
        /// 此时它拿不到单实例互斥体。用这个参数区分"重启出来的实例"与"用户重复启动"。
        /// </summary>
        private const string RestartArgument = "--restarted";

        /// <summary>
        /// 重启实例等待旧实例释放单实例互斥体的最长时间
        /// </summary>
        private const int RestartWaitMilliseconds = 5000;

        private static void RestartApplication() {
            string? fileName = Environment.ProcessPath;
            if (string.IsNullOrEmpty(fileName)) {
                return;
            }

            // 不用 using：Process.Start 返回后新进程仍在运行，立即 Dispose 语义不正确
            Process.Start(new ProcessStartInfo {
                FileName = fileName,
                Arguments = RestartArgument,
                UseShellExecute = true
            });

            Application.Quit();
            Environment.Exit(0);
        }

        [STAThread]
        public static void Main(string[] args) {
            // 命令行开关按 Windows 惯例不区分大小写
            bool restarted = args.Contains(RestartArgument, StringComparer.OrdinalIgnoreCase);

            using Mutex mutex = new(true, @"GTK_ImgsToPDF", out bool isFirstInstance);

            if (!isFirstInstance) {
                // 普通重复启动：直接退出
                if (!restarted) {
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
                [DllImport("kernel32.dll", SetLastError = true)]
                static extern bool SetDllDirectory(string lpPathName);
                SetDllDirectory(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtime"));
            }

            Application.Init();
            _ = new ImgsToPDF();
            Application.Run();
        }
    }
}
