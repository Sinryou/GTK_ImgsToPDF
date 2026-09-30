# 第三方组件与许可声明 (Third-Party Notices)

本文件列出 GTK_ImgsToPDF 项目使用并随发行版一起分发的第三方组件及其许可条款。
本项目自身以 MIT 许可证发布（见 [LICENSE.txt](LICENSE.txt)）；下列组件各自遵循其原始许可。

分发包中与第三方相关的文件主要位于根目录及 `Core/` 目录下（由 `ImgsToPDFCore` 项目构建时铺出）：

| 组件 | 许可证 | 版权所有者 | 分发文件 / 模块 |
| --- | --- | --- | --- |
| [iText 9](https://itextpdf.com/) (`itext`, `itext.bouncy-castle-adapter`) | AGPL-3.0 或商业许可 | Copyright (c) 1998-2026 Apryse Group NV | `itext.*.dll` |
| [GtkSharp](https://github.com/GtkSharp/GtkSharp) | LGPL-2.0-only | Copyright (c) GtkSharp Contributors, Novell Inc., Xamarin Inc. | `GtkSharp.dll`、`GdkSharp.dll`、`GLibSharp.dll`、`GioSharp.dll`、`AtkSharp.dll`、`CairoSharp.dll`、`PangoSharp.dll` |
| [SkiaSharp](https://github.com/mono/SkiaSharp) | MIT | Copyright (c) 2015-2016 Xamarin, Inc.<br>Copyright (c) 2017-2026 Microsoft Corporation | `SkiaSharp.dll`、`libSkiaSharp.dll`、`libSkiaSharp.so` |
| [Google Skia](https://skia.org/)（SkiaSharp 底层图形库） | BSD-3-Clause | Copyright (c) 2011 Google Inc. | 包含在 `libSkiaSharp` 原生库中 |
| [BouncyCastle.Cryptography](https://www.bouncycastle.org/) | MIT | Copyright © Legion of the Bouncy Castle Inc. 2000-2026 | `BouncyCastle.Cryptography.dll` |
| [SharpCompress](https://github.com/adamhathcock/sharpcompress) | MIT | Copyright (c) 2025 Adam Hathcock | `SharpCompress.dll` |
| [CommandLineParser](https://github.com/commandlineparser/commandline) | MIT | Copyright (c) 2005 - 2020 Giacomo Stelluti Scala & Contributors | `CommandLine.dll` |
| [XLua](https://github.com/Tencent/xLua) | MIT | Copyright (c) 2016-2018 Tencent | `xlua.dll`、`libxlua.so`、`XLua.Mini.dll` |
| [Lua](https://www.lua.org/)（嵌入在 `xlua.dll` / `libxlua.so` 中） | MIT | Copyright © 1994–2025 Lua.org, PUC-Rio | `xlua.dll`、`libxlua.so` |

`.NET 10` 运行时由微软提供，支持库遵循 MIT 许可证。系统 GTK 运行时（Linux 系统自带或 Windows 部署的 GTK 3.24 动态链接库）遵循 LGPL 许可条款。

---

## iText 9 与 AGPL-3.0

iText 9 采用双许可模式：AGPL-3.0 或商业许可。本项目使用的是 AGPL-3.0 分支。

GNU Affero General Public License (AGPL-3.0) 官方许可全文见：<https://www.gnu.org/licenses/agpl-3.0.html>

---

## GtkSharp 与 LGPL-2.0

GtkSharp 采用 GNU Library General Public License 2.0 (LGPL-2.0-only) 授权。

官方许可全文见：<https://www.gnu.org/licenses/old-licenses/lgpl-2.0.html>

---

## MIT 许可证全文

以下组件均以 MIT 许可证授权，各自保留其版权声明：

- SkiaSharp — Copyright (c) 2015-2016 Xamarin, Inc., Copyright (c) 2017-2026 Microsoft Corporation. All rights reserved.
- BouncyCastle.Cryptography — Copyright © Legion of the Bouncy Castle Inc. 2000-2026
- SharpCompress — Copyright (c) 2025 Adam Hathcock
- CommandLineParser — Copyright (c) 2005 - 2020 Giacomo Stelluti Scala & Contributors
- XLua — Copyright (c) 2016-2018 Tencent
- Lua 5.4.x — Copyright © 1994–2025 Lua.org, PUC-Rio

```text
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## Google Skia（BSD-3-Clause）许可证全文

`libSkiaSharp` 底层依赖 Google 开源的 Skia 图形渲染引擎。

```text
Copyright (c) 2011 Google Inc. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

  * Redistributions of source code must retain the above copyright
    notice, this list of conditions and the following disclaimer.

  * Redistributions in binary form must reproduce the above copyright
    notice, this list of conditions and the following disclaimer in the
    documentation and/or other materials provided with the distribution.

  * Neither the name of Google Inc. nor the names of its contributors may
    be used to endorse or promote products derived from this software
    without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE
LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
POSSIBILITY OF SUCH DAMAGE.
```
