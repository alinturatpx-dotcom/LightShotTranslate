// lst-ocr: LightShot Translate 的原生 OCR 子进程（C++17，ONNX Runtime C++ API，运行时动态加载 onnxruntime.dll）。
// 与托管版 OnnxOcrEngine 算法一致：PP-OCRv4 移动端检测 + 识别，纯 C++ 后处理，不依赖 OpenCV。
// 协议（小端，经 stdin/stdout 二进制）：
//   请求 = int32 Magic, int32 w, int32 h, int32 len, byte[len] BGR 紧密像素
//   响应 = byte 0, int32 n, n × { int32 x, y, w, h; float32 score; 7 位变长长度前缀 + UTF-8 文本 }
//         或 byte 1, 变长前缀字符串（错误信息）
// 用法：lst-ocr <推理线程数> <单次检测像素预算> [模型目录]    （模型目录缺省 = exe 所在目录\models）
// windows.h 的 min/max 宏会与 std::min/max/clamp 冲突：MSVC 下必须先禁用（MinGW 也兼容）
#define NOMINMAX
#define ORT_API_MANUAL_INIT
#if defined(__MINGW32__) || defined(__MINGW64__)
// ORT 头文件使用 MSVC 的 SAL 注解，MinGW 没有这几个：定义为空
#  ifndef _Frees_ptr_opt_
#    define _Frees_ptr_opt_
#  endif
#  ifndef _Check_return_
#    define _Check_return_
#  endif
#  ifndef _Ret_maybenull_
#    define _Ret_maybenull_
#  endif
#  ifndef _Ret_notnull_
#    define _Ret_notnull_
#  endif
#endif
#include "onnxruntime_cxx_api.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>

#ifdef _WIN32
#  define WIN32_LEAN_AND_MEAN
#  include <windows.h>
#  include <psapi.h>
#  include <fcntl.h>
#  include <io.h>
#else
#  include <dlfcn.h>
#  include <unistd.h>
#  include <limits.h>
#endif

