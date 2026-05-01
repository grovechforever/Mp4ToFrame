# Mp4ToFrame

Windows 桌面工具：在工作路径下维护 **`Video` / `Frame` / `Matted`**（及可选 **`Final`**），用 **FFmpeg** 从 MP4 导出序列帧，可选 **rembg** 抠图，再将 **Matted** 按界面设定的目标分辨率导出到 **Final** 或自定义文件夹。

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
| `Final/` | （可选）将 Matted 内图片按「目标宽/高」等比缩放并透明边填充后的输出；输出路径可在界面改为任意文件夹 |

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
- **Border（L/R/T/B）**：**正数**表示从该边**向内裁切**相应像素；**负数**表示向该边**外扩**（绝对值）像素，扩展区域用 **ARGB 填充色**（默认 `00000000` 全透明）。  
- **最终导出（Matted → 目标分辨率）**：在缩放到目标画布后，可再套一层 **Border**，规则与上条相同；界面中为**独立**一组参数（与视频抽帧的 Border 互不共用）。  
- **抠图**：通过 **rembg** 命令行处理 `Frame` 中的图片，参数与 rembg 官方 CLI 一致（模型、alpha matting 等）。**加速**：界面「并行进程数」设为 2～8 可多进程分批跑 rembg（墙钟时间常明显缩短，内存约按进程数增加）；另可换轻量模型（如 `u2netp`）、关闭 Alpha matting、安装 `rembg[gpu,cli]` 用 NVIDIA GPU。

---

## 配置保存

`%AppData%\Mp4ToFrame\settings.json`

---

## 合规与隐私（摘要）

- **许可证**：仓库内自研代码以 **`LICENSE`（MIT）** 授权；**不包含** FFmpeg、rembg、模型、.NET 运行时的权利转让。  
- **再分发**：若安装包或压缩包内**附带 `ffmpeg.exe`**（含使用「下载便携版」后再打包），须遵守该 **FFmpeg 构建**（默认来自 BtbN **GPL** 包）的再分发要求，详见 **`THIRD_PARTY_NOTICES.md`**。  
- **隐私**：程序**无遥测**；设置仅存本机 `%AppData%\Mp4ToFrame\settings.json`。使用「下载便携版」会访问 **GitHub**；抠图时 **rembg** 可能自行联网下载模型。  
- **素材**：用户对工作区内的视频/图片的**版权与授权**自行负责。  

完整第三方列表、winget、网络与责任说明见 **`THIRD_PARTY_NOTICES.md`**（其中说明**不构成法律意见**）。

---

## 开源与许可证

- 本仓库业务代码：**`LICENSE`**（MIT，版权行可按维护者身份修改）。  
- 第三方与合规要点：**`THIRD_PARTY_NOTICES.md`**；公开发布或商用前请通读。

---

## 常见问题

| 现象 | 处理 |
|------|------|
| 放了 mp4 但列表没有 | 文件须在 **`Video` 根目录**；可切换回窗口刷新列表。 |
| 找不到 FFmpeg | 使用程序内下载，或安装后加入 PATH / 填写完整路径。 |
| 抠图失败 | 确认已安装 rembg；必要时在界面填写 `rembg.exe` 或 `python.exe` 的完整路径。 |
| 首次抠图很慢 | rembg 正在下载模型，需稳定网络。 |
