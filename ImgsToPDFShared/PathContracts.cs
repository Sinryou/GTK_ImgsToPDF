namespace ImgsToPDF.Shared {
    /// <summary>
    /// 文件系统路径的比较语义：全项目唯一的定义处。
    ///
    /// 路径是否忽略大小写由<b>底层文件系统</b>决定，不能按字符串内容猜：
    /// · Windows 上 "D:\A" 与 "d:\a" 是同一个目录，用序数比较会把同一目录当成两个
    ///   （书签树里出现重复节点、目录字典各建一份）；
    /// · Unix 系（含 macOS）大小写敏感，忽略大小写则会把两个真实存在的不同目录合并成一个，
    ///   其中一份的图片被静默丢掉 —— 比重复更严重，属于内容丢失。
    ///
    /// macOS 默认的 APFS 虽不区分大小写，但同一目录经 API 返回的大小写形态固定，
    /// 序数比较已足以去重；反之在显式区分大小写的 APFS 卷上忽略大小写反而会误合并。
    /// 因此只有 Windows 使用 OrdinalIgnoreCase。
    /// </summary>
    internal static class PathContracts {
        /// <summary>用于 Dictionary / HashSet 键的路径比较器。</summary>
        public static readonly StringComparer Comparer =
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        /// <summary>用于 string.Equals 的路径比较方式。</summary>
        public static readonly StringComparison Comparison =
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }
}