namespace {

static int env_int(const char* name, int def) { const char* v = std::getenv(name); return v && *v ? std::atoi(v) : def; }
constexpr int32_t kMagic = 0x3254534C;
constexpr int kMaxDetWidth = 1600, kMaxDetHeight = 3200;
constexpr int kBandOverlapBig = 64, kBandOverlapSmall = 32, kMinBandHeightBig = 128, kMinBandHeightSmall = 64;   // 带高 ≥128 用 64 行重叠，否则 32 行
constexpr int kRecHeight = 48, kRecMaxWidth = 3200;
static const int kRecMinWidth = env_int("LST_REC_MINW", 96);   // 批宽下限：实测短标签场景识别耗时 2.9s → 1.1s，文本完全一致（见 PERFORMANCE.md）
constexpr int kRecBatchMax = 6;
static const int kRecBatchBudget = env_int("LST_REC_BUDGET", 1200);   // 批内 (条数 × 宽度) 上限：识别输出是 [B,T,6625] 的 float，这是识别阶段的主要峰值
constexpr float kDetThresh = 0.3f, kBoxThresh = 0.5f, kUnclipRatio = 1.6f, kMinScore = 0.5f;
constexpr size_t kMaxCandidates = 1000;

struct Rect { int x0, y0, x1, y1; int w() const { return x1 - x0; } int h() const { return y1 - y0; } };
struct Found { Rect box; int band; };
struct Line { Rect box; float score; std::string text; };

// ---------------- 协议 IO ----------------
bool read_exact(void* p, size_t n) { return n == 0 || std::fread(p, 1, n, stdin) == n; }
void write_all(const void* p, size_t n) { std::fwrite(p, 1, n, stdout); }

void put_i32(std::vector<uint8_t>& b, int32_t v) { const uint8_t* p = reinterpret_cast<const uint8_t*>(&v); b.insert(b.end(), p, p + 4); }
void put_f32(std::vector<uint8_t>& b, float v) { const uint8_t* p = reinterpret_cast<const uint8_t*>(&v); b.insert(b.end(), p, p + 4); }
void put_str(std::vector<uint8_t>& b, const std::string& s)
{
    uint32_t n = static_cast<uint32_t>(s.size());
    while (n >= 0x80) { b.push_back(static_cast<uint8_t>(n | 0x80)); n >>= 7; }
    b.push_back(static_cast<uint8_t>(n));
    b.insert(b.end(), s.begin(), s.end());
}

void send_error(const std::string& msg)
{
    std::vector<uint8_t> b; b.push_back(1); put_str(b, msg);
    write_all(b.data(), b.size()); std::fflush(stdout);
}

// ---------------- 诊断 ----------------
void log_peak(int w, int h)
{
    long long mb = 0, privMb = 0;
#ifdef _WIN32
    PROCESS_MEMORY_COUNTERS pmc{};
    if (GetProcessMemoryInfo(GetCurrentProcess(), &pmc, sizeof(pmc))) {
        mb = (long long)(pmc.PeakWorkingSetSize / 1048576);
        privMb = (long long)(pmc.PeakPagefileUsage / 1048576);   // 峰值私有（提交）内存
    }
    wchar_t tmp[MAX_PATH]; DWORD n = GetTempPathW(MAX_PATH, tmp);
    if (n == 0 || n >= MAX_PATH) return;
    std::wstring path = std::wstring(tmp) + L"lst-ocr.log";
    FILE* f = _wfopen(path.c_str(), L"ab");
#else
    if (FILE* s = std::fopen("/proc/self/status", "r")) {
        char line[256];
        while (std::fgets(line, sizeof line, s)) {
            long long kb;
            if (std::sscanf(line, "VmHWM: %lld kB", &kb) == 1) mb = kb / 1024;
            if (std::sscanf(line, "VmPeak: %lld kB", &kb) == 1) privMb = kb / 1024;
        }
        std::fclose(s);
    }
    FILE* f = std::fopen("/tmp/lst-ocr.log", "ab");
#endif
    if (!f) return;
    std::fprintf(f, "[native] %dx%d  peak working set %lld MB  peak private/commit %lld MB\n", w, h, mb, privMb);
    std::fclose(f);
}

// ---------------- 图像：双线性缩放 + 归一化 ----------------
// 把 src（BGR，行宽 srcW）里的区域 (rx,ry,rw,rh) 缩放到 dw×dh，(v/255-0.5)/0.5，以 CHW 写入 dst：
// 通道平面大小 plane，行距 dstStride。
void resize_normalize(const uint8_t* src, int srcW, int rx, int ry, int rw, int rh,
                      float* dst, int dw, int dh, int dstStride, int plane)
{
    const float fx = static_cast<float>(rw) / dw, fy = static_cast<float>(rh) / dh;
    std::vector<int> xi0(dw), xi1(dw);
    std::vector<float> xw(dw);
    for (int x = 0; x < dw; ++x) {
        float s = std::max(0.f, (x + 0.5f) * fx - 0.5f);
        int i = static_cast<int>(s);
        if (i >= rw - 1) { i = rw - 1; xi0[x] = xi1[x] = (rx + i) * 3; xw[x] = 0; }
        else { xi0[x] = (rx + i) * 3; xi1[x] = (rx + i + 1) * 3; xw[x] = s - i; }
    }
    const size_t rowBytes = static_cast<size_t>(srcW) * 3;
    for (int y = 0; y < dh; ++y) {
        float s = std::max(0.f, (y + 0.5f) * fy - 0.5f);
        int i = static_cast<int>(s);
        size_t r0, r1; float wy;
        if (i >= rh - 1) { i = rh - 1; r0 = r1 = static_cast<size_t>(ry + i) * rowBytes; wy = 0; }
        else { r0 = static_cast<size_t>(ry + i) * rowBytes; r1 = static_cast<size_t>(ry + i + 1) * rowBytes; wy = s - i; }
        for (int x = 0; x < dw; ++x) {
            const float wx = xw[x];
            for (int c = 0; c < 3; ++c) {
                float top = src[r0 + xi0[x] + c] * (1 - wx) + src[r0 + xi1[x] + c] * wx;
                float bot = src[r1 + xi0[x] + c] * (1 - wx) + src[r1 + xi1[x] + c] * wx;
                dst[static_cast<size_t>(c) * plane + static_cast<size_t>(y) * dstStride + x] = (top * (1 - wy) + bot * wy) / 127.5f - 1.f;
            }
        }
    }
}

// ---------------- 引擎 ----------------
class Engine {
public:
    Engine(const ORTCHAR_T* detPath, const ORTCHAR_T* recPath, const std::string& keysPath, int threads, int bandPixels)
        : env_(ORT_LOGGING_LEVEL_WARNING, "lst-ocr"),
          bandPixels_(std::clamp(bandPixels, 60000, 1000000)),
          mem_(Ort::MemoryInfo::CreateCpu(OrtDeviceAllocator, OrtMemTypeDefault))
    {
        load_keys(keysPath);
        det_ = std::make_unique<Ort::Session>(env_, detPath, make_options(threads));
        rec_ = std::make_unique<Ort::Session>(env_, recPath, make_options(threads));
        Ort::AllocatorWithDefaultOptions alloc;
        detIn_ = det_->GetInputNameAllocated(0, alloc).get();
        detOut_ = det_->GetOutputNameAllocated(0, alloc).get();
        recIn_ = rec_->GetInputNameAllocated(0, alloc).get();
        recOut_ = rec_->GetOutputNameAllocated(0, alloc).get();
    }

