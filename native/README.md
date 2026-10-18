# lst-ocr（原生 OCR 子进程）

LightShot Translate 的 OCR 引擎，C++17 实现，**不含 .NET 运行时**，识别时占用比托管版低。

- 与 `OnnxOcrEngine.cs`（托管版，作为后备）算法一致：PP-OCRv4 移动端检测 + 识别，检测分带处理，纯 C++ 后处理，不依赖 OpenCV。
- 运行时通过 `LoadLibraryExW` 动态加载同目录的 `onnxruntime.dll`（用 ORT C++ API 的 `ORT_API_MANUAL_INIT`），所以编译时不需要 ORT 的导入库。
- 通信：stdin/stdout 二进制流，协议与托管版相同（见 `src/lst_ocr.cpp` 顶部注释）。
- 用法：`lst-ocr <推理线程数> <单次检测像素预算> [模型目录]`，模型目录缺省为 exe 所在目录下的 `models`。
- 设置环境变量 `LST_OCR_LOG=1`：每次识别后往 `%TEMP%\lst-ocr.log` 追加峰值工作集与峰值私有内存，并向 stderr 输出分阶段耗时。
- 调优用的环境变量（默认值即实测最优，见 PERFORMANCE.md）：`LST_REC_MINW`（识别批宽下限，默认 96）、`LST_REC_BUDGET`（识别批预算，默认 1200）、`LST_ORT_OPT`（0/1/2 = BASIC/EXTENDED/ALL，默认 2）、`LST_ORT_SPIN`（1 = 恢复线程池空转）、`LST_ORT_ARENA`（1 = 打开 ORT 内存池）。
- 预热：发送 w=h=len=0 的空帧 = 只创建并预热引擎，返回 0 行。
- 头文件取自 onnxruntime 1.20.1 发行包（MIT）。ORT 版本需与程序随附的 `onnxruntime.dll`（NuGet 1.20.1）主版本一致（`ORT_API_VERSION` = 20）。

## 重新编译
- Linux / WSL：`sh build_mingw.sh`（MinGW-w64）
- Windows：在「x64 Native Tools Command Prompt」里运行 `build_msvc.bat`

编译出的 `lst-ocr.exe` 放在 `native\` 下，项目构建时会自动复制到输出目录。程序找不到它、或它无法启动时，会自动退回托管版 OCR。
