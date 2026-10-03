using System.Buffers.Binary;

namespace ImgsToPDFCore {
    /// <summary>
    /// 从 JPEG 的 APP1/EXIF 段中只解析出 Orientation（方向）标签，
    /// 不解码任何像素数据。
    ///
    /// 之所以需要它：SkiaSharp 的 <c>SKImage.FromEncodedData</c> 会在解码时把方向
    /// "烘焙"进像素，一旦解码就必须重新编码（有损且体积膨胀）。
    /// 而绝大多数 JPEG 的方向都能从元数据直接读出来，此时可以让原始字节原样
    /// 直通进 PDF，方向留到排版期用仿射矩阵应用 —— 零解码、零重编码。
    /// </summary>
    internal static class JpegExif {
        /// <summary>TIFF 中 Orientation 标签的 ID。</summary>
        private const ushort OrientationTag = 0x0112;

        /// <summary>TIFF 魔数 42（0x002A）。</summary>
        private const ushort TiffMagic = 42;

        /// <summary>TIFF 中 SHORT 类型（3）的 ID。</summary>
        private const ushort TypeShort = 3;

        /// <summary>JPEG 段起始标记 0xFF。</summary>
        private const byte MarkerPrefix = 0xFF;

        /// <summary>APP1 段标记。</summary>
        private const byte App1Marker = 0xE1;

        /// <summary>SOI（图像起始）。</summary>
        private const byte SoiMarker = 0xD8;

        /// <summary>EOI（图像结束）。</summary>
        private const byte EoiMarker = 0xD9;

        /// <summary>SOS（扫描行起始）—— 其内部不再有段结构，遇到就停止扫描。</summary>
        private const byte SosMarker = 0xDA;

        /// <summary>EXIF 数据在 APP1 段内的固定前缀 "Exif\0\0"。</summary>
        private static readonly byte[] ExifPrefix = [(byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0];

        /// <summary>
        /// 从完整的 JPEG 字节中读取 EXIF Orientation。
        /// 无 EXIF、无该标签、类型不符或数据损坏时返回 1（表示"无需变换"）。
        /// </summary>
        public static ushort GetOrientation(byte[] raw) {
            try {
                return ParseOrientation(raw);
            }
            catch {
                // 元数据损坏不应导致整张图片被跳过，按"无方向"处理
                return 1;
            }
        }

        private static ushort ParseOrientation(byte[] raw) {
            // 最短的 JPEG 也要有 SOI + 至少一个段
            if (raw.Length < 4 || raw[0] != MarkerPrefix || raw[1] != SoiMarker) {
                return 1;
            }

            int pos = 2;
            while (pos + 4 <= raw.Length) {
                if (raw[pos] != MarkerPrefix) {
                    // 段边界错乱：JPEG 段之间只允许出现 0xFF 填充字节
                    return 1;
                }

                // 跳过 0xFF 填充（多个连续的 0xFF 是合法的）
                int markerPos = pos;
                while (markerPos < raw.Length && raw[markerPos] == MarkerPrefix) {
                    markerPos++;
                }
                if (markerPos >= raw.Length) {
                    return 1;
                }

                byte marker = raw[markerPos];

                // SOS 之后是压缩数据，不再有 APP1；EOI 表示图像结束
                if (marker == SosMarker || marker == EoiMarker) {
                    return 1;
                }

                // 无长度字段的独立标记，直接跳过
                if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) {
                    pos = markerPos + 1;
                    continue;
                }

                int lengthPos = markerPos + 1;
                if (lengthPos + 2 > raw.Length) {
                    return 1;
                }

                // 段长度字段含自身 2 字节
                int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(lengthPos, 2));
                if (segmentLength < 2) {
                    return 1;
                }

                int dataStart = lengthPos + 2;
                int dataLength = segmentLength - 2;
                if (dataStart + dataLength > raw.Length) {
                    return 1;
                }

                if (marker == App1Marker && StartsWithExifPrefix(raw, dataStart, dataLength)) {
                    return ParseTiffOrientation(raw, dataStart + ExifPrefix.Length, dataLength - ExifPrefix.Length);
                }

                pos = dataStart + dataLength;
            }

            return 1;
        }

        private static bool StartsWithExifPrefix(byte[] raw, int offset, int length) {
            if (length < ExifPrefix.Length) {
                return false;
            }
            for (int i = 0; i < ExifPrefix.Length; i++) {
                if (raw[offset + i] != ExifPrefix[i]) {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 在一个 TIFF 头（EXIF 载荷）里定位 Orientation 标签。
        /// 兼容 "II"（小端）与 "MM"（大端）两种字节序。
        /// </summary>
        private static ushort ParseTiffOrientation(byte[] raw, int offset, int length) {
            // 至少需要：字节序标记(2) + 魔数(2) + IFD 偏移(4)
            if (length < 8) {
                return 1;
            }

            int end = offset + length;
            bool littleEndian;
            if (raw[offset] == 'I' && raw[offset + 1] == 'I') {
                littleEndian = true;
            }
            else if (raw[offset] == 'M' && raw[offset + 1] == 'M') {
                littleEndian = false;
            }
            else {
                return 1;
            }

            ushort ReadUInt16(int pos) => littleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(pos, 2))
                : BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(pos, 2));

            uint ReadUInt32(int pos) => littleEndian
                ? BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(pos, 4))
                : BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(pos, 4));

            if (ReadUInt16(offset + 2) != TiffMagic) {
                return 1;
            }

            uint ifdOffset = ReadUInt32(offset + 4);

            // IFD 偏移必须能容纳 2 字节的条目数量字段
            if (ifdOffset > (uint)(length - 8)) {
                return 1;
            }

            int ifd = offset + (int)ifdOffset;
            if (ifd + 2 > end) {
                return 1;
            }

            ushort entryCount = ReadUInt16(ifd);
            int entriesStart = ifd + 2;

            for (int i = 0; i < entryCount; i++) {
                int entry = entriesStart + i * 12;   // 每个 TIFF 条目固定 12 字节
                if (entry + 12 > end) {
                    return 1;
                }

                if (ReadUInt16(entry) != OrientationTag) {
                    continue;
                }

                ushort type = ReadUInt16(entry + 2);
                uint count = ReadUInt32(entry + 4);

                // Orientation 必须是 SHORT 且数量为 1
                if (type != TypeShort || count != 1) {
                    return 1;
                }

                // 值存在 4 字节值字段的低 2 字节（小端）或高 2 字节（大端）
                ushort orientation = ReadUInt16(entry + 8);
                return orientation is >= 1 and <= 8 ? orientation : (ushort)1;
            }

            return 1;
        }
    }
}