    // 预热：各跑一次最小输入，完成 ORT 首次推理的一次性初始化（权重预打包、线程池、算子缓存），
    // 之后的第一次真实识别就是“热”的。用户还在选区时由父进程提前触发。
    void warmup()
    {
        std::vector<float> d(static_cast<size_t>(3) * 64 * 64, 0.f);
        const int64_t ds[4] = {1, 3, 64, 64};
        Ort::Value din = Ort::Value::CreateTensor<float>(mem_, d.data(), d.size(), ds, 4);
        const char* dIn[] = {detIn_.c_str()}; const char* dOut[] = {detOut_.c_str()};
        det_->Run(Ort::RunOptions{nullptr}, dIn, &din, 1, dOut, 1);

        std::vector<float> r(static_cast<size_t>(3) * kRecHeight * 96, 0.f);
        const int64_t rs[4] = {1, 3, kRecHeight, 96};
        Ort::Value rin = Ort::Value::CreateTensor<float>(mem_, r.data(), r.size(), rs, 4);
        const char* rIn[] = {recIn_.c_str()}; const char* rOut[] = {recOut_.c_str()};
        rec_->Run(Ort::RunOptions{nullptr}, rIn, &rin, 1, rOut, 1);
    }

    // 最近一次识别的阶段耗时（毫秒）与规模，供诊断日志使用
    double lastDetMs = 0, lastRecMs = 0; int lastBands = 0; size_t lastBoxes = 0;

    std::vector<Line> run(const uint8_t* bgr, int w, int h)
    {
        using clk = std::chrono::steady_clock;
        auto t0 = clk::now();
        std::vector<Rect> boxes = detect(bgr, w, h);
        auto t1 = clk::now();
        lastDetMs = std::chrono::duration<double, std::milli>(t1 - t0).count();
        lastBoxes = boxes.size(); lastRecMs = 0;
        if (boxes.empty()) return {};
        std::vector<Line> lines = recognize(bgr, w, h, boxes);
        lastRecMs = std::chrono::duration<double, std::milli>(clk::now() - t1).count();
        std::sort(lines.begin(), lines.end(), [](const Line& a, const Line& b) {
            int ya = a.box.y0 + a.box.h() / 2, yb = b.box.y0 + b.box.h() / 2;
            return ya != yb ? ya < yb : a.box.x0 < b.box.x0;
        });
        return lines;
    }

private:
    Ort::Env env_;
    int bandPixels_;
    Ort::MemoryInfo mem_;
    std::unique_ptr<Ort::Session> det_, rec_;
    std::string detIn_, detOut_, recIn_, recOut_;
    std::vector<std::string> chars_;   // [0]=blank，末尾=空格

