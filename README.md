# LightShot Translate (LST) — v0.8.1

Windows 轻量截图工具：**截图 · 画笔标注 · 本地 OCR 识字 · 原位覆盖翻译 · 贴图**。
C# / .NET 8 / WinForms，托盘常驻；OCR 跑在独立的原生子进程里（ONNX Runtime + PP-OCRv4 移动端模型），用完即退，空闲时几乎不占内存。

> 版本变更见 [CHANGELOG.md](CHANGELOG.md)；性能测量与调优依据见 [PERFORMANCE.md](PERFORMANCE.md)；人工回归测试清单见 [TESTING.md](TESTING.md)。

## 概览

- **技术栈**：C# / .NET 8 WinForms（x64、PerMonitorV2 DPI）；原生 C++ OCR 子进程（ONNX Runtime + PP-OCRv4）；OpenAI 兼容翻译接口；Inno Setup 安装包 + 自包含便携包。
- **截图链路**：热键 → 虚拟桌面截图 → 智能框选遮罩 → 画笔 / 复制 / 保存 / OCR / 翻译 / 贴图。
- **OCR**：识别跑在独立原生子进程里，空闲自动退出并回收内存；大图按内存预算分带检测，保持原分辨率。
- **翻译**：选区文字按段落原位覆盖翻译；批量请求按行严格校验，失败自动重试且不会把译文错位到别的文字块。
- **AI 悬浮球**：问答 / 翻译双会话（历史互不干扰）、流式输出、划词工具栏、对话记录查看与导出（TXT / Markdown）。
- **可靠性**：配置与会话原子写入、单实例、OCR 超时看门狗、网络错误分类与结果缓存；`LST_PERF=1` 可输出热键到首帧、OCR 与面板的耗时。

## 使用

| 快捷键（默认） | 行为 |
|---|---|
| `Alt+A` | 截图：选区后弹出工具栏 **画笔 / 复制 / 保存 / OCR / 翻译 / 贴图 / 取消** |
| `Alt+S` | 选区后直接识字（原位窗口，淡蓝框标出文字，按 `T` 再翻译） |
| `Alt+T` | 选区后直接 **原位翻译**（译文按段落覆盖在原文位置） |
| `Alt+P` | 选区后直接 **贴图**（置顶悬浮，可缩放；窗口内可再识字/翻译） |

### 选区：自动框选 + 自由拖动
- 鼠标移动时，**自动高亮指针下的窗口/控件**；**单击**选中，**拖动**则自由框选。
- **滚轮**调整自动选区范围（上滚=更大：控件 → 窗口 → 整个显示器；下滚=更小）。
- 选区右下角显示 `宽 × 高 px`；`Esc` 或右键退出，选区已确定时右键 = 取消选区重选。

### 画笔
- 选区后点工具栏第一个按钮「画笔」：6 种颜色、3 种粗细，撤销（按钮或 `Ctrl+Z`）。
- 笔迹画进**复制 / 保存 / 贴图**的图片；**识字 / 翻译使用不带笔迹的原图**（笔迹会干扰识别）。
- 「保存」始终弹出“另存为”（可改目录、文件名、格式 png/jpg/bmp），默认从上次保存的目录开始。

### 贴图 / 原位智能窗口
- 在选区原位置置顶贴出，**滚轮缩放**（20%–600%，以指针为锚点）、**左键拖动**、`1` 还原 100%。
- 鼠标移入出现图标工具栏：复制 / 识字 / 翻译 / 原译切换 / 关闭；`Esc` 关闭。
- **单击文字块 = 复制**（译文状态下复制该段译文）；`空格` 切换原文⇄译文；`T` 翻译 / 重新翻译。
- **段落合并**：设置里选 关闭 / 严格 / 标准 / 宽松；窗口内按 `M` 循环切换（已翻译时会按新分块自动重译）。同一段落的相邻行会合并成一段整体翻译，再按原区域自动换行、自适应字号覆盖回去；标题、短行、代码行、列表项不会被误合并。
- 右键菜单：查看/编辑识别文字、复制图片/译文/原文、另存为图片、还原大小。

### 翻译
- 使用 **OpenAI 兼容接口**（`POST {API 地址}/chat/completions`），在托盘「设置」里填 API 地址 / Key / 模型 / 目标语言。API Key 用 Windows DPAPI 加密保存。
- **隐私**：识别出的文字会发送到你配置的接口；OCR 本身完全离线。
- 按行批量翻译要求模型返回“与输入等长的 JSON 字符串数组”。数量或格式不对会带更明确的提示**自动重试一次**；仍不合格则保留原文并提示原因（可再按 `T` 重试），**不会**把译文错位到别的文字块上。暂时性故障（超时 / 429 / 5xx）自动重试一次。
- 相同文本 + 相同参数的结果会缓存（最近 64 条），来回切换段落合并模式不会重复调用 API。

