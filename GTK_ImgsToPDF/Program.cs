using Gdk;
using Gtk;
using GTK_ImgsToPDF.Config;
using GTK_ImgsToPDF.Localization;
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

        // 定义支持的文件扩展名
        private readonly string[] _supportedExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".jfif", ".pjpeg", ".pjp", ".apng" };
        private readonly string[] _supportedCompressedExtensions = { ".zip", ".rar", ".7z" };

        public ImgsToPDF() : base("ImgsToPDF") {
            string language = _configService.Config.UILocale != "" ? _configService.Config.UILocale : System.Globalization.CultureInfo.CurrentCulture.Name;
            Thread.CurrentThread.CurrentUICulture = new System.Globalization.CultureInfo(language);

            SetDefaultSize(800, 600);
            SetPosition(WindowPosition.Center);
            this.DeleteEvent += (s, e) => Application.Quit();

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

            MenuItem fileMenu = new(Strings.Menu_File);
            Menu fileSub = [];

            MenuItem openFolderItem = new(Strings.Menu_OpenFolder);
            openFolderItem.Activated += (s, e) => SelectFolder();
            fileSub.Append(openFolderItem);

            MenuItem openArchiveItem = new(Strings.Menu_OpenArchive);
            openArchiveItem.Activated += (s, e) => SelectArchive();
            fileSub.Append(openArchiveItem);

            MenuItem clearChosenItem = new(Strings.Menu_ClearSelection);
            clearChosenItem.Activated += (s, e) => {
                _pathLabel.Text = Strings.Path_Waiting;
                ResetToInitialState();
            };
            fileSub.Append(clearChosenItem);

            fileSub.Append(new SeparatorMenuItem());

            MenuItem quitItem = new(Strings.Menu_Exit);
            quitItem.Activated += (s, e) => Application.Quit();
            fileSub.Append(quitItem);
            fileMenu.Submenu = fileSub;

            menuBar.Append(fileMenu);

            MenuItem configFileItem = new(Strings.Menu_Config);
            configFileItem.Activated += (s, e) => {
                string cfgFilePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Core", "config.lua");
                if (!File.Exists(cfgFilePath)) {
                    MsgBox.Show(this, Strings.Msg_ConfigMissing);
                    return;
                }
                Process.Start(
                    new ProcessStartInfo(
                        cfgFilePath
                        ) { UseShellExecute = true }
                );
            };
            menuBar.Append(configFileItem);

            MenuItem langItem = new(Strings.Menu_Lang);
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

            if (Thread.CurrentThread.CurrentUICulture.Name.StartsWith("zh")) {
                menuItemLangCN.Sensitive = false;
            }
            else {
                menuItemLangEN.Sensitive = false;
            }

            langItem.Submenu = langSub;
            menuBar.Append(langItem);

            MenuItem aboutItem = new(Strings.Menu_About);
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
                MarginTop = 10
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
            _dropTarget.DragDataReceived += OnDragDataReceived;

            return _dropTarget;
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
                _hintLabel.Text = Strings.Hint_Generating;
                // 切换为可见状态
                progressBar.Visible = true;
                progressBar.Fraction = 0.5;
                _startBtn.Sensitive = false;
                await Task.Run(() => ButtonClickAction());  // 这里的“await”语句会在后台线程运行LoadData方法
                progressBar.Fraction = 1.0;
                _startBtn.Sensitive = true;
                _hintLabel.Text = Strings.Hint_Done;
            };
            actionBox.PackStart(_startBtn, false, true, 20);

            actionBox.PackStart(progressBar, true, true, 20);

            bottomBox.PackStart(actionBox, false, false, 0);
            return bottomBox;
        }

        private void ButtonClickAction() {
            // 根据平台动态决定文件名
            string coreName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                              ? "ImgsToPDFCore.exe"
                              : "ImgsToPDFCore";
            var fileName = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Core", coreName);
            if (_recursiveCheck.Active && Directory.Exists(_pathLabel.Text)) {
                RecursiveFolder(_pathLabel.Text, []).AsParallel().WithDegreeOfParallelism(4).ForAll(dirPath => {
                    string[] args = _lossyCheck.Active ? [
                        "-d", dirPath,
                        "-l", _layoutCombo.Active.ToString(), "--fast"
                    ] : [
                        "-d", dirPath,
                        "-l", _layoutCombo.Active.ToString()
                    ];
                    var (_, stderr) = RunProcess(fileName, args);
                    if (stderr.Length > 0) {
                        Gtk.Application.Invoke((sender, args) => {
                            MsgBox.Show(this, stderr);
                        });
                    }
                });
                if (_mergeCheck.Active) {
                    string[] args = [
                        "-d", _pathLabel.Text,
                        "--merge-pdfs"
                    ];
                    var (_, stderr) = RunProcess(fileName, args);
                    if (stderr.Length > 0) {
                        Gtk.Application.Invoke((sender, args) => {
                            MsgBox.Show(this, stderr);
                        });
                    }
                }
            }
            else {
                string[] args = _lossyCheck.Active ? [
                    "-d", _pathLabel.Text,
                    "-l", _layoutCombo.Active.ToString(), "--fast"
                ] : [
                    "-d", _pathLabel.Text,
                    "-l", _layoutCombo.Active.ToString()
                ];
                var (_, stderr) = RunProcess(fileName, args);
                if (stderr.Length > 0) {
                    Gtk.Application.Invoke((sender, args) => {
                        MsgBox.Show(this, stderr);
                    });
                }
            }

        }
        static List<string> RecursiveFolder(string path, List<string> dirs) {
            dirs.Add(path);
            var TheFolder = new DirectoryInfo(path);
            foreach (var childFolder in TheFolder.GetDirectories()) {
                RecursiveFolder(childFolder.FullName, dirs);
            }
            return dirs;
        }
        private static (string stdout, string stderr) RunProcess(string fileName, string[] args) {
            // 例Process
            Process p = new();
            p.StartInfo.FileName = fileName;
            // 针对 Windows 和 Linux 采用不同的参数处理策略
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                // Windows 处理：处理末尾反斜杠转义问题
                for (int i = 0; i < args.Length; i++) {
                    if (!string.IsNullOrEmpty(args[i]) && args[i].EndsWith('\\')) {
                        // 如果以 \ 结尾，再加一个 \ 抵消转义
                        args[i] += @"\";
                    }
                    // 包装双引号以处理空格
                    args[i] = $"\"{args[i]}\"";
                }
                p.StartInfo.Arguments = string.Join(" ", args);
            }
            else {
                // Linux 处理：不需要手动加引号，也不存在反斜杠转义可执行文件的问题
                // 直接使用 .NET 自动处理的参数拼接
                p.StartInfo.Arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? $"'{a}'" : a));
            }
            p.StartInfo.UseShellExecute = false;        // Shell的使用
            p.StartInfo.RedirectStandardInput = true;   // 重定向输入
            p.StartInfo.RedirectStandardOutput = true;  // 重定向输出
            p.StartInfo.RedirectStandardError = true;   // 重定向输出错误
            p.StartInfo.CreateNoWindow = true;          // 设置置不显示示窗口
            p.StartInfo.WorkingDirectory = System.IO.Path.GetDirectoryName(fileName);
            p.Start();
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (stdout, stderr); // 输出出流取得命令行结果
        }

        // 处理拖拽接收事件
        private void OnDragDataReceived(object o, DragDataReceivedArgs args) {
            // 检查数据类型是否正确
            if (args.Info != 0) { args.RetVal = true; return; }

            // 获取拖拽的文件 URI 列表 (file://...)
            string[] uris = args.SelectionData.Uris;
            if (uris == null || uris.Length == 0) { args.RetVal = true; return; }

            // 获取第一个 URI 并转换为本地路径
            string firstUri = uris[0];
            Uri fileUri = new(firstUri);

            if (!fileUri.IsFile) { args.RetVal = true; return; }

            string folderPath = fileUri.LocalPath;

            // 检查拖入的是否为文件夹
            if (Directory.Exists(folderPath)) {
                ProcessFolder(folderPath);
            }
            else if (File.Exists(folderPath)) {
                string extension = System.IO.Path.GetExtension(folderPath).ToLower();
                if (_supportedCompressedExtensions.Contains(extension)) {
                    ProcessArchive(folderPath);
                } else {
                    Console.WriteLine(Strings.Drop_NotSupported);
                }
            }
            else {
                Console.WriteLine(Strings.Drop_NotExist);
            }
            args.RetVal = true; // 表示事件已处理
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

                // 4. 显式销毁对话框
                dialog.Destroy();
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

                dialog.Destroy();
            }

            if (string.IsNullOrEmpty(selectedPath)) {
                return;
            }

            if (File.Exists(selectedPath)) {
                string extension = System.IO.Path.GetExtension(selectedPath).ToLower();
                if (_supportedCompressedExtensions.Contains(extension)) {
                    ProcessArchive(selectedPath);
                }
            }
        }

        // 处理文件夹：识别图片并更新 UI
        private void ProcessFolder(string folderPath) {
            _pathLabel.Text = folderPath;

            try {
                // 查找第一张图片
                var firstImageFile = Directory.EnumerateFiles(folderPath)
                    .Where(file => _supportedExtensions.Contains(System.IO.Path.GetExtension(file).ToLower()))
                    .FirstOrDefault();

                if (firstImageFile != null) {
                    _startBtn.Sensitive = true;

                    // 尝试加载预览图（GdkPixbuf 不支持 WebP/TIFF 等格式时回退到 SkiaSharp）
                    var preview = TryLoadPreviewPixbuf(firstImageFile, 420, 420);
                    if (preview != null) {
                        _mainImage.Pixbuf = preview;
                        preview.Dispose();
                    }
                    else {
                        _mainImage.SetFromIconName("image-x-generic", IconSize.Dialog);
                    }

                    SetLabelColor(_hintLabel, 138, 43, 226);
                    _hintLabel.Text = Strings.Hint_Ready;

                    _smallFolderIcon.Show();
                }
                else {
                    // 文件夹内没有图片，恢复初始状态或提示
                    _pathLabel.Text += Strings.Msg_NoImages;
                    ResetToInitialState();
                }
            }
            catch (Exception ex) {
                MsgBox.Show(this, $"{Strings.Msg_ErrProcess}{ex.Message}");
                ResetToInitialState();
            }
        }

        // 处理压缩包：更新 UI 状态
        private void ProcessArchive(string archivePath) {
            _pathLabel.Text = archivePath;
            _startBtn.Sensitive = true;

            // 显示归档图标
            _mainImage.SetFromIconName("package-x-generic", IconSize.Dialog);

            SetLabelColor(_hintLabel, 138, 43, 226); // 紫色
            _hintLabel.Text = Strings.Hint_Ready;

            // 压缩包不显示文件夹叠加图标
            _smallFolderIcon.Hide();
        }

        private void ResetToInitialState() {
            _mainImage.SetFromIconName("folder", IconSize.Dialog);
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
            AboutDialog ad = new() {
                Logo = GetAppIcon(),
                ProgramName = "ImagesToPDF",
                Version = versionStr,
                Copyright = copyright,
                Website = "https://github.com/Sinryou/ImagesToPDF",
                License = "By MIT License\n\n" + copyright,
                TransientFor = this // 设置父窗口
            };

            ad.Run();
            ad.Destroy();
        }
        private static void SetLabelColor(Label label, byte r, byte g, byte b) {
            var cssProvider = new CssProvider();
            cssProvider.LoadFromData($"label {{ color: rgb({r},{g},{b}); }}");
            label.StyleContext.AddProvider(cssProvider, Gtk.StyleProviderPriority.User);
        }

        private static Pixbuf? TryLoadPreviewPixbuf(string imagePath, int maxWidth, int maxHeight) {
            try {
                return new Pixbuf(imagePath, maxWidth, maxHeight, true);
            }
            catch {
                // GdkPixbuf 不支持此格式，尝试 SkiaSharp
            }

            try {
                using var bitmap = SKBitmap.Decode(imagePath);
                if (bitmap == null) return null;

                double scale = Math.Min((double)maxWidth / bitmap.Width, (double)maxHeight / bitmap.Height);
                int scaledW = (int)(bitmap.Width * scale);
                int scaledH = (int)(bitmap.Height * scale);

                using var surface = SKSurface.Create(new SKImageInfo(scaledW, scaledH));
                var canvas = surface.Canvas;
                canvas.DrawBitmap(bitmap, new SKRect(0, 0, scaledW, scaledH));
                using var image = surface.Snapshot();
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);

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

        private static void RestartApplication() {
            var fileName = Environment.ProcessPath;

            Process.Start(new ProcessStartInfo {
                FileName = fileName,
                UseShellExecute = true
            });

            Application.Quit();
            Environment.Exit(0);
        }

        public static void Main() {
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
