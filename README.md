# Mp4ToFrame

Windows 桌面工具：在工作路径下维护 **`Video` / `Frame` / `Matted`**，用 **FFmpeg** 从 MP4 导出序列帧，可选 **rembg** 批量抠图。

---

## 快速开始

1. 安装 **[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)**，可用 `dotnet --version` 确认。  
2. 在仓库根目录打开终端，进入工程目录： **`cd src/Mp4ToFrame`**。  
3. 执行 **`dotnet run -c Release`**，在窗口中选择工作路径；将 **`.mp4` 放在 `Video` 目录下（仅顶层，不递归子文件夹）**。  
4. **FFmpeg**：使用程序内 **「下载便携版」**（需能访问 GitHub）或自行安装；**仅导出序列帧**时不需要 Python。  
5. **抠图**：先执行 `pip install "rembg[cli,cpu]"`；首次抠图时 rembg 会**联网下载**所选模型。

**发布单文件 exe**（自包含 win-x64，便于在未安装 SDK 的电脑上使用）：

```bash
cd src/Mp4ToFrame
dotnet publish -c Release -o ../../publish
```

输出示例：仓库根下的 **`publish/Mp4ToFrame.exe`**。可将 **`ffmpeg.exe`** 放在 exe 同目录，或使用程序内下载。

---

## 目录约定

| 文件夹 | 用途 |
|--------|------|
| `Video/` | 放入 `.mp4`（仅扫描该目录**根下**的 mp4） |
| `Frame/` | 导出的序列帧 PNG |
| `Matted/` | rembg 抠图结果 |

若存在误拼文件夹 **`Vedio`** 且尚无 **`Video`**，首次整理时会自动改名为 **`Video`**。

---

## 依赖

- **Windows x64**  
- **.NET 8**：开发使用 SDK；`dotnet publish` 生成的自包含 exe 可在无 SDK 环境运行  
- **FFmpeg**：与 exe 同目录、系统 PATH，或在界面填写路径；支持程序内下载便携构建  
- **抠图（可选）**：**Python** 与 `pip install "rembg[cli,cpu]"`；程序会尝试 PATH、`Scripts\rembg.exe`、`python -m rembg`，也可在界面填写 `rembg.exe` 或 `python.exe` 路径  

---

## 抽帧与抠图行为（简要）

- **抽帧**：按界面设定的 **目标宽高** 与 **FPS**，用 FFmpeg 做缩放与补边（保持宽高比，不足部分黑边），可选 **水平镜像**；时间轴与关键帧策略以 FFmpeg 为准。  
- **抠图**：通过 **rembg** 命令行处理 `Frame` 中的图片，参数与 rembg 官方 CLI 一致（模型、alpha matting 等）。

---

## 配置保存

`%AppData%\Mp4ToFrame\settings.json`

---

## 开源与许可证

- 本仓库业务代码示例许可：**`LICENSE`**（MIT，可按你的项目调整）。  
- **FFmpeg、rembg、模型权重、.NET 运行时** 等第三方义务见 **`THIRD_PARTY_NOTICES.md`**；公开发布或商用前请一并阅读。

---

## 常见问题

| 现象 | 处理 |
|------|------|
| 放了 mp4 但列表没有 | 文件须在 **`Video` 根目录**；可切换回窗口刷新列表。 |
| 找不到 FFmpeg | 使用程序内下载，或安装后加入 PATH / 填写完整路径。 |
| 抠图失败 | 确认已安装 rembg；必要时在界面填写 `rembg.exe` 或 `python.exe` 的完整路径。 |
| 首次抠图很慢 | rembg 正在下载模型，需稳定网络。 |
