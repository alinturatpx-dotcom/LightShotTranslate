using System.Runtime.CompilerServices;

namespace LST;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // OCR 工作进程：必须先于单实例检查和 WinForms 初始化，且不创建任何窗口
        if (args.Length >= 5 && args[0] == OcrWorker.Arg) return OcrWorker.Run(args);
        return RunGui();
    }

    // 单独成方法且禁止内联：OCR 子进程走不到这里，JIT 编译 Main 时也就不会去加载 WinForms / System.Drawing
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunGui()
    {
        // 单实例
        using var mutex = new Mutex(true, "LightShotTranslate.SingleInstance", out bool created);
        if (!created) return 0;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
        return 0;
    }
}
