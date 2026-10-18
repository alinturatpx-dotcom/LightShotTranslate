namespace LST;

/// <summary>小型结果窗口：原文（可编辑，用来修正 OCR 错字）+ 译文。</summary>
internal sealed class ResultForm : Form
{
    private readonly TextBox _src;
    private readonly TextBox _dst;
    private readonly Label _status;
    private readonly Button _btnTranslate;
    private readonly Func<string, CancellationToken, Task<string>> _translate;
    private CancellationTokenSource? _cts;

    public ResultForm(string ocrText, Func<string, CancellationToken, Task<string>> translate)
    {
        _translate = translate;

        Text = "LightShot Translate";
        Icon = AppIcon.Window;
        Font = new Font("Microsoft YaHei UI", 10f);
        Size = new Size(520, 520);
        MinimumSize = new Size(360, 320);
        TopMost = true;
        KeyPreview = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(10)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _src = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Text = ocrText };
        _dst = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, ReadOnly = true, BackColor = SystemColors.Window };
        _status = new Label { AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(0, 6, 0, 0) };

        _btnTranslate = new Button { Text = "翻译", AutoSize = true };
        _btnTranslate.Click += async (_, _) => await TranslateAsync();
        var btnCopySrc = new Button { Text = "复制原文", AutoSize = true };
        btnCopySrc.Click += (_, _) => CopyText(_src.Text);
        var btnCopyDst = new Button { Text = "复制译文", AutoSize = true };
        btnCopyDst.Click += (_, _) => CopyText(_dst.Text);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        buttons.Controls.AddRange(new Control[] { _btnTranslate, btnCopySrc, btnCopyDst });

        layout.Controls.Add(new Label { Text = "原文（可直接修改）", AutoSize = true }, 0, 0);
        layout.Controls.Add(_src, 0, 1);
        layout.Controls.Add(new Label { Text = "译文", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, 2);
        layout.Controls.Add(_dst, 0, 3);
        var bottom = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 1 };
        bottom.Controls.Add(buttons);
        bottom.Controls.Add(_status);
        layout.Controls.Add(bottom, 0, 4);
        Controls.Add(layout);

        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(
            Math.Clamp(Cursor.Position.X + 12, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(Cursor.Position.Y + 12, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }

    public async Task TranslateAsync()
    {
        var text = _src.Text.Trim();
        if (text.Length == 0) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _btnTranslate.Enabled = false;
        _status.Text = "在线翻译中…（文字将发送至你配置的 API）";
        try
        {
            var result = await _translate(text, ct);
            if (ct.IsCancellationRequested) return;
            _dst.Text = result;
            _status.Text = "完成";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _status.Text = "翻译失败：" + ex.Message;
        }
        finally
        {
            if (!IsDisposed) _btnTranslate.Enabled = true;
        }
    }

    private static void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetDataObject(text, true, 5, 100); } catch { }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) Close();
        else base.OnKeyDown(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        base.OnFormClosed(e);
    }
}
