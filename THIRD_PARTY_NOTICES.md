# 第三方组件与许可证提示（发行时请随包提供）

本程序**不内置**下列组件，由用户或发行包另行提供：

| 组件 | 说明 |
|------|------|
| **FFmpeg** | 动态链接/可执行形式分发时，请遵守 [FFmpeg 许可证 FAQ](https://ffmpeg.org/legal.html) 及你所使用的构建版本（如 LGPL/GPL）要求，并附带相应许可证文本。 |
| **rembg** | [rembg 仓库许可证](https://github.com/danielgatis/rembg)（通常为 MIT，以实际版本为准）。 |
| **rembg 所下载的模型权重** | 各模型可能有单独条款，商用前请查阅 rembg / 模型卡说明。 |
| **.NET 运行时** | 自包含发布时包含 .NET 运行时，遵循 [Microsoft .NET 许可](https://dotnet.microsoft.com/license)。 |

本仓库中的 **Mp4ToFrame** 自写代码**不包含**上述二进制；仅通过进程调用 `ffmpeg` 与 `rembg`。
