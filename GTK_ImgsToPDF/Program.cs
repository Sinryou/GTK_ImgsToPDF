using Gdk;
using Gtk;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace GTK_ImgsToPDF {
    public class ImgsToPDF : Gtk.Window {
        // 界面控件引用，用于动态更新
        private Overlay _overlay;
        private EventBox _dropTarget; // 接收拖拽的区域
        private Image _mainImage;       // 显示图片预览（或初始大文件夹）
        private Label _hintLabel;       // "拖入包含图片的文件夹"
        private Label _pathLabel;       // 显示 E:\Temp
        private Image _smallFolderIcon; // 叠加的小文件夹图标
        private Button _startBtn;
        private CheckButton _lossyCheck;
        private CheckButton _recursiveCheck;
        private CheckButton _mergeCheck;
        private ComboBoxText _layoutCombo;

        // 定义支持的文件扩展名
        private readonly string[] _supportedExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif" };

        public ImgsToPDF() : base("ImgsToPDF") {
            SetDefaultSize(800, 600);
            SetPosition(WindowPosition.Center);
            this.DeleteEvent += (s, e) => Application.Quit();

            // 主布局：垂直盒子
            VBox mainBox = new VBox(false, 0);
            Add(mainBox);

            // 1. 菜单栏
            mainBox.PackStart(CreateMenuBar(), false, false, 0);

            // 1. 中央区域 (拖放区)
            mainBox.PackStart(CreateCentralDragArea(), true, true, 0);

            // 2. 底部控制栏
            mainBox.PackStart(CreateBottomControls(), false, false, 10);

            ShowAll();

            // 初始状态下隐藏叠加的小图标
            _smallFolderIcon.Hide();
        }

        private MenuBar CreateMenuBar() {
            MenuBar menuBar = new MenuBar();

            MenuItem fileMenu = new MenuItem("文件(F)");
            Menu fileSub = new Menu();

            MenuItem openFolderItem = new MenuItem("打开文件夹(O)");
            openFolderItem.Activated += (s, e) => SelectFolder();
            fileSub.Append(openFolderItem);

            MenuItem clearChosenItem = new MenuItem("清除选择(S)");
            clearChosenItem.Activated += (s, e) => ResetToInitialState();
            fileSub.Append(clearChosenItem);

            fileSub.Append(new SeparatorMenuItem());

            MenuItem quitItem = new MenuItem("退出程序(E)");
            quitItem.Activated += (s, e) => Application.Quit();
            fileSub.Append(quitItem);
            fileMenu.Submenu = fileSub;

            menuBar.Append(fileMenu);

            MenuItem configFileItem = new MenuItem("配置文件(C)");
            configFileItem.Activated += (s, e) => {
                if (!File.Exists(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Core", "config.lua"))) {
                    MsgBox.Show(this, "配置文件不存在！");
                    return;
                }
                Process.Start(
                    new ProcessStartInfo(
                        System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Core", "config.lua")
                        ) { UseShellExecute = true }
                );
            };
            menuBar.Append(configFileItem);

            MenuItem langItem = new MenuItem("语言(L)");
            Menu langSub = new Menu();
            langSub.Append(new MenuItem("中文(CN)"));
            langItem.Submenu = langSub;
            menuBar.Append(langItem);

            MenuItem aboutItem = new MenuItem("关于(A)");
            aboutItem.Activated += OnAboutClicked;
            menuBar.Append(aboutItem);

            return menuBar;
        }

        private Widget CreateCentralDragArea() {
            // 1. 使用 EventBox 使整个中央区域可接收事件
            _dropTarget = new EventBox();

            // 2. 使用 Overlay 允许元素重叠
            _overlay = new Overlay();
            _dropTarget.Add(_overlay);

            // --- 底层：垂直内容布局 ---
            VBox contentBox = new VBox(false, 10);
            contentBox.Valign = Align.Center;

            // 初始状态：显示大文件夹图标
            // 这里使用内置 Stock 图标模拟，实际开发可用特定的 PNG 资源
            _mainImage = new Image(Stock.Directory, IconSize.Dialog);
            // 调整图标大小（可选，如果 Stock 图标太小）
            //_mainImage.PixelSize = 128;

            _hintLabel = new Label("拖入包含图片的文件夹");
            _hintLabel.ModifyFg(StateType.Normal, new Color(0, 0, 255)); // 蓝色文字

            _pathLabel = new Label("等待拖入..."); // 初始状态
            _pathLabel.MarginTop = 10;

            contentBox.PackStart(_mainImage, false, false, 0);
            contentBox.PackStart(_hintLabel, false, false, 0);
            contentBox.PackStart(_pathLabel, false, false, 0);

            _overlay.Add(contentBox);

            // --- 叠加层：小文件夹图标 ---
            // 实际开发中应加载一个自定义的透明 PNG 文件
            _smallFolderIcon = new Image(Stock.Directory, IconSize.Menu);
            //_smallFolderIcon.PixelSize = 32; // 变小

            // 设置在左下角
            _smallFolderIcon.Halign = Align.Start;
            _smallFolderIcon.Valign = Align.End;
            // 设置边距，防止紧贴边缘
            _smallFolderIcon.MarginLeft = 10;
            _smallFolderIcon.MarginBottom = 10;

            _overlay.AddOverlay(_smallFolderIcon);


            // --- 配置拖拽目标的接收能力 ---
            // 设置目标类型为 URI 列表（文件浏览器拖拽通常是这个类型）
            TargetEntry[] targets = new TargetEntry[] {
            new TargetEntry("text/uri-list", 0, 0)
        };
            Gtk.Drag.DestSet(_dropTarget, DestDefaults.All, targets, DragAction.Copy);

            // 连接拖拽接收事件
            _dropTarget.DragDataReceived += OnDragDataReceived;

            return _dropTarget;
        }

        private Widget CreateBottomControls() {
            // 底部控制栏布局 (与前一个代码示例类似，增加了进度条)
            VBox bottomBox = new VBox(false, 10);
            bottomBox.MarginLeft = 20;
            bottomBox.MarginRight = 20;
            bottomBox.MarginBottom = 10;

            // 1. 创建 CheckButton 实例并保留引用
            _lossyCheck = new CheckButton("有损 (更小文件更快生成速度)");
            _recursiveCheck = new CheckButton("递归子文件夹");
            _mergeCheck = new CheckButton("合并子PDF");

            // 2. 设置初始状态
            _mergeCheck.Sensitive = false; // 默认禁用状态
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
            HBox checkBoxes = new HBox(true, 10);
            checkBoxes.PackStart(_lossyCheck, false, false, 0);
            checkBoxes.PackStart(_recursiveCheck, false, false, 0);
            checkBoxes.PackStart(_mergeCheck, false, false, 0);
            bottomBox.PackStart(checkBoxes, false, false, 0);

            HBox actionBox = new HBox(false, 10);
            actionBox.PackStart(new Label("PDF输出版式："), false, false, 0);
            _layoutCombo = new ComboBoxText();
            _layoutCombo.AppendText("单页");
            _layoutCombo.AppendText("双页");
            _layoutCombo.AppendText("双页右至左");
            _layoutCombo.Active = 0;
            actionBox.PackStart(_layoutCombo, false, false, 20);

            // 使用类字段 startBtn
            _startBtn = new Button("开始");
            _startBtn.SetSizeRequest(100, -1);
            _startBtn.Sensitive = false;

            ProgressBar progressBar = new ProgressBar();
            progressBar.Valign = Align.Center; // 设置垂直居中
            progressBar.Hide(); // 关键：初始状态不可见
            progressBar.Fraction = 0.0; // 初始进度为 0

            _startBtn.Clicked += async (s, e) => {
                _hintLabel.Text = "PDF生成中......";
                // 切换为可见状态
                progressBar.Visible = true;
                progressBar.Fraction = 0.5;
                _startBtn.Sensitive = false;
                await Task.Run(() => ButtonClickAction());  // 这里的“await”语句会在后台线程运行LoadData方法
                progressBar.Fraction = 1.0;
                _startBtn.Sensitive = true;
                _hintLabel.Text = "PDF文件已经输出到你的指定路径！";
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
                RecursiveFolder(_pathLabel.Text, new List<string> { }).AsParallel().WithDegreeOfParallelism(4).ForAll(dirPath => {
                    string[] args = _lossyCheck.Active ? new string[] {
                        "-d", dirPath,
                        "-l", _layoutCombo.Active.ToString(), "--fast"
                    } : new string[] {
                        "-d", dirPath,
                        "-l", _layoutCombo.Active.ToString()
                    };
                    var (_, stderr) = RunProcess(fileName, args);
                    if (stderr.Length > 0) {
                        Gtk.Application.Invoke((sender, args) => {
                            MsgBox.Show(this, stderr);
                        });
                    }
                });
                if (_mergeCheck.Active) {
                    string[] args = new string[] {
                        "-d", _pathLabel.Text,
                        "--merge-pdfs"
                    };
                    var (_, stderr) = RunProcess(fileName, args);
                    if (stderr.Length > 0) {
                        Gtk.Application.Invoke((sender, args) => {
                            MsgBox.Show(this, stderr);
                        });
                    }
                }
            }
            else {
                string[] args = _lossyCheck.Active ? new string[] {
                    "-d", _pathLabel.Text,
                    "-l", _layoutCombo.Active.ToString(), "--fast"
                } : new string[] {
                    "-d", _pathLabel.Text,
                    "-l", _layoutCombo.Active.ToString()
                };
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
            Process p = new Process();
            p.StartInfo.FileName = fileName;
            // 针对 Windows 和 Linux 采用不同的参数处理策略
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                // Windows 处理：处理末尾反斜杠转义问题
                for (int i = 0; i < args.Length; i++) {
                    if (!string.IsNullOrEmpty(args[i]) && args[i].EndsWith(@"\")) {
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
                p.StartInfo.Arguments = string.Join(" ", args.Select(a => a.Contains(" ") ? $"'{a}'" : a));
            }
            p.StartInfo.UseShellExecute = false;        // Shell的使用
            p.StartInfo.RedirectStandardInput = true;   // 重定向输入
            p.StartInfo.RedirectStandardOutput = true;  // 重定向输出
            p.StartInfo.RedirectStandardError = true;   // 重定向输出错误
            p.StartInfo.CreateNoWindow = true;          // 设置置不显示示窗口
            p.Start();
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (stdout, stderr); // 输出出流取得命令行结果
        }

        // 处理拖拽接收事件
        private void OnDragDataReceived(object o, DragDataReceivedArgs args) {
            // 检查数据类型是否正确
            if (args.Info != 0) return;

            // 获取拖拽的文件 URI 列表 (file://...)
            string[] uris = args.SelectionData.Uris;
            if (uris == null || uris.Length == 0) return;

            // 获取第一个 URI 并转换为本地路径
            string firstUri = uris[0];
            Uri fileUri = new Uri(firstUri);

            if (!fileUri.IsFile) return;

            string folderPath = fileUri.LocalPath;

            // 检查拖入的是否为文件夹
            if (Directory.Exists(folderPath)) {
                ProcessFolder(folderPath);
            }
            else {
                // 如果是文件，可以做额外处理，这里暂不实现
                Console.WriteLine("拖入的不是文件夹");
            }
            args.RetVal = true; // 表示事件已处理
        }
        private void SelectFolder() {
            string selectedPath = null;

            // 1. 创建文件夹选择对话框
            // 参数：标题, 父窗口, 模式 (SelectFolder), 按钮及其返回码
            using (FileChooserDialog dialog = new FileChooserDialog(
                "选择包含图片的文件夹",
                this, // 如果在 Window 类内，传入 this；否则传入 null
                FileChooserAction.SelectFolder,
                "取消", ResponseType.Cancel,
                "确定", ResponseType.Accept)) {
                dialog.SetDefaultSize(800, 600);
                // 2. 运行对话框并获取用户操作结果
                if (dialog.Run() == (int)ResponseType.Accept) {
                    // 3. 获取选择的路径
                    selectedPath = dialog.Filename;
                }

                // 4. 显式销毁对话框
                dialog.Destroy();
            }

            // 检查拖入的是否为文件夹
            if (Directory.Exists(selectedPath)) {
                ProcessFolder(selectedPath);
            }
            else {
                // 如果是文件，可以做额外处理，这里暂不实现
                Console.WriteLine("拖入的不是文件夹");
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
                    _startBtn?.Sensitive = true;

                    // **核心功能：更新预览图**

                    // 加载并调整图片大小（防止图片过大撑破界面）
                    // 使用 Pixbuf 可以方便地缩放
                    Pixbuf fullPixbuf = new Pixbuf(firstImageFile);

                    // 计算缩放比例，保持长宽比
                    double scale = Math.Min(420.0 / fullPixbuf.Width, 420.0 / fullPixbuf.Height);
                    int scaledWidth = (int)(fullPixbuf.Width * scale);
                    int scaledHeight = (int)(fullPixbuf.Height * scale);

                    Pixbuf scaledPixbuf = fullPixbuf.ScaleSimple(scaledWidth, scaledHeight, InterpType.Bilinear);

                    // 将 Image 控件的数据替换为图片预览
                    _mainImage.Pixbuf = scaledPixbuf;
                    _mainImage.PixelSize = -1; // 取消固定像素大小，使用图片实际大小

                    // 更改提示文字颜色
                    _hintLabel.ModifyFg(StateType.Normal, new Color(138, 43, 226)); // 紫色文字
                    _hintLabel.Text = "点击开始按钮开始PDF文件生成";

                    // **核心功能：显示叠加的小图标**
                    _smallFolderIcon.Show();

                    fullPixbuf.Dispose();
                }
                else {
                    // 文件夹内没有图片，恢复初始状态或提示
                    _pathLabel.Text += " (未发现图片)";
                    ResetToInitialState();
                }
            }
            catch (Exception ex) {
                MsgBox.Show(this, $"处理文件夹出错: {ex.Message}");
                ResetToInitialState();
            }
        }

        private void ResetToInitialState() {
            _mainImage.Pixbuf = null;
            _mainImage.Stock = Stock.Directory;
            _mainImage.IconSize = (int)IconSize.Dialog;
            //_mainImage.PixelSize = 128;
            _hintLabel.ModifyFg(StateType.Normal, new Color(0, 0, 255));
            _hintLabel.Text = "拖入包含图片的文件夹";
            _smallFolderIcon.Hide();

            // 重置 startBtn 状态（安全检查）
            _startBtn?.Sensitive = false;
        }

        // 在“关于”菜单项的 Activated 事件中调用
        private void OnAboutClicked(object? sender, EventArgs e) {
            // 获取程序集信息
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version;
            var copyright = ((System.Reflection.AssemblyCopyrightAttribute)assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyCopyrightAttribute), false)[0]).Copyright;

            // 创建对话框
            AboutDialog ad = new AboutDialog();
            ad.Logo = GetAppIcon();
            ad.ProgramName = "ImagesToPDF";
            ad.Version = version.ToString();
            ad.Copyright = copyright.ToString();
            ad.Website = "https://github.com/Sinryou/ImagesToPDF";
            ad.License = "By MIT License\n\n" + copyright.ToString();
            ad.TransientFor = this; // 设置父窗口

            ad.Run();
            ad.Destroy();
        }
        public Pixbuf GetAppIcon(int targetWidth = 64, int targetHeight = 64) {
            // 1. 从资源类获取字节数组
            byte[] iconBytes = Properties.Resources.appIcon;

            if (iconBytes == null || iconBytes.Length == 0)
                return null;

            // 2. 将字节数组加载为原始 Pixbuf
            using (Pixbuf original = new Pixbuf(iconBytes)) {
                // 3. 计算等比例缩放尺寸
                // 取 目标宽度/原始宽度 和 目标高度/原始高度 中的最小值，确保图片完全适应框内且不拉伸
                double ratio = Math.Min((double)targetWidth / original.Width, (double)targetHeight / original.Height);

                int finalWidth = (int)(original.Width * ratio);
                int finalHeight = (int)(original.Height * ratio);

                // 4. 返回缩放后的 Pixbuf
                return original.ScaleSimple(finalWidth, finalHeight, InterpType.Bilinear);
            }
        }

        public static void Main() {
            Application.Init();
            new ImgsToPDF();
            Application.Run();
        }
    }
}
