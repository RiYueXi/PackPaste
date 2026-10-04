namespace PackPaste;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            if (args.Any(a => a.Equals("-Embedding", StringComparison.OrdinalIgnoreCase) || a == "--com"))
                ShellServer.Run();
            else if (args.Length >= 2 && args[0] == "copy")
                Application.Run(new TransferForm(args.Skip(1).ToArray(), null));
            else if (args.Length >= 2 && args[0] == "paste")
                Application.Run(new TransferForm(null, args[1] == "--desktop" ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) : args[1]));
            else
                MessageBox.Show("在资源管理器中选中文件或文件夹，右键 → 显示更多选项 → 打包复制。\n然后在目标文件夹空白处右键 → 解包粘贴。\n\n支持多选、进度与取消，格式为 tar.gz。", "打包复制 / 解包粘贴", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "打包复制", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
