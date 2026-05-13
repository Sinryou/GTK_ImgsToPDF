using Gtk;

namespace GTK_ImgsToPDF {
    internal static class MsgBox {
        public static void Show(Window parent, string message, MessageType type = MessageType.Info) {
            MessageDialog md = new(parent, DialogFlags.Modal, type, ButtonsType.Ok, message);
            md.Run();
            md.Destroy();
        }
    }
}