### OCR 相关设置
- **OCR 模型驻留**：极致轻量（空闲 30 秒结束子进程）/ 平衡（5 分钟，默认）/ 性能优先（常驻到退出）。
- **OCR 识别时内存**：省内存（默认）/ 平衡 / 高速。大图会按像素预算分带检测，保持原分辨率，只影响大图的速度和占用，不影响精度。

## 构建、运行与打包

需要：Windows 10 1607+ x64、[.NET 8 SDK](https://dotnet.microsoft.com/download)。

```powershell
cd LightShotTranslate
dotnet run -c Release          # 开发运行（OCR 需要 native\lst-ocr.exe 和 models\，已随项目提供）
```

**打包给没有 .NET 的电脑**（需要本机已装 .NET 8 SDK、Inno Setup 6）：

```powershell
.\build_installer.bat
```

自动完成：`publish.ps1 -SelfContained`（自带 .NET 运行时 + VC++ 运行库，ReadyToRun）→ Inno Setup 编译安装包 → 生成便携 zip。
产物在 `dist\`：`LightShotTranslate-Setup-版本.exe`（简体中文/英文向导，默认装到当前用户目录、无需管理员权限）和 `LightShotTranslate-portable-版本.zip`（免安装）。目标电脑要求 64 位 Windows 10 1607 及以上，**无需安装 .NET 或 VC++**。

- VC++ 运行库（vcruntime140、vcruntime140_1、msvcp140）优先取自 Visual Studio 的 `VC\Redist\MSVC\…\x64\Microsoft.VC14*.CRT`，没有 VS 则取本机 System32；所用文件、版本与 SHA256 记录在发布目录的 `redist-manifest.txt`。
- 只想要小体积、目标机已有 .NET 8 桌面运行时：`.\publish.ps1`（不带 `-SelfContained`）。
- 重新编译原生 OCR 程序：见 [native/README.md](native/README.md)。

## 排错
- OCR 报“无法加载 onnxruntime.dll”：确认 `onnxruntime.dll` 与 `lst-ocr.exe` 在程序目录，且 VC++ 运行库（vcruntime140 等）在同目录或已安装 vc_redist.x64。
- 报“缺少 OCR 模型文件”：确认程序目录下有 `models` 文件夹（含 2 个 .onnx 和 ppocr_keys_v4.txt）。
- OCR 提示“识别超时”：子进程已被终止并会在下次识别时重建，直接重试即可；超时时限随图片大小自动放宽（环境变量 `LST_OCR_TIMEOUT_MS` 可覆盖）。
- 原生 OCR 起不来时程序会自动退回托管版（`LightShotTranslate.exe --ocr-worker`）；环境变量 `LST_OCR_WORKER=managed` 可强制用托管版对比。
- 性能排查：`LST_PERF=1` → `%TEMP%\lst-perf.log`（热键→首帧、OCR 排队/往返/冷热启动）；`LST_OCR_LOG=1` → `%TEMP%\lst-ocr.log`（子进程峰值工作集与峰值私有内存）。评估内存请看**私有内存（Private Bytes）**，工作集会被 trim，不代表真实需求。

## 目录
```
Program.cs              入口（先判 --ocr-worker，再单实例 + 托盘）
TrayApp.cs              托盘、热键、动作流程（复制/保存/识字/翻译/贴图）
HotkeyWindow.cs         RegisterHotKey
ScreenCapture.cs        整个虚拟桌面截图
SelectionOverlay.cs     遮罩 + 框选 + 智能选区 + 画笔
OverlayToolbar.cs / PenBar.cs  自绘工具栏 / 画笔选项条
WindowFinder.cs         窗口/控件枚举（带缓存）
PinTranslateWindow.cs   贴图 + 原位翻译窗口（段落合并、缩放、渲染）
PixelBuffer.cs / Perf.cs  非托管像素缓冲 / 性能追踪
ResultForm.cs           文字结果窗口
TranslationService.cs   OpenAI 兼容翻译（校验 / 重试 / 缓存 / 错误分类）
OcrService.cs           OCR 客户端：子进程管理、超时看门狗、取消、空闲回收
OcrWorker.cs            托管版 OCR 子进程（后备）
OnnxOcrEngine.cs        托管版检测 + 识别引擎（后备）
native/                 原生 C++ OCR 子进程 lst-ocr.exe（首选）及源码、构建脚本
models/                 PP-OCRv4 检测 / 识别模型与字典（约 16MB）
Settings.cs / SettingsForm.cs  设置、DPAPI 加密 Key、开机自启
FloatBall/              AI 悬浮球：悬浮球 / 对话面板 / 消息列表 / 会话持久化 / 划词工具栏
MemoryTrim.cs / AppIcon.cs     工作集 trim / 程序图标
publish.ps1 / installer.iss / build_installer.bat   发布与打包
```
