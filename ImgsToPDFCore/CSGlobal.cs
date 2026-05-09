using ImgsToPDFCore;
using XLua;

/// <summary>
/// C#内全局使用的变量，同时供Lua调用
/// </summary>
internal struct CSGlobal {
    #region readonlys
    public static readonly LuaEnv luaEnv = new();
    [LuaCallCSharp]
    [ReflectionUse]
    public static readonly List<Type> lua_call_cs_list = [
        typeof(iText.Kernel.Geom.PageSize),
        typeof(iText.Kernel.Geom.Rectangle),
        typeof(PDFWrapper),
    ];
    #endregion
    public static IConfig? luaConfig;
}