    static Ort::SessionOptions make_options(int threads)
    {
        Ort::SessionOptions o;
        o.SetIntraOpNumThreads(threads);
        o.SetInterOpNumThreads(1);
        o.SetExecutionMode(ORT_SEQUENTIAL);
        switch (env_int("LST_ORT_OPT", 2)) {   // 2 = ALL（默认：典型选区比 BASIC 快 15–25%，峰值多 3–6MB）/ 1 = EXTENDED / 0 = BASIC
            case 0: o.SetGraphOptimizationLevel(ORT_ENABLE_BASIC); break;
            case 1: o.SetGraphOptimizationLevel(ORT_ENABLE_EXTENDED); break;
            default: o.SetGraphOptimizationLevel(ORT_ENABLE_ALL); break;
        }
        // 关闭线程池空转（spin-wait）：推理结束后工作线程立即休眠，不再白烧 CPU；默认开启，LST_ORT_SPIN=1 可恢复 ORT 默认行为做对比
        o.AddConfigEntry("session.intra_op.allow_spinning", env_int("LST_ORT_SPIN", 0) ? "1" : "0");
        if (!env_int("LST_ORT_ARENA", 0)) {   // 默认关闭内存池与内存模式缓存：不保留推理用的内存，用完即还
            o.DisableCpuMemArena();
            o.DisableMemPattern();
        }
        return o;
    }

    void load_keys(const std::string& path)
    {
#ifdef _WIN32
        // path 是 UTF-8：必须转成宽字符再打开，否则安装目录含中文等非 ANSI 字符时 fopen 会失败
        FILE* f = nullptr;
        {
            int wn = MultiByteToWideChar(CP_UTF8, 0, path.c_str(), -1, nullptr, 0);
            std::wstring wp(wn > 0 ? wn - 1 : 0, L'\0');
            if (wn > 0) MultiByteToWideChar(CP_UTF8, 0, path.c_str(), -1, wp.data(), wn);
            f = _wfopen(wp.c_str(), L"rb");
        }
#else
        FILE* f = std::fopen(path.c_str(), "rb");
#endif
        if (!f) throw std::runtime_error("缺少 OCR 字典文件：" + path);
        std::string data; char buf[8192]; size_t n;
        while ((n = std::fread(buf, 1, sizeof buf, f)) > 0) data.append(buf, n);
        std::fclose(f);
        if (data.size() >= 3 && (uint8_t)data[0] == 0xEF && (uint8_t)data[1] == 0xBB && (uint8_t)data[2] == 0xBF) data.erase(0, 3);
        chars_.push_back("blank");
        size_t start = 0;
        for (size_t i = 0; i <= data.size(); ++i)
            if (i == data.size() || data[i] == '\n') { chars_.push_back(data.substr(start, i - start)); start = i + 1; }
        chars_.push_back(" ");
    }

    // ---- 检测：小图一次；大图保持分辨率，切成带重叠的横向条带逐条检测，再合并跨条带的框 ----
    std::vector<Rect> detect(const uint8_t* bgr, int w, int h)
    {
        double scale = 1.0;
        if (std::max(w, h) < 480)   // 很小的图放大（小字更易识别），但放大后的像素数不超过预算：放大是峰值内存的主要来源之一
            scale = std::clamp(std::sqrt(bandPixels_ / (static_cast<double>(w) * h)), 1.0, 2.0);
        scale = std::min(scale, std::min(kMaxDetWidth / static_cast<double>(w), kMaxDetHeight / static_cast<double>(h)));
        int dw = std::max(32, static_cast<int>(std::nearbyint(w * scale / 32.0)) * 32);
        int dh = std::max(32, static_cast<int>(std::nearbyint(h * scale / 32.0)) * 32);

        int bandH = dh;
        if (static_cast<long long>(dw) * dh > static_cast<long long>(bandPixels_) * 5 / 4)
            bandH = std::min(dh, std::max(bandPixels_ < 150000 ? kMinBandHeightSmall : kMinBandHeightBig, bandPixels_ / dw / 32 * 32));
        const int overlap = bandH >= 128 ? kBandOverlapBig : kBandOverlapSmall;

        const int capH = std::min(dh, bandH + overlap);
        std::vector<float> input(static_cast<size_t>(3) * capH * dw);
        std::vector<uint8_t> mask(static_cast<size_t>(capH) * dw);
        std::vector<int> stack(static_cast<size_t>(capH) * dw);
        std::vector<Found> found;
        const double sx = static_cast<double>(w) / dw, sy = static_cast<double>(h) / dh;

        const int stride = std::max(32, bandH - overlap);
        int band = 0;
        for (int y0 = 0;; ++band) {
            int end = std::min(dh, y0 + bandH);
            if (dh - end < overlap) end = dh;
            detect_band(bgr, w, h, dw, dh, y0, end - y0, input, mask, stack, sx, sy, band, found);
            if (end >= dh || found.size() >= kMaxCandidates) break;
            y0 += stride;
        }
        lastBands = band + 1;
        if (band == 0) { std::vector<Rect> r; r.reserve(found.size()); for (auto& f : found) r.push_back(f.box); return r; }
        return merge_bands(found);
    }

