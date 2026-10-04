using System.Collections.Specialized;

namespace PackPaste;

internal sealed class TransferForm : Form
{
    readonly string[]? sources;
    readonly string? target;
    readonly Label phase = new() { AutoSize = false, Dock = DockStyle.Top, Height = 38, Text = "准备中…", Font = new Font("Microsoft YaHei UI", 13, FontStyle.Bold) };
    readonly Label detail = new() { AutoSize = false, Dock = DockStyle.Top, Height = 72, AutoEllipsis = true };
    readonly ProgressBar bar = new() { Dock = DockStyle.Top, Height = 24, Style = ProgressBarStyle.Marquee };
    readonly Label numbers = new() { Dock = DockStyle.Top, Height = 36, TextAlign = ContentAlignment.MiddleLeft };
    readonly Button cancel = new() { Text = "取消", Width = 100, Height = 34, Dock = DockStyle.Right };
    readonly CancellationTokenSource cts = new();
    bool busy = true;
    long lastUpdate;
    readonly System.Windows.Forms.Timer closeTimer = new() { Interval = 350 };
    public TransferForm(string[]? sources, string? target, bool autoStart = true)
    {
        this.sources = sources; this.target = target;
        Text = sources != null ? "打包复制" : "解包粘贴";
        ClientSize = new Size(550, 310); Padding = new Padding(24); StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = true;
        Font = new Font("Microsoft YaHei UI", 10);
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 36 }; bottom.Controls.Add(cancel);
        Controls.Add(bottom); Controls.Add(numbers); Controls.Add(bar); Controls.Add(detail); Controls.Add(phase);
        cancel.Click += (_, _) => { if (busy) { cts.Cancel(); cancel.Enabled = false; phase.Text = "正在取消…"; } else Close(); };
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; cts.Cancel(); cancel.Enabled = false; phase.Text = "正在取消…"; } };
        closeTimer.Tick += (_, _) => { closeTimer.Stop(); Close(); };
        Disposed += (_, _) => { closeTimer.Dispose(); cts.Dispose(); };
        if (autoStart) Shown += async (_, _) => await Run();
    }
    void Report(TransferProgress p)
    {
        long now = Environment.TickCount64;
        if (now - lastUpdate < 100) return;
        lastUpdate = now;
        BeginInvoke(() =>
        {
            if (!busy || cts.IsCancellationRequested) return;
            phase.Text = p.Phase; detail.Text = p.Name;
            bar.Style = p.Total > 0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
            bar.Value = p.Total > 0 ? (int)Math.Clamp(p.Done * 100.0 / p.Total, 0, 100) : 0;
            numbers.Text = p.Phase == "正在放置文件" ? $"{p.Done} / {p.Total} 项" : p.Total > 0 ? $"{bar.Value}%  ·  {FormatBytes(p.Done)} / {FormatBytes(p.Total)}" : "正在计算…";
        });
    }
    static string FormatBytes(long n) => n >= 1073741824 ? $"{n / 1073741824.0:F2} GB" : n >= 1048576 ? $"{n / 1048576.0:F1} MB" : $"{n:N0} B";
    ConflictDecision Ask(string path) => (ConflictDecision)Invoke(() =>
    {
        cts.Token.ThrowIfCancellationRequested();
        using var dialog = new ConflictForm(path);
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) => { if (cts.IsCancellationRequested) dialog.Close(); };
        timer.Start();
        if (dialog.ShowDialog(this) != DialogResult.OK) { cts.Cancel(); cts.Token.ThrowIfCancellationRequested(); }
        return dialog.Decision;
    });
    static T ClipboardRetry<T>(Func<T> action)
    {
        for (int i = 0; ; i++)
        {
            try { return action(); }
            catch (System.Runtime.InteropServices.ExternalException) when (i < 9) { Thread.Sleep(80); }
        }
    }
    static string? ClipboardArchive() => ClipboardRetry(() => Clipboard.ContainsFileDropList() ? (Clipboard.GetFileDropList() is { Count: 1 } files ? files[0] : null) : null);
    async Task Run()
    {
        using var mutex = new Mutex(false, "Local\\PackPaste.Transfer");
        bool locked = false;
        try
        {
            try { locked = mutex.WaitOne(0); } catch (AbandonedMutexException) { locked = true; }
            if (!locked) throw new IOException("已有打包或粘贴任务正在运行，请等待它完成。");
            var engine = new TransferEngine(Report, Ask, cts.Token);
            if (sources != null)
            {
                Package? previous = null;
                try { var old = ClipboardArchive(); if (old != null) previous = TransferEngine.LoadPackage(old); } catch { /* Non-PackPaste clipboard contents are unrelated. */ }
                var package = await Task.Run(() => engine.Pack(sources));
                try
                {
                    cts.Token.ThrowIfCancellationRequested();
                    ClipboardRetry(() => { var list = new StringCollection { package.Archive }; Clipboard.SetFileDropList(list); return true; });
                }
                catch { TransferEngine.DeletePackage(package); throw; }
                if (previous != null) { try { TransferEngine.DeletePackage(previous); } catch { /* Keep an in-use or changed old archive. */ } }
                Finish("打包完成", "已复制文件包。请在目标文件夹空白处右键，选择“解包粘贴”。", autoClose: true);
            }
            else
            {
                var archive = ClipboardArchive() ?? throw new IOException("剪贴板中没有本工具的文件包，请先使用“打包复制”。");
                var package = TransferEngine.LoadPackage(archive);
                await Task.Run(() => engine.Paste(package, target!));
                var warnings = new List<string>();
                // Clear only our still-current clipboard entry; never erase a newer clipboard value.
                try { if (ClipboardArchive() == archive) ClipboardRetry(() => { Clipboard.Clear(); return true; }); }
                catch (Exception ex) { warnings.Add("剪贴板清理失败：" + ex.Message); }
                try { TransferEngine.DeletePackage(package); }
                catch (Exception ex) { warnings.Add("源临时包清理失败：" + ex.Message); }
                Finish("粘贴完成", $"文件已放置到：\n{target}" + (engine.Skipped > 0 ? $"\n已按选择跳过 {engine.Skipped} 个冲突项目。" : "") + (warnings.Count > 0 ? "\n" + string.Join("\n", warnings) : ""), autoClose: warnings.Count == 0);
            }
        }
        catch (OperationCanceledException ex) { Finish("已取消", ex.Message == "The operation was canceled." ? "操作已取消。" : ex.Message); }
        catch (Exception ex) { Finish("操作未完成", ex.Message); MessageBox.Show(this, ex.Message, "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { if (locked) mutex.ReleaseMutex(); }
    }
    internal void Finish(string title, string message, bool autoClose = false)
    {
        closeTimer.Stop();
        busy = false; phase.Text = title; detail.Text = message; detail.Height = 105;
        bar.Style = ProgressBarStyle.Continuous; bar.Value = title is "打包完成" or "粘贴完成" ? 100 : 0;
        numbers.Text = ""; cancel.Text = "关闭"; cancel.Enabled = true;
        if (autoClose) closeTimer.Start();
    }
}
internal sealed class ConflictForm : Form
{
    public ConflictDecision Decision { get; private set; } = new(ConflictChoice.Skip, false);
    public ConflictForm(string path)
    {
        Text = "目标位置已存在同名项目"; ClientSize = new Size(600, 240); Padding = new Padding(20);
        Font = new Font("Microsoft YaHei UI", 10); StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        var label = new Label { Text = path + "\n\n覆盖将替换现有项目；同名文件夹会合并，并逐项处理冲突。", Dock = DockStyle.Top, Height = 110, AutoEllipsis = true };
        var apply = new CheckBox { Text = "应用到后续所有冲突", Dock = DockStyle.Top, Height = 35 };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft };
        var stop = new Button { Text = "取消任务", Width = 100, Height = 34 };
        stop.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); }; buttons.Controls.Add(stop);
        foreach (var (text, choice) in new[] { ("重命名", ConflictChoice.Rename), ("跳过", ConflictChoice.Skip), ("覆盖", ConflictChoice.Overwrite) })
        {
            var button = new Button { Text = text, Width = 100, Height = 34 };
            button.Click += (_, _) => { Decision = new(choice, apply.Checked); DialogResult = DialogResult.OK; Close(); };
            buttons.Controls.Add(button);
        }
        Controls.Add(buttons); Controls.Add(apply); Controls.Add(label);
    }
}
