using Gtk;
using GTK_ImgsToPDF.Localization;

namespace GTK_ImgsToPDF {
    /// <summary>
    /// 解压密码输入对话框。
    /// 由 GUI 在主窗口上以模态方式弹出，Core 子进程通过 stdout/stdin 的
    /// <see cref="ImgsToPDF.Shared.IpcContracts"/> 协议发起请求并接收结果 ——
    /// 密码框属于界面进程，不能由 Core 自己绘图（命令行调用时 Core 没有 GTK 环境）。
    /// </summary>
    internal sealed class PasswordDialog : Dialog {
        private readonly Entry _passwordEntry;

        /// <summary>用户输入的密码（仅在 <see cref="ResponseType.Ok"/> 时有效）。</summary>
        public string Password => _passwordEntry.Text;

        public PasswordDialog(string archivePath, Window parent)
            : base(Strings.Dialog_PasswordTitle, parent, DialogFlags.Modal) {
            SetDefaultSize(360, -1);
            SetPosition(WindowPosition.CenterOnParent);

            // 提示文案里的占位符是压缩包文件名：多进程并发解压时用户才知道该输哪个包的密码
            // 注意：Window 上有 Gtk.Widget.Path 方法，必须写全限定名才指向 System.IO.Path
            string prompt = string.Format(Strings.Dialog_PasswordPrompt, System.IO.Path.GetFileName(archivePath));

            var box = new Box(Orientation.Vertical, 8) {
                MarginStart = 16,
                MarginEnd = 16,
                MarginTop = 12,
                MarginBottom = 8
            };

            var promptLabel = new Label(prompt) {
                Halign = Align.Start,
                LineWrap = true,
                MaxWidthChars = 40
            };

            _passwordEntry = new Entry {
                Visibility = false,
                ActivatesDefault = true,   // 回车即确认
                MarginTop = 4
            };

            var showPassword = new CheckButton(Strings.Dialog_ShowPassword) { Halign = Align.Start };
            showPassword.Toggled += (_, _) => _passwordEntry.Visibility = showPassword.Active;

            box.PackStart(promptLabel, false, false, 0);
            box.PackStart(_passwordEntry, false, false, 0);
            box.PackStart(showPassword, false, false, 0);

            // Dialog.ContentArea 在所有 GTK3 版本上都存在，AddButton 的按钮顺序由主题决定
            ContentArea.Add(box);

            AddButton(Strings.Dialog_Cancel, ResponseType.Cancel);
            AddButton(Strings.Dialog_OK, ResponseType.Ok);
            DefaultResponse = ResponseType.Ok;

            ShowAll();
            _passwordEntry.GrabFocus();
        }
    }
}