    void detect_band(const uint8_t* bgr, int w, int h, int dw, int dh, int y0, int hh,
                     std::vector<float>& input, std::vector<uint8_t>& mask, std::vector<int>& stack,
                     double sx, double sy, int band, std::vector<Found>& found)
    {
        const int n = dw * hh;
        const int srcY0 = static_cast<int>(std::nearbyint(y0 * static_cast<double>(h) / dh));
        const int srcY1 = std::min(h, static_cast<int>(std::nearbyint((y0 + hh) * static_cast<double>(h) / dh)));
        resize_normalize(bgr, w, 0, srcY0, w, std::max(1, srcY1 - srcY0), input.data(), dw, hh, dw, n);

        const int64_t shape[4] = {1, 3, hh, dw};
        Ort::Value in = Ort::Value::CreateTensor<float>(mem_, input.data(), static_cast<size_t>(3) * n, shape, 4);
        const char* inNames[] = {detIn_.c_str()}; const char* outNames[] = {detOut_.c_str()};
        auto out = det_->Run(Ort::RunOptions{nullptr}, inNames, &in, 1, outNames, 1);
        auto dims = out[0].GetTensorTypeAndShapeInfo().GetShape();
        const int ph = static_cast<int>(dims[2]), pw = static_cast<int>(dims[3]);
        const float* prob = out[0].GetTensorData<float>();

        const int pn = ph * pw;
        std::fill(mask.begin(), mask.begin() + pn, 0);
        for (int y = 0; y < ph; ++y)
            for (int x = 0; x < pw; ++x) {
                bool on = prob[y * pw + x] > kDetThresh
                          || (x > 0 && prob[y * pw + x - 1] > kDetThresh)
                          || (y > 0 && prob[(y - 1) * pw + x] > kDetThresh)
                          || (x > 0 && y > 0 && prob[(y - 1) * pw + x - 1] > kDetThresh);
                if (on) mask[y * pw + x] = 1;
            }

        for (int start = 0; start < pn && found.size() < kMaxCandidates; ++start) {
            if (mask[start] != 1) continue;
            int sp = 0; stack[sp++] = start; mask[start] = 2;
            int minX = INT32_MAX, minY = INT32_MAX, maxX = -1, maxY = -1, count = 0;
            double sum = 0;
            while (sp > 0) {
                int idx = stack[--sp];
                int cy = idx / pw, cx = idx - cy * pw;
                ++count; sum += prob[idx];
                minX = std::min(minX, cx); maxX = std::max(maxX, cx);
                minY = std::min(minY, cy); maxY = std::max(maxY, cy);
                for (int dy = -1; dy <= 1; ++dy) {
                    int ny = cy + dy; if (ny < 0 || ny >= ph) continue;
                    for (int dx = -1; dx <= 1; ++dx) {
                        int nx = cx + dx; if (nx < 0 || nx >= pw) continue;
                        int ni = ny * pw + nx;
                        if (mask[ni] == 1) { mask[ni] = 2; stack[sp++] = ni; }
                    }
                }
            }
            double bw = maxX - minX + 1, bh = maxY - minY + 1;
            if (std::min(bw, bh) < 3 || sum / count < kBoxThresh) continue;
            double d = bw * bh * kUnclipRatio / (2 * (bw + bh));   // DB 的 unclip
            double x0 = minX - d, x1 = maxX + 1 + d;
            double ty0 = y0 + minY - d, ty1 = y0 + maxY + 1 + d;
            if (std::min(x1 - x0, ty1 - ty0) < 5) continue;
            int bx0 = static_cast<int>(std::max(0.0, std::nearbyint(x0 * sx)));
            int by0 = static_cast<int>(std::max(0.0, std::nearbyint(ty0 * sy)));
            int bx1 = static_cast<int>(std::min<double>(w, std::nearbyint(x1 * sx)));
            int by1 = static_cast<int>(std::min<double>(h, std::nearbyint(ty1 * sy)));
            if (bx1 - bx0 >= 2 && by1 - by0 >= 2) found.push_back({{bx0, by0, bx1, by1}, band});
        }
    }

