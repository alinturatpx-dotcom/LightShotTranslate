using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LST;

/// <summary>
/// PP-OCRv4 移动端模型（检测 4.7MB + 识别 10.9MB）+ ONNX Runtime（纯 CPU）。
/// 检测后处理与 CTC 解码均为纯 C# 实现，不依赖 OpenCV。
/// 内存策略：关闭 CPU 内存池和内存模式缓存，推理结束即归还，空闲时只剩模型本身。
/// 只处理水平文字（截图场景），不做 180° 方向分类。
/// </summary>
internal static class OrtOptionsExt
{
    /// <summary>线程池不空转（spin-wait）：推理结束后工作线程立即休眠，不再白烧 CPU。</summary>
    public static SessionOptions WithNoSpinning(this SessionOptions o)
    {
        o.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        return o;
    }
}

internal sealed class OnnxOcrEngine : IDisposable
{
    private const int RecHeight = 48, RecMinWidth = 96, RecMaxWidth = 3200;   // 批宽下限 96：短标签场景识别耗时降到约 1/3，文本不变（见 PERFORMANCE.md）
    private const int RecBatchMax = 6, RecBatchBudget = 1200;   // 批内 (条数 × 宽度) 上限：控制 [B,T,6625] 概率张量的内存
    private const float DetThresh = 0.3f, BoxThresh = 0.5f, UnclipRatio = 1.6f;
    private const int MaxCandidates = 1000;

    private const int MaxDetWidth = 1600, MaxDetHeight = 3200;   // 检测时图像的宽/高上限（超过才按比例缩小）
    private const int BandOverlapBig = 64, BandOverlapSmall = 32, MinBandHeightBig = 128, MinBandHeightSmall = 64;   // 分带检测：带高 ≥128 用 64 行重叠，否则 32 行（均为 32 的倍数）
    private readonly int _bandPixels;                 // 单次检测输入的像素预算：越小，识别时占用的内存越低（大图分带处理，不损失分辨率）
    private readonly InferenceSession _det, _rec;
    private readonly string _detIn, _recIn;
    private readonly string[] _chars;                // [0]=blank，末尾=空格，与识别模型输出类别一一对应

    public readonly record struct Line(Rectangle Box, float Score, string Text);

    public OnnxOcrEngine(string modelDir, int threads, int bandPixels)
    {
        _bandPixels = Math.Clamp(bandPixels, 60_000, 1_000_000);
        string detPath = Path.Combine(modelDir, "ch_PP-OCRv4_det_infer.onnx");
        string recPath = Path.Combine(modelDir, "ch_PP-OCRv4_rec_infer.onnx");
        string keysPath = Path.Combine(modelDir, "ppocr_keys_v4.txt");
        foreach (var f in new[] { detPath, recPath, keysPath })
            if (!File.Exists(f)) throw new FileNotFoundException("缺少 OCR 模型文件：" + f);

        var keys = File.ReadAllText(keysPath, System.Text.Encoding.UTF8).Split('\n');
        _chars = new string[keys.Length + 2];
        _chars[0] = "blank";
        Array.Copy(keys, 0, _chars, 1, keys.Length);
        _chars[^1] = " ";

        _det = new InferenceSession(detPath, MakeOptions(threads));
        _rec = new InferenceSession(recPath, MakeOptions(threads));
        _detIn = _det.InputMetadata.Keys.First();
        _recIn = _rec.InputMetadata.Keys.First();
    }

    private static SessionOptions MakeOptions(int threads) => new SessionOptions()
    {
        EnableCpuMemArena = false,        // 不保留推理用的内存池：用完即还
        EnableMemoryPattern = false,
        IntraOpNumThreads = threads,
        InterOpNumThreads = 1,
        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL   // 实测典型选区比 BASIC 快 15–25%，峰值多 3–6MB
    }.WithNoSpinning();

    /// <summary>预热：各跑一次最小输入，完成 ORT 首次推理的一次性初始化，之后第一次真实识别就是“热”的。</summary>
    public void Warmup()
    {
        var d = new float[3 * 64 * 64];
        using (_det.Run(new[] { NamedOnnxValue.CreateFromTensor(_detIn, new DenseTensor<float>(d.AsMemory(), new[] { 1, 3, 64, 64 })) })) { }
        var r = new float[3 * RecHeight * 96];
        using (_rec.Run(new[] { NamedOnnxValue.CreateFromTensor(_recIn, new DenseTensor<float>(r.AsMemory(), new[] { 1, 3, RecHeight, 96 })) })) { }
    }

