using System.Runtime.InteropServices;

namespace GTK_ImgsToPDF {
    /// <summary>
    /// 平台原生 API 的集中管理类。
    ///
    /// 这里只放"托管代码无法等价实现"的系统调用；可用 .NET BCL 完成的
    /// （可用内存查询、窗口前置等）一律留在各自调用点，避免无谓的 P/Invoke。
    /// </summary>
    internal static class NativeMethods {
        /// <summary>
        /// 与 Windows 资源管理器一致的自然排序比较（"2" 排在 "10" 前面）。
        ///
        /// 仅在 Windows 上调用：Linux 上不存在 shlwapi.dll，
        /// <see cref="FolderScanner.NaturalPathComparer"/> 会走托管实现。
        /// 统一限定从 System32 加载，防止当前目录下的同名 DLL 被优先加载。
        /// </summary>
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int StrCmpLogicalW(string psz1, string psz2);
    }
}