    static bool same_text(const Rect& a, const Rect& b)
    {
        Rect i{std::max(a.x0, b.x0), std::max(a.y0, b.y0), std::min(a.x1, b.x1), std::min(a.y1, b.y1)};
        if (i.x1 <= i.x0 || i.y1 <= i.y0) return false;
        long long ia = static_cast<long long>(i.w()) * i.h();
        long long minA = std::min(static_cast<long long>(a.w()) * a.h(), static_cast<long long>(b.w()) * b.h());
        if (ia * 2 > minA) return true;
        double vr = i.h() / static_cast<double>(std::min(a.h(), b.h()));
        double hr = i.w() / static_cast<double>(std::min(a.w(), b.w()));
        return vr > 0.5 && hr > 0.3;
    }

    static std::vector<Rect> merge_bands(const std::vector<Found>& found)
    {
        const size_t n = found.size();
        std::vector<size_t> parent(n);
        for (size_t i = 0; i < n; ++i) parent[i] = i;
        auto find = [&](size_t i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; };
        for (size_t i = 0; i < n; ++i)
            for (size_t j = i + 1; j < n; ++j) {
                if (found[j].band > found[i].band + 1) break;
                if (found[j].band == found[i].band) continue;
                if (same_text(found[i].box, found[j].box)) parent[find(j)] = find(i);
            }
        std::vector<Rect> merged(n);
        std::vector<char> has(n, 0);
        std::vector<size_t> order;
        for (size_t i = 0; i < n; ++i) {
            size_t r = find(i);
            if (has[r]) {
                Rect& m = merged[r];
                m = {std::min(m.x0, found[i].box.x0), std::min(m.y0, found[i].box.y0), std::max(m.x1, found[i].box.x1), std::max(m.y1, found[i].box.y1)};
            } else { merged[r] = found[i].box; has[r] = 1; order.push_back(r); }
        }
        std::vector<Rect> out; out.reserve(order.size());
        for (size_t r : order) out.push_back(merged[r]);
        return out;
    }