    /// <param name="bgr">tight packed BGR，w*h*3 字节</param>
    public List<Line> Run(byte[] bgr, int w, int h, float minScore)
    {
        var boxes = Detect(bgr, w, h);
        if (boxes.Count == 0) return new List<Line>();
        var lines = Recognize(bgr, w, h, boxes, minScore);
        lines.Sort((a, b) =>
        {
            int ya = a.Box.Y + a.Box.Height / 2, yb = b.Box.Y + b.Box.Height / 2;
            return ya != yb ? ya.CompareTo(yb) : a.Box.X.CompareTo(b.Box.X);
        });
        return lines;
    }

    // ---------------- 检测 ----------------
    // 小图（≤ 像素预算）一次检测；大图保持原分辨率，切成带重叠的横向条带逐条检测，再把跨条带的框合并。
    // 这样识别时的内存只由“单条的像素预算”决定，与图片多大无关，小字也不会因整图缩小而丢失。
    private List<Rectangle> Detect(byte[] bgr, int w, int h)
    {
        double scale = 1.0;
        if (Math.Max(w, h) < 480)   // 很小的图放大（小字更易识别），但放大后的像素数不超过预算
            scale = Math.Clamp(Math.Sqrt(_bandPixels / ((double)w * h)), 1.0, 2.0);
        scale = Math.Min(scale, Math.Min(MaxDetWidth / (double)w, MaxDetHeight / (double)h));
        int dw = Math.Max(32, (int)Math.Round(w * scale / 32.0) * 32);
        int dh = Math.Max(32, (int)Math.Round(h * scale / 32.0) * 32);

        int bandH = dh;
        if ((long)dw * dh > _bandPixels * 5L / 4)
            bandH = Math.Min(dh, Math.Max(_bandPixels < 150_000 ? MinBandHeightSmall : MinBandHeightBig, _bandPixels / dw / 32 * 32));
        int overlap = bandH >= 128 ? BandOverlapBig : BandOverlapSmall;

        int capH = Math.Min(dh, bandH + overlap);       // 末尾带最多比 bandH 高 < 64 行
        var input = new float[3 * capH * dw];
        var mask = new byte[capH * dw];
        var stack = new int[capH * dw];
        var found = new List<(Rectangle Box, int Band)>();
        double sx = (double)w / dw, sy = (double)h / dh;

        int stride = Math.Max(32, bandH - overlap), band = 0;
        for (int y0 = 0; ; band++)
        {
            int end = Math.Min(dh, y0 + bandH);
            if (dh - end < overlap) end = dh;           // 避免出现很窄的末尾带
            DetectBand(bgr, w, h, dw, dh, y0, end - y0, input, mask, stack, sx, sy, band, found);
            if (end >= dh || found.Count >= MaxCandidates) break;
            y0 += stride;
        }
        return band == 0 ? found.Select(f => f.Box).ToList() : MergeAcrossBands(found);
    }

