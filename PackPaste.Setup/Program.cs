using Microsoft.Win32;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PackPaste.Setup;
internal static class Program
{
    const string Clsid = "{91AE144F-295F-4807-93C1-F47597495B52}";
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PackPaste";
    static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PackPaste");
    static string App => Path.Combine(InstallDir, "PackPaste.exe");
    static string Uninstaller => Path.Combine(InstallDir, "PackPaste.Setup.exe");
    static readonly string[] CopyVerbs = { @"*\shell\PackPaste.Copy", @"Directory\shell\PackPaste.Copy" };
    static readonly string[] PasteVerbs = { @"Directory\Background\shell\PackPaste.Paste", @"Directory\shell\PackPaste.Paste", @"Drive\shell\PackPaste.Paste", @"DesktopBackground\Shell\PackPaste.Paste" };
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            bool uninstall = args.Contains("--uninstall");
            if (uninstall && Path.GetFullPath(Environment.ProcessPath!).StartsWith(InstallDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                string temporary = Path.Combine(Path.GetTempPath(), "PackPaste-Uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
                File.Copy(Environment.ProcessPath!, temporary);
                Process.Start(new ProcessStartInfo(temporary) { UseShellExecute = true, Arguments = "--uninstall" });
                MoveFileEx(temporary, null, 4);
                return;
            }
            Application.Run(new SetupForm(uninstall));
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "打包复制安装程序", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
    static RegistryKey Machine() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
    static void Set(RegistryKey root, string key, string name, object value)
    { using var item = root.CreateSubKey(key, true); item.SetValue(name, value); }
    internal static void ApplyPasteSelectionModel(RegistryKey root, string key, bool background)
    {
        using var item = root.CreateSubKey(key, true);
        // An empty-space menu has no selected item. Single suppresses the verb.
        // Delete on upgrade as well; merely omitting the value leaves 1.0.0's restriction behind.
        if (background) item.DeleteValue("MultiSelectModel", false);
        else item.SetValue("MultiSelectModel", "Single");
    }
    static void Register()
    {
        using var machine = Machine();
        string classes = @"SOFTWARE\Classes\";
        Set(machine, classes + @"CLSID\" + Clsid, "", "PackPaste selection handler");
        Set(machine, classes + @"CLSID\" + Clsid + @"\LocalServer32", "", "\"" + App + "\" --com");
        Set(machine, classes + @"CLSID\" + Clsid + @"\LocalServer32", "ServerExecutable", App);
        foreach (var verb in CopyVerbs)
        {
            string key = classes + verb;
            Set(machine, key, "", "打包复制"); Set(machine, key, "Icon", App);
            Set(machine, key, "MultiSelectModel", "Player");
            Set(machine, key + @"\DropTarget", "CLSID", Clsid);
        }
        foreach (var verb in PasteVerbs)
        {
            string key = classes + verb;
            Set(machine, key, "", "解包粘贴"); Set(machine, key, "Icon", App);
            string location = verb.StartsWith("DesktopBackground") ? "--desktop" : verb.Contains("Background") ? "%V\\." : "%1\\.";
            Set(machine, key + @"\command", "", "\"" + App + "\" paste \"" + location + "\"");
            ApplyPasteSelectionModel(machine, key, verb.Contains("Background", StringComparison.OrdinalIgnoreCase));
        }
        Set(machine, UninstallKey, "DisplayName", "打包复制 / 解包粘贴");
        Set(machine, UninstallKey, "DisplayVersion", "1.0.1");
        Set(machine, UninstallKey, "Publisher", "PackPaste");
        Set(machine, UninstallKey, "InstallLocation", InstallDir);
        Set(machine, UninstallKey, "DisplayIcon", App);
        Set(machine, UninstallKey, "UninstallString", "\"" + Uninstaller + "\" --uninstall");
        Set(machine, UninstallKey, "NoModify", 1); Set(machine, UninstallKey, "NoRepair", 1);
        Set(machine, UninstallKey, "EstimatedSize", (int)(Directory.EnumerateFiles(InstallDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1024));
        Notify();
    }
    static void Unregister()
    {
        using var machine = Machine();
        foreach (var key in CopyVerbs.Concat(PasteVerbs).Append(@"CLSID\" + Clsid)) machine.DeleteSubKeyTree(@"SOFTWARE\Classes\" + key, false);
        machine.DeleteSubKeyTree(UninstallKey, false); Notify();
    }
    static void CheckFilesNotInUse()
    {
        if (!Directory.Exists(InstallDir)) return;
        foreach (var file in Directory.EnumerateFiles(InstallDir, "*", SearchOption.AllDirectories))
        {
            try { using var check = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None); }
            catch (IOException ex) { throw new IOException("程序正在使用中，请关闭任务窗口并等待约 45 秒后重试。", ex); }
        }
    }
    public static void Install(Action<string> status)
    {
        CheckFilesNotInUse();
        string staging = InstallDir + ".new-" + Guid.NewGuid().ToString("N");
        string backup = InstallDir + ".old-" + Guid.NewGuid().ToString("N");
        bool previous = Directory.Exists(InstallDir), moved = false, committed = false;
        try
        {
            status("正在安装程序文件…"); Directory.CreateDirectory(staging);
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip") ?? throw new IOException("安装包缺少程序文件。"))
            using (var zip = new ZipArchive(stream)) zip.ExtractToDirectory(staging);
            File.Copy(Environment.ProcessPath!, Path.Combine(staging, "PackPaste.Setup.exe"));
            if (previous) Directory.Move(InstallDir, backup);
            Directory.Move(staging, InstallDir); moved = true;
            status("正在注册所有用户的右键菜单…"); Register(); committed = true;
            if (previous) Directory.Delete(backup, true);
        }
        catch (Exception ex)
        {
            if (!committed)
            {
                if (moved) { Unregister(); Directory.Delete(InstallDir, true); }
                if (Directory.Exists(backup)) { Directory.Move(backup, InstallDir); Register(); }
            }
            throw new IOException("安装未完成。请关闭正在运行的打包/粘贴窗口，等待约 45 秒后重试。\n" + ex.Message, ex);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    public static void Uninstall(Action<string> status)
    {
        CheckFilesNotInUse();
        status("正在卸载…");
        string backup = InstallDir + ".remove-" + Guid.NewGuid().ToString("N");
        if (Directory.Exists(InstallDir)) Directory.Move(InstallDir, backup);
        try { Unregister(); }
        catch { if (Directory.Exists(backup)) Directory.Move(backup, InstallDir); Register(); throw; }
        if (Directory.Exists(backup)) Directory.Delete(backup, true);
    }
    static void Notify() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool MoveFileEx(string existing, string? replacement, uint flags);
    [DllImport("shell32.dll")] static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
internal sealed class SetupForm : Form
{
    readonly Button action = new() { Width = 140, Height = 38 };
    readonly Label label = new() { Dock = DockStyle.Fill, Padding = new Padding(0, 12, 0, 0) };
    bool running;
    public SetupForm(bool uninstall)
    {
        Text = uninstall ? "卸载打包复制" : "安装打包复制";
        Font = new Font("Microsoft YaHei UI", 10); ClientSize = new Size(540, 295); Padding = new Padding(24);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen;
        var title = new Label { Text = "打包复制 / 解包粘贴", Font = new Font(Font.FontFamily, 16, FontStyle.Bold), Dock = DockStyle.Top, Height = 42 };
        label.Text = uninstall ? "移除所有用户的右键菜单和程序文件。\n\n用户原始文件不受影响。失败或取消任务留下的临时目录需手动清理。" : "为所有用户安装到 Program Files\\PackPaste。\n\n• 文件与文件夹多选打包为 tar.gz\n• 右键解包粘贴，支持冲突处理、进度和取消\n• Windows 11：显示更多选项\n\n需要本机已有 .NET 9 Desktop Runtime (x64)。";
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft };
        action.Text = uninstall ? "卸载" : "安装";
        action.Click += async (_, _) =>
        {
            if (action.Text == "关闭") { Close(); return; }
            running = true; action.Enabled = false;
            try
            {
                Action<string> report = message => BeginInvoke(() => label.Text = message);
                await Task.Run(() => { if (uninstall) Program.Uninstall(report); else Program.Install(report); });
                label.Text = uninstall ? "卸载完成。" : "安装完成。\n\n选中文件 → 右键 → 显示更多选项 → 打包复制\n目标文件夹空白处 → 右键 → 解包粘贴\n\n无需重启，无需后台服务。";
                action.Text = "关闭";
            }
            catch (Exception ex) { label.Text = "操作未完成，可关闭占用程序后重试。"; MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { running = false; action.Enabled = true; }
        };
        footer.Controls.Add(action); Controls.Add(label); Controls.Add(footer); Controls.Add(title);
        FormClosing += (_, e) => e.Cancel = running;
    }
}