    // ---- 识别 ----
    std::vector<Line> recognize(const uint8_t* bgr, int w, int /*h*/, const std::vector<Rect>& boxes)
    {
        std::vector<size_t> order(boxes.size());
        for (size_t i = 0; i < order.size(); ++i) order[i] = i;
        std::stable_sort(order.begin(), order.end(), [&](size_t a, size_t b) {
            return static_cast<double>(boxes[a].w()) / boxes[a].h() < static_cast<double>(boxes[b].w()) / boxes[b].h();
        });

        std::vector<Line> lines;
        size_t pos = 0;
        while (pos < order.size()) {
            size_t end = pos; int width = kRecMinWidth;
            while (end < order.size() && end - pos < static_cast<size_t>(kRecBatchMax)) {
                const Rect& b = boxes[order[end]];
                int wNeed = std::min(kRecMaxWidth, static_cast<int>(kRecHeight * std::max(kRecMinWidth / static_cast<double>(kRecHeight), static_cast<double>(b.w()) / b.h())));
                if (end > pos && static_cast<int>(end - pos + 1) * wNeed > kRecBatchBudget) break;
                width = wNeed; ++end;
            }
            const int count = static_cast<int>(end - pos);
            const size_t per = static_cast<size_t>(3) * kRecHeight * width;
            std::vector<float> input(per * count, 0.f);   // 未写入部分保持 0（= 归一化后的灰色补边）
            for (int k = 0; k < count; ++k) {
                const Rect& b = boxes[order[pos + k]];
                int rw = std::min(width, static_cast<int>(std::ceil(kRecHeight * static_cast<double>(b.w()) / b.h())));
                resize_normalize(bgr, w, b.x0, b.y0, b.w(), b.h(), input.data() + per * k, rw, kRecHeight, width, kRecHeight * width);
            }

            const int64_t shape[4] = {count, 3, kRecHeight, width};
            Ort::Value in = Ort::Value::CreateTensor<float>(mem_, input.data(), input.size(), shape, 4);
            const char* inNames[] = {recIn_.c_str()}; const char* outNames[] = {recOut_.c_str()};
            auto out = rec_->Run(Ort::RunOptions{nullptr}, inNames, &in, 1, outNames, 1);
            auto dims = out[0].GetTensorTypeAndShapeInfo().GetShape();
            const int steps = static_cast<int>(dims[1]), classes = static_cast<int>(dims[2]);
            const float* pr = out[0].GetTensorData<float>();

            for (int k = 0; k < count; ++k) {
                std::string text; double scoreSum = 0; int kept = 0, last = 0;
                for (int s = 0; s < steps; ++s) {
                    const float* row = pr + (static_cast<size_t>(k) * steps + s) * classes;
                    int best = 0; float bv = row[0];
                    for (int c = 1; c < classes; ++c) if (row[c] > bv) { bv = row[c]; best = c; }
                    if (best != 0 && best != last && static_cast<size_t>(best) < chars_.size()) { text += chars_[best]; scoreSum += bv; ++kept; }
                    last = best;
                }
                if (kept == 0) continue;
                float score = static_cast<float>(scoreSum / kept);
                size_t a = text.find_first_not_of(" \t\r\n"), z = text.find_last_not_of(" \t\r\n");
                if (a == std::string::npos) continue;
                text = text.substr(a, z - a + 1);
                if (score < kMinScore) continue;
                lines.push_back({boxes[order[pos + k]], score, text});
            }
            pos = end;
        }
        return lines;
    }
};

// ---------------- 动态加载 onnxruntime ----------------
#ifdef _WIN32
std::wstring exe_dir()
{
    wchar_t p[MAX_PATH * 2]; DWORD n = GetModuleFileNameW(nullptr, p, MAX_PATH * 2);
    std::wstring s(p, n); size_t k = s.find_last_of(L"\\/");
    return k == std::wstring::npos ? L"." : s.substr(0, k);
}
const OrtApiBase* load_ort_base()
{
    std::wstring dll = exe_dir() + L"\\onnxruntime.dll";
    HMODULE m = LoadLibraryExW(dll.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);   // 同目录下的 VC++ 运行库一并被找到
    if (!m) throw std::runtime_error("无法加载 onnxruntime.dll（错误码 " + std::to_string(GetLastError()) + "）。请确认与 lst-ocr.exe 在同一目录，且已安装/随附 VC++ 运行库。");
    auto fn = reinterpret_cast<const OrtApiBase* (ORT_API_CALL*)()>(GetProcAddress(m, "OrtGetApiBase"));
    if (!fn) throw std::runtime_error("onnxruntime.dll 缺少 OrtGetApiBase");
    return fn();
}
#else
std::string exe_dir()
{
    char p[PATH_MAX]; ssize_t n = readlink("/proc/self/exe", p, sizeof p - 1);
    std::string s(p, n > 0 ? n : 0); size_t k = s.find_last_of('/');
    return k == std::string::npos ? "." : s.substr(0, k);
}
const OrtApiBase* load_ort_base()
{
    std::string so = exe_dir() + "/libonnxruntime.so";
    void* m = dlopen(so.c_str(), RTLD_NOW);
    if (!m) m = dlopen("libonnxruntime.so", RTLD_NOW);
    if (!m) throw std::runtime_error(std::string("无法加载 libonnxruntime.so：") + dlerror());
    auto fn = reinterpret_cast<const OrtApiBase* (*)()>(dlsym(m, "OrtGetApiBase"));
    if (!fn) throw std::runtime_error("缺少 OrtGetApiBase");
    return fn();
}
#endif

}  // namespace

