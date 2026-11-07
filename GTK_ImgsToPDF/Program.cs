using Gdk;
using Gtk;
using GTK_ImgsToPDF.Config;
using GTK_ImgsToPDF.Localization;
using ImgsToPDF.Shared;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using SkiaSharp;
// Gtk.Layout 是 GTK 的容器控件，与共享契约里的版式枚举同名；
// 用别名固定指向共享定义，避免文件内每一处都写全限定名。
using Layout = ImgsToPDF.Shared.Layout;

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
        private ProgressBar _progressBar = null!;
        private CheckButton _lossyCheck = null!;
        private CheckButton _recursiveCheck = null!;
        private CheckButton _mergeCheck = null!;
        private ComboBoxText _layoutCombo = null!;
        private MenuItem _openFolderItem = null!;
        private MenuItem _openArchiveItem = null!;
        private MenuItem _chooseImagesItem = null!;
        private MenuItem _choosePdfsItem = null!;
        private MenuItem _clearChosenItem = null!;
        private MenuItem _configFileItem = null!;
        private MenuItem _langItem = null!;
        private bool _isProcessing;

        /// <summary>
        /// 当前目标选择（模式 + 载荷，不可变、整体替换）。
        /// 唯一转移入口是 <see cref="SetSelection"/>；所有模式判定只读
        /// <see cref="SelectionState"/> 的谓词，不再从路径标签文本反推状态。
        /// </summary>
        private SelectionState _selection = SelectionState.Empty;

        /// <summary>
        /// UpdateControlStates 的重入保护：方法内部设置 Active 会触发 Toggled
        /// 递归调用自身，用标志拦下重复推导
        /// </summary>
        private bool _isSyncingControlStates;

        /// <summary>
        /// 窗口是否已确认关闭，用于阻止关闭瞬间排队中的任务继续拉起新子进程
        /// </summary>
        private volatile bool _isClosing;

        /// <summary>
        /// 避免并发的 Core 进程同时弹出多个密码输入窗口，将密码输入串行化
        /// </summary>
        private readonly SemaphoreSlim _passwordPromptLock = new(1, 1);

        // 版式下拉框的取值：与 AppendText 的顺序一一对应，
        // 传参时直接用枚举名而不是下拉框索引，避免"界面顺序"与"枚举数值"耦合。
        private static readonly Layout[] LayoutChoices = [
            Layout.Single,
            Layout.DuplexLeftToRight,
            Layout.DuplexRightToLeft
        ];

        private CssProvider? _hintStyleProvider;

        /// <summary>
        /// 预览图加载的请求序号。预览解码在后台线程进行，用户可能在解码完成前
        /// 又选了别的目录；用自增序号作废过期结果，避免慢加载覆盖新选择。
        /// </summary>
        private int _previewRequestId;

        /// <summary>
        /// 拖放区光标：空闲时用手型（对应原版 PicInFolder.Cursor = SystemHandCursor），
        /// 处理中换回默认箭头。Gdk 光标是原生资源，进程内共用一份，不做释放。
        /// </summary>
        private Gdk.Cursor? _handCursor;
        private Gdk.Cursor? _arrowCursor;

        static ImgsToPDF() {
            NativeLibrary.SetDllImportResolver(typeof(SKImage).Assembly, ResolveSkiaSharpNativeLibrary);
        }

        /// <summary>
        /// 将 GUI 的 SkiaSharp 原生库解析重定向到 Core 子目录，直接复用 Core\libSkiaSharp，
        /// 避免 GUI 根目录与 Core\ 下各存一份完全相同的原生库。
        /// </summary>
        private static IntPtr ResolveSkiaSharpNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) {
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
            this.DeleteEvent += (s, e) => {
                if (!ConfirmExit()) {
                    e.RetVal = true;
                    return;
                }
                _isClosing = true;
                Application.Quit();
            };

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

            // 初始状态下隐藏叠加的小图标与进度条
            _smallFolderIcon.Hide();
            _progressBar.Hide();
        }

        private bool ConfirmExit() {
            if (!_isProcessing) {
                return true;
            }
            return MsgBox.Confirm(this, Strings.Msg_WarnGenerating, Strings.Msg_WarningTitle);
        }

        private MenuBar CreateMenuBar() {
            MenuBar menuBar = [];

            // useUnderline: true 让 GTK 把文案里的下划线解析为 Alt 助记符
            // （对应原版 WinForms 的 &F / &O / &Z …）
            MenuItem fileMenu = new(Strings.Menu_File) { UseUnderline = true };
            Menu fileSub = [];

            _openFolderItem = new(Strings.Menu_OpenFolder) { UseUnderline = true };
            _openFolderItem.Activated += (s, e) => SelectFolder();
            fileSub.Append(_openFolderItem);

            _openArchiveItem = new(Strings.Menu_OpenArchive) { UseUnderline = true };
            _openArchiveItem.Activated += (s, e) => SelectArchive();
            fileSub.Append(_openArchiveItem);

            _chooseImagesItem = new(Strings.Menu_ChooseImages) { UseUnderline = true };
            _chooseImagesItem.Activated += (s, e) => SelectImages();
            fileSub.Append(_chooseImagesItem);

            _choosePdfsItem = new(Strings.Menu_ChoosePdfs) { UseUnderline = true };
            _choosePdfsItem.Activated += (s, e) => SelectPdfs();
            fileSub.Append(_choosePdfsItem);

            _clearChosenItem = new(Strings.Menu_ClearSelection) { UseUnderline = true };
            _clearChosenItem.Activated += (s, e) => {
                if (_isProcessing) {
                    return;
                }
                SetPathLabel(Strings.Path_Waiting);
                ResetToInitialState();
            };
            fileSub.Append(_clearChosenItem);

            fileSub.Append(new SeparatorMenuItem());

            MenuItem quitItem = new(Strings.Menu_Exit) { UseUnderline = true };
            quitItem.Activated += (s, e) => {
                if (ConfirmExit()) {
                    _isClosing = true;
                    Application.Quit();
                }
            };
            fileSub.Append(quitItem);
            fileMenu.Submenu = fileSub;

            menuBar.Append(fileMenu);

            _configFileItem = new(Strings.Menu_Config) { UseUnderline = true };
            _configFileItem.Activated += (s, e) => {
                if (_isProcessing) {
                    return;
                }
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
            menuBar.Append(_configFileItem);

            _langItem = new(Strings.Menu_Lang) { UseUnderline = true };
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

            _langItem.Submenu = langSub;
            menuBar.Append(_langItem);

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
            _dropTarget.ButtonPressEvent += (o, args) => {
                if (_isProcessing) {
                    return;
                }
                if (args.Event.Button == 1 && args.Event.Type == EventType.ButtonPress) {
                    SelectFolder();
                }
            };

            // 控件真正落到 GdkWindow 之后才能设置光标，构造阶段窗口还不存在
            _dropTarget.Realized += (_, _) => {
                _arrowCursor ??= CreateCursor("default", Gdk.CursorType.LeftPtr);
                _handCursor ??= CreateCursor("pointer", Gdk.CursorType.Hand2);
                UpdateDropTargetCursor();
            };

            // 高亮样式只需注册一次；AddProviderForScreen 是静态方法，无需保留字段
            Gtk.StyleContext.AddProviderForScreen(Gdk.Screen.Default, CreateDropHighlightProvider(), Gtk.StyleProviderPriority.User);

            return _dropTarget;
        }

        /// <summary>
        /// 按"是否忙碌"切换拖放区光标：空闲时手型（提示这一区域可点击选目录），
        /// 处理中恢复默认箭头（此时点击无效，手型会误导用户）。
        /// 对应原版在 UpdateControlStates 里设置 PicInFolder.Cursor 的做法。
        /// </summary>
        private void UpdateDropTargetCursor() {
            Gdk.Window? window = _dropTarget?.Window;
            if (window == null) {
                return;   // 尚未 Realize，等 Realized 事件回调
            }
            Gdk.Cursor? cursor = _isProcessing ? _arrowCursor : _handCursor;
            if (cursor != null) {
                window.Cursor = cursor;
            }
        }

        /// <summary>
        /// 按主题名创建光标，主题里没有该名字时退回内置的光标类型；
        /// 两者都失败返回 null（调用方保持原光标不变）。
        /// </summary>
        private static Gdk.Cursor? CreateCursor(string themeName, Gdk.CursorType fallbackType) {
            Gdk.Display? display = Gdk.Display.Default;
            if (display == null) {
                return null;
            }
            try {
                return new Gdk.Cursor(display, themeName);
            }
            catch {
                try {
                    // 内置类型是从 GTK2 沿用的旧接口，仅在主题名缺失时兜底
#pragma warning disable CS0612
                    return new Gdk.Cursor(fallbackType);
#pragma warning restore CS0612
                }
                catch {
                    return null;
                }
            }
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

        /// <summary>
        /// 全局设置界面忙碌/就绪状态，锁定用户输入以防止竞态条件。
        /// 控件的可用状态由 <see cref="UpdateControlStates"/> 统一推导，此处不直接触碰控件。
        /// </summary>
        private void SetUiState(bool isProcessing) {
            _isProcessing = isProcessing;
            UpdateControlStates();
        }

        /// <summary>
        /// 目标选择的唯一转移入口：整体替换 <see cref="_selection"/> 并渲染由它派生的
        /// 路径标签与悬停提示，最后经状态同步点刷新控件可用性。
        /// 昂贵的派生（首图扫描、缩略图解码）仍由各动作方法自行完成，不在此处执行。
        /// </summary>
        private void SetSelection(SelectionState selection) {
            _selection = selection ?? SelectionState.Empty;

            (string? text, string? toolTip) = _selection.Describe();
            SetPathLabel(string.IsNullOrEmpty(text) ? Strings.Path_Waiting : text);
            _pathLabel.TooltipText = string.IsNullOrEmpty(toolTip) ? Strings.Path_Waiting : toolTip;

            UpdateControlStates();
        }

        /// <summary>
        /// 全局唯一的控件状态同步点：从「选择模式 + 忙碌状态」推导全部控件的
        /// Enabled，并维护三条不变量：
        /// 1. 递归子目录与合并子PDF 仅对目录模式有意义，压缩包与合并模式下一律禁用并取消勾选；
        /// 2. 合并子PDF 依赖递归子目录，后者取消勾选时前者随之取消；
        /// 3. 版式与快速压缩对 PDF 合并无意义（合并只搬移页面，不重排版、不重编码）。
        /// 任何改变上述状态的代码（选择目标、开始/结束处理、清除、用户勾选）完成后都必须调用
        /// 本方法；禁止在调用方手工零散设置控件 Sensitive/Active，避免状态同步逻辑漂移。
        /// </summary>
        private void UpdateControlStates() {
            if (_isSyncingControlStates) {
                return;
            }
            _isSyncingControlStates = true;
            try {
                bool isProcessing = _isProcessing;
                SelectionState selection = _selection;
                bool isMergeMode = selection.IsImageMerge || selection.IsPdfMerge;

                // 递归与合并子目录仅对路径模式下的真实文件夹生效；压缩包与两种合并模式一律禁用
                // （压缩包由 Core 侧固定按「整体解压 + 递归生成单个 PDF」处理）。
                // IsRealDirectory/IsArchiveFile 每次实时重判文件系统，目录被删后勾选随之收敛
                bool supportsRecursion = !isMergeMode
                    && (selection.Mode == SelectionMode.None || !selection.IsArchiveFile);

                _recursiveCheck.Sensitive = !isProcessing && supportsRecursion;
                if (!supportsRecursion) {
                    _recursiveCheck.Active = false;
                }

                _mergeCheck.Sensitive = !isProcessing && supportsRecursion && _recursiveCheck.Active;
                if (!supportsRecursion || !_recursiveCheck.Active) {
                    _mergeCheck.Active = false;
                }

                _layoutCombo.Sensitive = !isProcessing && !selection.IsPdfMerge;
                _lossyCheck.Sensitive = !isProcessing && !selection.IsPdfMerge;

                // 无效路径时 IsStartable 为 false，开始按钮自然禁用，不会把无效路径传给 Core
                _startBtn.Sensitive = !isProcessing && selection.IsStartable;

                _openFolderItem.Sensitive = !isProcessing;
                _openArchiveItem.Sensitive = !isProcessing;
                _chooseImagesItem.Sensitive = !isProcessing;
                _choosePdfsItem.Sensitive = !isProcessing;
                _clearChosenItem.Sensitive = !isProcessing;
                _configFileItem.Sensitive = !isProcessing;
                _langItem.Sensitive = !isProcessing;

                UpdateDropTargetCursor();
            }
            finally {
                _isSyncingControlStates = false;
            }
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
            // 联动逻辑统一在 UpdateControlStates 里推导，这里只把"用户动过"这件事通知过去
            _recursiveCheck.Toggled += (s, e) => UpdateControlStates();

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

            _progressBar = new() {
                Valign = Align.Center // 设置垂直居中
            };
            _progressBar.Hide(); // 关键：初始状态不可见
            _progressBar.Fraction = 0.0; // 初始进度为 0

            _startBtn.Clicked += async (s, e) => {
                if (_isProcessing) {
                    return;
                }

                // ① 先在主线程把选择与控件状态取成局部变量。
                //    GTK 控件不是线程安全的，后台任务只能读这些快照，不能直接访问控件。
                SelectionState selection = _selection;
                if (!selection.IsStartable) {
                    return;
                }

                string? manifestPath = null;
                string targetPathForCore;
                string displayTargetPath;
                bool recursive;
                bool merge;
                bool fastMode = !selection.IsPdfMerge && _lossyCheck.Active;
                // 传枚举名而不是索引：界面顺序调整不会改变 Core 的解释
                Layout layout = selection.IsPdfMerge ? Layout.Single : LayoutChoices[_layoutCombo.Active];

                if (selection.IsImageMerge || selection.IsPdfMerge) {
                    // 合并模式：把文件列表写成临时清单交给 Core，规避命令行长度上限。
                    // 输出名沿用既有约定：图片合并 = 首个文件所在目录名.pdf；
                    // PDF 合并 = 首个文件所在目录名_Merged.pdf。
                    IReadOnlyList<string> files = selection.Files!;
                    string firstFile = files[0];
                    string outDir = System.IO.Path.GetDirectoryName(firstFile) ?? string.Empty;
                    string baseName = System.IO.Path.GetFileName(outDir);
                    if (string.IsNullOrEmpty(baseName)) {
                        baseName = System.IO.Path.GetFileNameWithoutExtension(firstFile);
                    }
                    displayTargetPath = System.IO.Path.Combine(
                        outDir, selection.IsPdfMerge ? $"{baseName}_Merged.pdf" : $"{baseName}.pdf");

                    manifestPath = System.IO.Path.Combine(
                        System.IO.Path.GetTempPath(), $"imgstopdf_{Guid.NewGuid():N}.txt");
                    var lines = new List<string> { IpcContracts.ManifestOutputPrefix + displayTargetPath };
                    lines.AddRange(files);
                    File.WriteAllLines(manifestPath, lines,
                        new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    targetPathForCore = manifestPath;

                    recursive = false;   // 合并模式不存在"递归子目录"的语义
                    merge = false;
                }
                else {
                    targetPathForCore = selection.TargetPath!;
                    displayTargetPath = selection.TargetPath!;
                    recursive = _recursiveCheck.Active;
                    merge = _mergeCheck.Active;
                }

                _hintLabel.Text = Strings.Hint_Generating;
                // 切换为可见状态并锁定控件
                _progressBar.Visible = true;
                _progressBar.Fraction = 0.5;
                SetUiState(true);

                try {
                    var (failures, warnings) = await Task.Run(() =>
                        GeneratePdfs(targetPathForCore, recursive, fastMode, merge, layout, displayTargetPath));
                    if (_isClosing) return;

                    _progressBar.Fraction = 1.0;

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
                    if (_isClosing) return;

                    _progressBar.Fraction = 1.0;
                    SetLabelColor(_hintLabel, 200, 0, 0);
                    _hintLabel.Text = string.Format(Strings.Hint_Failed, ex.Message);
                    MsgBox.Show(this, ex.Message, MessageType.Error, Strings.Msg_ErrorTitle);
                }
                finally {
                    // 临时清单只在本次任务生命周期内有效，无论成败都要删掉
                    if (manifestPath != null) {
                        try {
                            if (File.Exists(manifestPath)) {
                                File.Delete(manifestPath);
                            }
                        }
                        catch (Exception ex) {
                            Debug.WriteLine($"[ImgsToPDF] Failed to delete temp manifest '{manifestPath}': {ex.Message}");
                        }
                    }

                    if (!_isClosing) {
                        // 无论成功还是出错都恢复控件可用状态，避免界面卡死
                        SetUiState(false);
                    }
                }
            };
            actionBox.PackStart(_startBtn, false, true, 20);

            actionBox.PackStart(_progressBar, true, true, 20);

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
        private async Task<(List<string> failures, List<string> warnings)> GeneratePdfs(
                string directoryPath, bool recursive, bool fastMode, bool merge, Layout layout, string displayTargetPath) {
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
                        if (_isClosing) return;
                        var (_, stderr, exitCode) = await RunProcessAsync(fileName, BuildCoreArgs(dirPath, fastMode, layout), PromptPasswordOnUiAsync);
                        CollectProcessResult(dirPath, stderr, exitCode, failureQueue, warningQueue);
                    }
                    finally {
                        semaphore.Release();
                    }
                }).ToList();
                await Task.WhenAll(tasks);

                if (merge && !_isClosing) {
                    var (_, stderr, exitCode) = await RunProcessAsync(fileName, BuildCoreArgs(directoryPath, fastMode: false, layout: Layout.Single, mergePdfs: true), PromptPasswordOnUiAsync);
                    CollectProcessResult(directoryPath, stderr, exitCode, failureQueue, warningQueue);
                }
            }
            else if (!_isClosing) {
                var (_, stderr, exitCode) = await RunProcessAsync(fileName, BuildCoreArgs(directoryPath, fastMode, layout), PromptPasswordOnUiAsync);
                CollectProcessResult(reportedTarget, stderr, exitCode, failureQueue, warningQueue);
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
        private static string[] BuildCoreArgs(string path, bool fastMode, Layout layout, bool mergePdfs = false) {
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
        /// 在 UI 线程弹出密码输入框并等待用户输入或取消。
        /// Core 子进程通过 stdout 的 ::__REQ_PASSWORD__:: 指令发起请求，
        /// 这里把结果（或取消指令）回写到它的 stdin。
        /// </summary>
        /// <param name="archivePath">需要解压的压缩包路径</param>
        /// <returns>用户输入的密码；取消或窗口正在关闭时返回 null</returns>
        private async Task<string?> PromptPasswordOnUiAsync(string archivePath) {
            if (_isClosing) {
                return null;
            }

            await _passwordPromptLock.WaitAsync();
            try {
                if (_isClosing) {
                    return null;
                }

                var tcs = new TaskCompletionSource<string?>();
                // GTK 控件只能在主线程操作：Application.Invoke 把弹窗排进主循环，
                // 后台读取线程在此 await，不阻塞主线程
                Application.Invoke((_, _) => {
                    try {
                        if (_isClosing) {
                            tcs.TrySetResult(null);
                            return;
                        }
                        using var dialog = new PasswordDialog(archivePath, this);
                        tcs.TrySetResult(dialog.Run() == (int)ResponseType.Ok ? dialog.Password : null);
                    }
                    catch (Exception ex) {
                        tcs.TrySetException(ex);
                    }
                });
                return await tcs.Task;
            }
            finally {
                _passwordPromptLock.Release();
            }
        }

        /// <summary>
        /// 运行给定的命令，返回标准输出、标准错误与退出码。
        /// 监听标准输出并拦截密码请求指令（::__REQ_PASSWORD__::），
        /// 通过回调在界面线程弹出密码框，再把结果经标准输入回传。
        /// stdout 与 stderr 必须同时异步读取，否则管道缓冲写满时会互相阻塞导致死锁。
        /// </summary>
        /// <param name="fileName">需要运行的指令</param>
        /// <param name="args">命令行参数列表</param>
        /// <param name="promptPasswordAsync">密码输入交互回调</param>
        /// <returns>元组：(stdout:标准输出, stderr:标准错误, exitCode:进程退出码)</returns>
        private static async Task<(string stdout, string stderr, int exitCode)> RunProcessAsync(
                string fileName, string[] args, Func<string, Task<string?>> promptPasswordAsync) {
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
                            string archivePath = line.Substring(IpcContracts.ReqPasswordPrefix.Length);
                            string? password = await promptPasswordAsync(archivePath);
                            try {
                                if (password != null) {
                                    // 密码可能含换行/引号等字符，先 UTF-8 + Base64 再走单行管道
                                    string base64 = Convert.ToBase64String(
                                        System.Text.Encoding.UTF8.GetBytes(password));
                                    await p.StandardInput.WriteLineAsync($"{IpcContracts.RespPasswordPrefix}{base64}");
                                }
                                else {
                                    await p.StandardInput.WriteLineAsync(IpcContracts.CancelToken);
                                }
                                await p.StandardInput.FlushAsync();
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

            // 退出码用于区分"真的失败"（CommonUtils.ReportFailure 会把它设为 1）
            // 与"PDF 已生成、只是个别图片被跳过"（stderr 有内容但退出码为 0）
            return (outputs[0], outputs[1], p.ExitCode);
        }

        /// <summary>
        /// 拖拽经过时的反馈：只按"目标类型"判断能否接收。
        /// 拖入内容不是 URI 列表（例如拖一段文字）时光标显示为禁止，
        /// 等价于原版 DragEnter 里把 e.Effect 设为 None。
        /// 具体路径是否支持，在 OnDragDataReceived 里判断并给出文字提示。
        /// </summary>
        private void OnDragMotion(object o, DragMotionArgs args) {
            if (_isProcessing) {
                Gdk.Drag.Status(args.Context, (DragAction)0, args.Time);
                SetDropHighlight(false);
                args.RetVal = true;
                return;
            }

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
            if (_isProcessing) {
                args.RetVal = true;
                return;
            }

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
                var paths = new List<string>(uris.Length);
                foreach (string uri in uris) {
                    if (Uri.TryCreate(uri, UriKind.Absolute, out Uri? fileUri) && fileUri.IsFile) {
                        paths.Add(fileUri.LocalPath);
                    }
                }
                if (paths.Count == 0) {
                    NotifyInvalidDrop(Strings.Msg_InvalidPath);
                    args.RetVal = true;
                    return;
                }

                // 按内容类型分流：纯图片组、纯 PDF 组各走合并模式，其余按容器（目录/压缩包）处理
                var imageFiles = paths
                    .Where(f => File.Exists(f) && ImageContracts.ImageExtensions.Contains(System.IO.Path.GetExtension(f)))
                    .ToList();
                var pdfFiles = paths
                    .Where(f => File.Exists(f) && string.Equals(System.IO.Path.GetExtension(f), ".pdf", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var containers = paths
                    .Where(f => Directory.Exists(f) || ImageContracts.ArchiveExtensions.Contains(System.IO.Path.GetExtension(f)))
                    .ToList();

                if (pdfFiles.Count > 0 && imageFiles.Count == 0 && containers.Count == 0) {
                    ChoosePdfsAction(pdfFiles);
                    return;
                }
                if (imageFiles.Count > 0 && containers.Count == 0) {
                    ChooseImagesAction(imageFiles);
                    return;
                }

                // 混合拖入或单个容器：仍按第一项处理，但要明确告知其余项被忽略
                string firstPath = paths[0];
                if (Directory.Exists(firstPath) || FolderScanner.ContainsSupportedImage(firstPath)) {
                    ChooseFileAction(firstPath);
                }
                else if (File.Exists(firstPath)) {
                    if (ImageContracts.ArchiveExtensions.Contains(System.IO.Path.GetExtension(firstPath))) {
                        ChooseFileAction(firstPath);
                    }
                    else {
                        NotifyInvalidDrop(Strings.Drop_NotSupported);
                        args.RetVal = true;
                        return;
                    }
                }
                else {
                    NotifyInvalidDrop(Strings.Drop_NotExist);
                    args.RetVal = true;
                    return;
                }

                if (paths.Count > 1) {
                    _hintLabel.Text = string.Format(Strings.Msg_MultipleDropped, paths.Count - 1);
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
            SetLabelColor(_hintLabel, 200, 0, 0);
            _hintLabel.Text = message;
            MsgBox.Show(this, message, MessageType.Warning, Strings.Msg_ErrorTitle);
        }
        private void SelectFolder() {
            if (_isProcessing) {
                return;
            }

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
                ChooseFileAction(selectedPath);
            }
        }

        /// <summary>
        /// 选择多个离散图片文件，合并输出为一个 PDF（进入图片合并模式）。
        /// 输出位置与拖入多张图片时一致：首张图片所在目录下的「目录名.pdf」。
        /// </summary>
        private void SelectImages() {
            if (_isProcessing) {
                return;
            }
            List<string>? files = PickFiles(
                Strings.Dialog_ImagesTitle, Strings.Filter_Images, ImageContracts.ImageExtensions);
            if (files == null || files.Count == 0) {
                return;
            }
            ChooseImagesAction(files);
        }

        /// <summary>
        /// 选择多个 PDF 文件，按文件名顺序合并为一个 PDF（进入 PDF 合并模式）。
        /// 输出位置与拖入多个 PDF 时一致：首个 PDF 所在目录下的「目录名_Merged.pdf」。
        /// </summary>
        private void SelectPdfs() {
            if (_isProcessing) {
                return;
            }
            List<string>? files = PickFiles(
                Strings.Dialog_PdfsTitle, Strings.Filter_Pdfs, [".pdf"]);
            if (files == null || files.Count == 0) {
                return;
            }
            ChoosePdfsAction(files);
        }

        /// <summary>
        /// 弹出多选文件对话框并返回用户选中的文件列表；用户取消时返回 null。
        /// 筛选器用的扩展名由调用方传入（图片侧来自 <see cref="ImageContracts"/>），
        /// 避免对话框允许选择的类型与 Core 实际支持的类型各自维护、逐渐漂移。
        /// </summary>
        private List<string>? PickFiles(string title, string filterName, IEnumerable<string> extensions) {
            using var dialog = new FileChooserDialog(
                title,
                this,
                FileChooserAction.Open,
                Strings.Dialog_Cancel, ResponseType.Cancel,
                Strings.Dialog_OK, ResponseType.Accept) {
                SelectMultiple = true   // 合并模式的前提就是一次选中多个文件
            };
            dialog.SetDefaultSize(800, 600);

            // 排序只是为了让筛选器里的通配符顺序稳定，与文件本身的排序无关
            var filter = new FileFilter { Name = filterName };
            foreach (string ext in extensions.OrderBy(e => e, StringComparer.OrdinalIgnoreCase)) {
                filter.AddPattern("*" + ext);
            }
            dialog.AddFilter(filter);

            if (dialog.Run() != (int)ResponseType.Accept) {
                return null;
            }
            return [.. dialog.Filenames];
        }

        private void SelectArchive() {
            if (_isProcessing) {
                return;
            }

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
                if (ImageContracts.ArchiveExtensions.Contains(extension)) {
                    ChooseFileAction(selectedPath);
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

        /// <summary>
        /// 选中一个目录或压缩包路径（点击预览区、打开文件夹/压缩包菜单与拖放兜底的共用实现）。
        /// 路径是否有效由 SelectionState 读取时判定，这里只按分类准备预览图，
        /// 标签与控件可用性交给 SetSelection 统一渲染。
        /// </summary>
        private void ChooseFileAction(string path) {
            var selection = SelectionState.FromPath(path);
            SetSelection(selection);

            if (selection.IsRealDirectory) {
                _ = UpdateFolderPreviewAsync(path);
            }
            else if (selection.IsArchiveFile) {
                ShowArchivePreview();
                SetLabelColor(_hintLabel, 138, 43, 226);
                _hintLabel.Text = Strings.Hint_Ready;
            }
            else {
                ShowNoPhotoPreview();
                SetLabelColor(_hintLabel, 200, 0, 0);
                _hintLabel.Text = Strings.Hint_NoImagesAtAll;
            }
        }

        /// <summary>
        /// 选中一组离散图片文件，进入“多图合并为一个 PDF”模式。
        /// </summary>
        private async void ChooseImagesAction(List<string> imageFiles) {
            if (imageFiles == null || imageFiles.Count == 0) {
                return;
            }

            // 这里的排序只决定预览用哪张图；最终页序由 Core 侧按 config.lua 的比较器重排。
            // 用自然排序与资源管理器一致：img2 在 img10 之前。
            imageFiles.Sort(FolderScanner.NaturalPathComparer.Instance);
            SetSelection(SelectionState.FromImageFiles(imageFiles));

            int requestId = ++_previewRequestId;
            var preview = await Task.Run(() => LoadFirstUsablePreview(imageFiles, 420, 420));
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
                ShowNoPhotoPreview();
            }
            _smallFolderIcon.Hide();

            SetLabelColor(_hintLabel, 138, 43, 226);
            _hintLabel.Text = Strings.Hint_Ready;
        }

        /// <summary>
        /// 选中一组离散 PDF 文件，进入“多 PDF 合并为一个 PDF”模式。
        /// </summary>
        private void ChoosePdfsAction(List<string> pdfFiles) {
            if (pdfFiles == null || pdfFiles.Count == 0) {
                return;
            }

            // 这里的排序只决定标签展示；最终合并顺序由 Core 侧按 config.lua 的比较器重排
            pdfFiles.Sort(FolderScanner.NaturalPathComparer.Instance);
            SetSelection(SelectionState.FromPdfFiles(pdfFiles));

            // 作废可能仍在后台运行的目录预览，避免它稍后覆盖 PDF 图标
            _previewRequestId++;
            ShowIconPreview("x-office-document");
            _smallFolderIcon.Hide();

            SetLabelColor(_hintLabel, 138, 43, 226);
            _hintLabel.Text = Strings.Hint_Ready;
        }

        /// <summary>
        /// 目录预览：本层有图片就取首张可用图做缩略图；没有则给出“改用递归”或“这里没图片”的提示。
        /// 只要目录存在就允许开始 —— 递归模式的主要用法正是“选中只有子目录的父目录”，
        /// 由 Core 逐个处理子目录。
        /// </summary>
        private async Task UpdateFolderPreviewAsync(string folderPath) {
            try {
                var candidates = Directory.EnumerateFiles(folderPath)
                    .Where(file => ImageContracts.ImageExtensions.Contains(System.IO.Path.GetExtension(file)))
                    .OrderBy(file => file, FolderScanner.NaturalPathComparer.Instance)
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
                        ShowIconPreview("image-x-generic");
                    }

                    SetLabelColor(_hintLabel, 138, 43, 226);
                    _hintLabel.Text = Strings.Hint_Ready;
                    _smallFolderIcon.Show();
                }
                else {
                    ShowIconPreview("folder");
                    _smallFolderIcon.Hide();

                    bool hasSubDirs = Directory.EnumerateDirectories(folderPath).Any();
                    if (hasSubDirs) {
                        // 有子目录：引导用户改用递归，而不是断言“这里没东西”
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
            }
        }

        /// <summary>压缩包预览：固定显示归档图标，不叠加角标。</summary>
        private void ShowArchivePreview() {
            _previewRequestId++;   // 作废可能仍在后台运行的目录预览
            ShowIconPreview("package-x-generic");
            _smallFolderIcon.Hide();
        }

        private void ShowNoPhotoPreview() {
            _previewRequestId++;
            ShowIconPreview("image-missing");
            _smallFolderIcon.Hide();
        }

        /// <summary>
        /// 用主题图标替换预览图。必须显式释放旧 Pixbuf：
        /// Gdk# 的 Pixbuf 是引用计数的原生资源，换图不释放会随选择次数累积。
        /// </summary>
        private void ShowIconPreview(string iconName) {
            var oldIcon = _mainImage.Pixbuf;
            _mainImage.SetFromIconName(iconName, IconSize.Dialog);
            oldIcon?.Dispose();
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

        /// <summary>
        /// 回到"未选择任何内容"的初始状态。
        /// 注意：只有"清除选择"与错误回退才应调用它 —— 选中目录但本层没有图片时不能调用，
        /// 否则会把刚启用的开始按钮又关掉，使递归功能不可达。
        /// </summary>
        private void ResetToInitialState() {
            // 先切状态：SetSelection 会同步刷新路径标签，并让开始按钮按 IsStartable 收敛
            SetSelection(SelectionState.Empty);

            ShowIconPreview("folder");

            SetLabelColor(_hintLabel, 0, 0, 255); // 蓝色
            _hintLabel.Text = Strings.Hint_Initial;
            _smallFolderIcon.Hide();

            if (_progressBar != null) {
                _progressBar.Hide();
                _progressBar.Fraction = 0.0;
            }
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

        /// <summary>
        /// 单实例唤醒命名管道监听器的取消令牌源；在语言切换重启前主动取消，
        /// 确保旧实例立即释放管道（Linux 下为 /tmp/CoreFxPipe_* 套接字）。
        /// </summary>
        private static CancellationTokenSource? _pipeServerCts;

        private static string GetActivationPipeName() {
            // 带上当前用户名，避免 Linux 多用户环境下 /tmp/CoreFxPipe_* 权限冲突
            return $"GTK_ImgsToPDF_Activate_{Environment.UserName}";
        }

        private static void RestartApplication() {
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

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
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
        private static CancellationTokenSource StartActivationPipeServer(ImgsToPDF window) {
            var cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;
            string pipeName = GetActivationPipeName();

            _ = Task.Run(async () => {
                while (!token.IsCancellationRequested) {
                    try {
                        using var server = new NamedPipeServerStream(
                            pipeName,
                            PipeDirection.In,
                            1,
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous);

                        await server.WaitForConnectionAsync(token);

                        Application.Invoke((s, e) => {
                            try {
                                window.Deiconify();
                                window.Present();
                            }
                            catch {
                                // 忽略窗口已关闭时的异常
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
                SetDllDirectory(Directory.Exists(runtimeBin) ? runtimeBin : runtimeRoot);
            }

            Application.Init();
            var mainWindow = new ImgsToPDF();
            using var pipeServerCts = StartActivationPipeServer(mainWindow);
            _pipeServerCts = pipeServerCts;
            Application.Run();
            _pipeServerCts?.Cancel();
        }
    }
}
