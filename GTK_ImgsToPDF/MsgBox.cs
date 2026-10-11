using Gtk;

namespace GTK_ImgsToPDF {
    internal static class MsgBox {
        public static void Show(Window parent, string message, MessageType type = MessageType.Info, string? title = null) {
            MessageDialog md = new(parent, DialogFlags.Modal, type, ButtonsType.Ok, message);
            if (!string.IsNullOrEmpty(title)) {
                // GTK 的 MessageDialog 默认标题是 "Message"，
                // 出错时换成本地化标题（对应原版 MessageBox 的 caption）
                md.Title = title;
            }
            md.Run();
            md.Destroy();
        }

        public static bool Confirm(Window parent, string message, string? title = null) {
            MessageDialog md = new(parent, DialogFlags.Modal, MessageType.Question, ButtonsType.YesNo, message);
            if (!string.IsNullOrEmpty(title)) {
                md.Title = title;
            }
            int response = md.Run();
            md.Destroy();
            return response == (int)ResponseType.Yes;
        }
    }
}