int main(int argc, char** argv)
{
#ifdef _WIN32
    _setmode(_fileno(stdin), _O_BINARY);
    _setmode(_fileno(stdout), _O_BINARY);
#endif
    if (argc < 3) { std::fprintf(stderr, "用法：lst-ocr <线程数> <单次检测像素预算> [模型目录]\n"); return 2; }
    const int threads = std::max(1, std::atoi(argv[1]));
    const int bandPixels = std::atoi(argv[2]);
    const bool log = std::getenv("LST_OCR_LOG") && std::string(std::getenv("LST_OCR_LOG")) == "1";

    std::unique_ptr<Engine> engine;
    std::string startupError;

    // 先把 ORT 加载好；失败不退出，而是对每个请求回报错误（父进程能把原因显示给用户）
    try { Ort::InitApi(load_ort_base()->GetApi(ORT_API_VERSION)); }
    catch (const std::exception& e) { startupError = e.what(); }

    for (;;) {
        int32_t hdr[5];
        if (!read_exact(&hdr[0], 4)) break;           // EOF：父进程已关闭管道或退出
        if (hdr[0] != kMagic) break;
        if (!read_exact(&hdr[1], 12)) break;
        const int32_t w = hdr[1], h = hdr[2], len = hdr[3];
        const bool warm = (w == 0 && h == 0 && len == 0);   // 预热请求：只创建并预热引擎，返回 0 行
        if (!warm && (w <= 0 || h <= 0 || static_cast<int64_t>(w) * h * 3 != len)) break;
        std::vector<uint8_t> buf(static_cast<size_t>(len));
        if (!read_exact(buf.data(), buf.size())) break;

        try {
            if (!startupError.empty()) throw std::runtime_error(startupError);
            if (!engine) {
                const auto tl = std::chrono::steady_clock::now();
#ifdef _WIN32
                std::wstring dir = argc >= 4 ? std::wstring() : exe_dir() + L"\\models";
                if (argc >= 4) { int n = MultiByteToWideChar(CP_UTF8, 0, argv[3], -1, nullptr, 0); dir.resize(n ? n - 1 : 0); MultiByteToWideChar(CP_UTF8, 0, argv[3], -1, dir.data(), n); }
                std::wstring det = dir + L"\\ch_PP-OCRv4_det_infer.onnx", rec = dir + L"\\ch_PP-OCRv4_rec_infer.onnx";
                int n2 = WideCharToMultiByte(CP_UTF8, 0, dir.c_str(), -1, nullptr, 0, nullptr, nullptr);
                std::string dir8(n2 ? n2 - 1 : 0, '\0'); WideCharToMultiByte(CP_UTF8, 0, dir.c_str(), -1, dir8.data(), n2, nullptr, nullptr);
                std::string keys = dir8 + "\\ppocr_keys_v4.txt";
                if (GetFileAttributesW(det.c_str()) == INVALID_FILE_ATTRIBUTES || GetFileAttributesW(rec.c_str()) == INVALID_FILE_ATTRIBUTES)
                    throw std::runtime_error("缺少 OCR 模型文件，目录：" + dir8);
                engine = std::make_unique<Engine>(det.c_str(), rec.c_str(), keys, threads, bandPixels);
#else
                std::string dir = argc >= 4 ? argv[3] : exe_dir() + "/models";
                engine = std::make_unique<Engine>((dir + "/ch_PP-OCRv4_det_infer.onnx").c_str(), (dir + "/ch_PP-OCRv4_rec_infer.onnx").c_str(),
                                                  dir + "/ppocr_keys_v4.txt", threads, bandPixels);
#endif
                if (log) std::fprintf(stderr, "[perf] engine load %.0fms\n", std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - tl).count());
            }
            if (warm) {
                const auto tw = std::chrono::steady_clock::now();
                engine->warmup();
                if (log) std::fprintf(stderr, "[perf] warmup %.0fms\n", std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - tw).count());
                std::vector<uint8_t> okb; okb.push_back(0); put_i32(okb, 0);
                write_all(okb.data(), okb.size()); std::fflush(stdout);
                continue;
            }
            std::vector<Line> lines = engine->run(buf.data(), w, h);
            if (log) std::fprintf(stderr, "[perf] %dx%d bands=%d boxes=%zu det=%.0fms rec=%.0fms lines=%zu\n", w, h, engine->lastBands, engine->lastBoxes, engine->lastDetMs, engine->lastRecMs, lines.size());

            std::vector<uint8_t> out; out.push_back(0); put_i32(out, static_cast<int32_t>(lines.size()));
            for (const Line& l : lines) {
                put_i32(out, l.box.x0); put_i32(out, l.box.y0); put_i32(out, l.box.w()); put_i32(out, l.box.h());
                put_f32(out, l.score); put_str(out, l.text);
            }
            write_all(out.data(), out.size()); std::fflush(stdout);
        } catch (const std::exception& e) {
            send_error(e.what());
        }
        std::vector<uint8_t>().swap(buf);
        if (log) log_peak(w, h);   // 工作集由父进程在空闲 3 秒后统一 trim，连续识别时不吃缺页
    }
    return 0;
}