    /// <summary>检测 [y0, y0+hh) 这一条（检测网格坐标），把得到的框（原图坐标）追加到 found。</summary>
    private void DetectBand(byte[] bgr, int w, int h, int dw, int dh, int y0, int hh,
                            float[] input, byte[] mask, int[] stack, double sx, double sy, int band,
                            List<(Rectangle Box, int Band)> found)
    {
        int n = dw * hh;
        int srcY0 = (int)Math.Round(y0 * (double)h / dh);
        int srcY1 = Math.Min(h, (int)Math.Round((y0 + hh) * (double)h / dh));
        ResizeNormalize(bgr, w, 0, srcY0, w, Math.Max(1, srcY1 - srcY0), input, 0, dw, hh, dw, n);

        using var results = _det.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor(_detIn, new DenseTensor<float>(input.AsMemory(0, 3 * n), new[] { 1, 3, hh, dw }))
        });
        var t = results.First().AsTensor<float>();
        int ph = t.Dimensions[2], pw = t.Dimensions[3];
        ReadOnlySpan<float> prob = t is DenseTensor<float> dt ? dt.Buffer.Span : t.ToArray();   // 直接读推理输出，不再多拷贝一份

        // 二值化 + 2×2 膨胀
        int pn = ph * pw;
        Array.Clear(mask, 0, pn);
        for (int y = 0; y < ph; y++)
            for (int x = 0; x < pw; x++)
            {
                bool on = prob[y * pw + x] > DetThresh
                          || (x > 0 && prob[y * pw + x - 1] > DetThresh)
                          || (y > 0 && prob[(y - 1) * pw + x] > DetThresh)
                          || (x > 0 && y > 0 && prob[(y - 1) * pw + x - 1] > DetThresh);
                if (on) mask[y * pw + x] = 1;
            }

        // 8 连通域 → 外接矩形（水平文字行）。每个像素最多入栈一次。
        for (int start = 0; start < pn && found.Count < MaxCandidates; start++)
        {
            if (mask[start] != 1) continue;
            int sp = 0; stack[sp++] = start; mask[start] = 2;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, count = 0;
            double sum = 0;
            while (sp > 0)
            {
                int idx = stack[--sp];
                int cy = idx / pw, cx = idx - cy * pw;
                count++; sum += prob[idx];
                if (cx < minX) minX = cx; if (cx > maxX) maxX = cx;
                if (cy < minY) minY = cy; if (cy > maxY) maxY = cy;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = cy + dy; if (ny < 0 || ny >= ph) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = cx + dx; if (nx < 0 || nx >= pw) continue;
                        int ni = ny * pw + nx;
                        if (mask[ni] == 1) { mask[ni] = 2; stack[sp++] = ni; }
                    }
                }
            }

            double bw = maxX - minX + 1, bh = maxY - minY + 1;
            if (Math.Min(bw, bh) < 3 || sum / count < BoxThresh) continue;
            // DB 的 unclip：按 面积×系数/周长 向四周外扩（检测输出的是收缩过的文字核）
            double d = bw * bh * UnclipRatio / (2 * (bw + bh));
            double x0 = minX - d, x1 = maxX + 1 + d;
            double ty0 = y0 + minY - d, ty1 = y0 + maxY + 1 + d;   // 换算到整幅检测网格
            if (Math.Min(x1 - x0, ty1 - ty0) < 5) continue;

            int bx0 = (int)Math.Max(0, Math.Round(x0 * sx)), by0 = (int)Math.Max(0, Math.Round(ty0 * sy));
            int bx1 = (int)Math.Min(w, Math.Round(x1 * sx)), by1 = (int)Math.Min(h, Math.Round(ty1 * sy));
            if (bx1 - bx0 >= 2 && by1 - by0 >= 2)
                found.Add((Rectangle.FromLTRB(bx0, by0, bx1, by1), band));
        }
    }

    /// <summary>
    /// 合并相邻条带里指向同一行文字的框：同一行完整出现在两条里（重复）、或被条带边界切成两半（残缺）。
    /// 只比较“相邻条带”的框，同一条带内的框不合并。
    /// </summary>
    private static List<Rectangle> MergeAcrossBands(List<(Rectangle Box, int Band)> found)
    {
        int n = found.Count;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }

        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                if (found[j].Band > found[i].Band + 1) break;      // found 按条带递增追加
                if (found[j].Band == found[i].Band) continue;
                if (SameText(found[i].Box, found[j].Box)) parent[Find(j)] = Find(i);
            }

        var merged = new Dictionary<int, Rectangle>();
        var order = new List<int>();
        for (int i = 0; i < n; i++)
        {
            int r = Find(i);
            if (merged.TryGetValue(r, out var cur)) merged[r] = Rectangle.Union(cur, found[i].Box);
            else { merged[r] = found[i].Box; order.Add(r); }
        }
        return order.Select(r => merged[r]).ToList();
    }

    private static bool SameText(Rectangle a, Rectangle b)
    {
        var inter = Rectangle.Intersect(a, b);
        if (inter.IsEmpty) return false;
        long interArea = (long)inter.Width * inter.Height;
        long minArea = Math.Min((long)a.Width * a.Height, (long)b.Width * b.Height);
        if (interArea * 2 > minArea) return true;                                   // 一个框基本落在另一个里：同一行重复检测
        double vr = inter.Height / (double)Math.Min(a.Height, b.Height);
        double hr = inter.Width / (double)Math.Min(a.Width, b.Width);
        return vr > 0.5 && hr > 0.3;                                                 // 同一行被条带边界切成两截
    }

    // ---------------- 识别 ----------------
    private List<Line> Recognize(byte[] bgr, int w, int h, List<Rectangle> boxes, float minScore)
    {
        // 宽高比相近的排在一起，批内补零更少
        var order = Enumerable.Range(0, boxes.Count)
            .OrderBy(i => (double)boxes[i].Width / boxes[i].Height).ToArray();
        var lines = new List<Line>(boxes.Count);

        int pos = 0;
        while (pos < order.Length)
        {
            // 取一批：升序排列，所以最后加入的框决定批宽
            int end = pos, width = RecMinWidth;
            while (end < order.Length && end - pos < RecBatchMax)
            {
                var b = boxes[order[end]];
                int wNeed = Math.Min(RecMaxWidth, (int)(RecHeight * Math.Max(RecMinWidth / (double)RecHeight, (double)b.Width / b.Height)));
                if (end > pos && (end - pos + 1) * wNeed > RecBatchBudget) break;
                width = wNeed; end++;
            }
            int count = end - pos;

            var input = new float[count * 3 * RecHeight * width];   // 未写入部分保持 0（= 归一化后的灰色补边）
            for (int k = 0; k < count; k++)
            {
                var b = boxes[order[pos + k]];
                int rw = Math.Min(width, (int)Math.Ceiling(RecHeight * (double)b.Width / b.Height));
                ResizeNormalize(bgr, w, b.X, b.Y, b.Width, b.Height, input,
                                k * 3 * RecHeight * width, rw, RecHeight, width, RecHeight * width);
            }

            using var results = _rec.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor(_recIn, new DenseTensor<float>(input.AsMemory(), new[] { count, 3, RecHeight, width }))
            });
            var t = results.First().AsTensor<float>();
            int steps = t.Dimensions[1], classes = t.Dimensions[2];
            ReadOnlySpan<float> pr = t is DenseTensor<float> dt ? dt.Buffer.Span : t.ToArray();

            for (int k = 0; k < count; k++)
            {
                var sb = new System.Text.StringBuilder();
                double scoreSum = 0; int kept = 0, last = 0;
                for (int s = 0; s < steps; s++)
                {
                    var row = pr.Slice((k * steps + s) * classes, classes);
                    int best = 0; float bv = row[0];
                    for (int c = 1; c < row.Length; c++) if (row[c] > bv) { bv = row[c]; best = c; }
                    if (best != 0 && best != last && best < _chars.Length)
                    {
                        sb.Append(_chars[best]); scoreSum += bv; kept++;
                    }
                    last = best;
                }
                if (kept == 0) continue;
                float score = (float)(scoreSum / kept);
                var text = sb.ToString().Trim();
                if (text.Length == 0 || score < minScore) continue;
                lines.Add(new Line(boxes[order[pos + k]], score, text));
            }
            pos = end;
        }
        return lines;
    }

    /// <summary>
    /// 把 src（BGR，行宽 srcW）中的区域 (rx,ry,rw,rh) 双线性缩放到 dw×dh，
    /// 按 (v/255-0.5)/0.5 归一化，以 CHW 写入 dst：通道平面大小 plane，行距 dstStride，起点 dstOffset。
    /// </summary>
    private static void ResizeNormalize(byte[] src, int srcW, int rx, int ry, int rw, int rh,
                                        float[] dst, int dstOffset, int dw, int dh, int dstStride, int plane)
    {
        float fx = (float)rw / dw, fy = (float)rh / dh;
        var xi0 = new int[dw]; var xi1 = new int[dw]; var xw = new float[dw];
        for (int x = 0; x < dw; x++)
        {
            float s = Math.Max(0f, (x + 0.5f) * fx - 0.5f);
            int i = (int)s;
            if (i >= rw - 1) { i = rw - 1; xi0[x] = xi1[x] = (rx + i) * 3; xw[x] = 0; }
            else { xi0[x] = (rx + i) * 3; xi1[x] = (rx + i + 1) * 3; xw[x] = s - i; }
        }
        for (int y = 0; y < dh; y++)
        {
            float s = Math.Max(0f, (y + 0.5f) * fy - 0.5f);
            int i = (int)s; float wy;
            int r0, r1;
            if (i >= rh - 1) { i = rh - 1; r0 = r1 = (ry + i) * srcW * 3; wy = 0; }
            else { r0 = (ry + i) * srcW * 3; r1 = (ry + i + 1) * srcW * 3; wy = s - i; }

            for (int x = 0; x < dw; x++)
            {
                float wx = xw[x];
                for (int c = 0; c < 3; c++)
                {
                    float top = src[r0 + xi0[x] + c] * (1 - wx) + src[r0 + xi1[x] + c] * wx;
                    float bot = src[r1 + xi0[x] + c] * (1 - wx) + src[r1 + xi1[x] + c] * wx;
                    dst[dstOffset + c * plane + y * dstStride + x] = (top * (1 - wy) + bot * wy) / 127.5f - 1f;
                }
            }
        }
    }

    public void Dispose() { _det.Dispose(); _rec.Dispose(); }
}
