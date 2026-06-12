local ffi = require("ffi")
local pathUtil = require("Modules.PathUtil")

local VisualGTK = {}

-- 1. 完善 C 声明
ffi.cdef [[
    typedef void* GtkWidget;
    typedef void* gpointer;
    typedef int gboolean;

    void gtk_init(int *argc, char ***argv);
    GtkWidget* gtk_window_new(int type);
    GtkWidget* gtk_button_new_with_label(const char *label);
    GtkWidget* gtk_entry_new();
    GtkWidget* gtk_label_new(const char *str);
    void gtk_container_add(GtkWidget *container, GtkWidget *widget);
    void gtk_widget_show_all(GtkWidget *widget);
    void gtk_main();
    void gtk_main_quit();
    GtkWidget* gtk_message_dialog_new(GtkWidget *parent, int flags, int type, int buttons, const char *message);
    int gtk_dialog_run(GtkWidget *dialog); // 修正返回值为 int
    void gtk_widget_destroy(GtkWidget *widget);

    void gtk_window_set_title(GtkWidget *window, const char *title);
    void gtk_window_set_default_size(GtkWidget *window, int width, int height);
    GtkWidget* gtk_box_new(int orientation, int spacing);
    void gtk_box_pack_start(GtkWidget *box, GtkWidget *child, gboolean expand, gboolean fill, int padding);
    const char* gtk_entry_get_text(GtkWidget *entry);
    void gtk_entry_set_text(GtkWidget *entry, const char *text);

    void gtk_entry_set_visibility(GtkWidget *entry, gboolean visible);
    void gtk_entry_set_invisible_char(GtkWidget *entry, uint32_t ch);

    void gtk_widget_set_can_default(GtkWidget *widget, gboolean can_default);
    void gtk_window_set_default(GtkWidget *window, GtkWidget *widget);
    void gtk_entry_set_activates_default(GtkWidget *entry, gboolean setting);

    typedef enum {
        GTK_WIN_POS_NONE = 0,
        GTK_WIN_POS_CENTER = 1,
        GTK_WIN_POS_MOUSE = 2,
        GTK_WIN_POS_CENTER_ALWAYS = 3,
        GTK_WIN_POS_CENTER_ON_PARENT = 4
    } GtkWindowPosition;

    // 设置窗口位置的函数
    void gtk_window_set_position(GtkWidget *window, GtkWindowPosition position);

    // 信号连接函数
    unsigned long g_signal_connect_data(gpointer instance, const char *detailed_signal, void (*c_handler)(void), gpointer data, gpointer destroy_data, int connect_flags);

    // Win32 API 用于设置 DLL 搜索路径
    int SetDllDirectoryA(const char* lpPathName);
]]

local gtk, gobject

-- 2. 库加载逻辑修复
if ffi.os == "Linux" then
    gtk = ffi.load("libgtk-3.so.0")
    gobject = gtk -- Linux 下通常符号是合并的，或者链接到同一地址
elseif ffi.os == "Windows" then
    -- 1. 安全获取用户目录，并统一使用反斜杠
    local userProfile = os.getenv("userprofile")
    local gtkPath = nil

    if userProfile then
        gtkPath = userProfile .. [[\AppData\Local\Gtk\3.24.24]]
    end

    -- 2. 如果 AppData 路径不存在或不可用，fallback 到当前目录的相对路径
    if not gtkPath or not pathUtil.dirExist(gtkPath) then
        -- 使用 Windows 的反斜杠统一路径格式
        local baseDir = pathUtil.currentDir() .. [[\..]]
        if pathUtil.fileExist(baseDir .. [[\libgtk-3-0.dll]]) then
            gtkPath = baseDir
        else
            gtkPath = baseDir .. [[\runtime]]
        end
    end

    -- 3. 确保最终找到的路径有效，避免传入无效路径给 C API
    if pathUtil.dirExist(gtkPath) then
        ffi.C.SetDllDirectoryA(gtkPath)
    end

    -- 4. 尝试加载库
    local success_gtk, lib_gtk = pcall(ffi.load, "libgtk-3-0.dll")
    local success_gob, lib_gob = pcall(ffi.load, "libgobject-2.0-0.dll")

    -- 6. 结果处理
    if not success_gtk or not success_gob then
        -- 打印出尝试过的路径，方便用户排查错误
        print(string.format("加载库失败。当前尝试的 GTK 路径: %s。请检查路径和架构(x86/x64)是否匹配。", gtkPath))
        return
    end

    gtk = lib_gtk
    gobject = lib_gob
end

-- 3. 防止回调函数被垃圾回收 (GC)
-- 这是一个极其重要的点：如果匿名函数被回收，程序会随机崩溃
local _keep_alive = {}
local function safe_connect(instance, signal, fn)
    _keep_alive[fn] = true -- 保持引用
    gobject.g_signal_connect_data(instance, signal, fn, nil, nil, 0)
end

function VisualGTK.InputBox(prompt, title)
    local inputText
    -- 初始化
    gtk.gtk_init(nil, nil)

    local window = gtk.gtk_window_new(0)
    gtk.gtk_window_set_title(window, title or "输入框示例")
    gtk.gtk_window_set_default_size(window, 300, 100)
    -- 关键行：设置窗口初始位置为屏幕中央
    gtk.gtk_window_set_position(window, 1) -- 1 对应 GTK_WIN_POS_CENTER

    -- 使用 gobject 而非 ffi.C 调用信号函数
    safe_connect(window, "destroy", function()
        gtk.gtk_main_quit()
    end)

    local vbox = gtk.gtk_box_new(1, 10)
    gtk.gtk_container_add(window, vbox)

    local label = gtk.gtk_label_new(prompt or "请输入内容：")
    local entry = gtk.gtk_entry_new()
    gtk.gtk_entry_set_visibility(entry, 0)
    gtk.gtk_entry_set_activates_default(entry, 1) -- 开启回车激活默认按钮
    gtk.gtk_box_pack_start(vbox, label, 1, 1, 0)
    gtk.gtk_box_pack_start(vbox, entry, 1, 1, 0)

    local button = gtk.gtk_button_new_with_label("确定")
    safe_connect(button, "clicked", function()
        inputText = ffi.string(gtk.gtk_entry_get_text(entry))

        -- local dialog = gtk.gtk_message_dialog_new(window, 0, 0, 1, "你输入了：" .. text)
        -- gtk.gtk_dialog_run(dialog)
        -- gtk.gtk_widget_destroy(dialog)

        gtk.gtk_widget_destroy(window)
        gtk.gtk_main_quit()
    end)
    gtk.gtk_widget_set_can_default(button, 1)  -- 允许按钮作为默认对象
    gtk.gtk_window_set_default(window, button) -- 绑定到窗口

    gtk.gtk_box_pack_start(vbox, button, 1, 1, 0)
    gtk.gtk_widget_show_all(window)
    gtk.gtk_main()

    _keep_alive = {}
    return inputText
end

return VisualGTK
