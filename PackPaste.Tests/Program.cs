using PackPaste;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

internal static class Program
{
    static string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.test-output"));
    static int passed;
    [DllImport("ole32.dll")] static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] static extern void OleUninitialize();
    [DllImport("ole32.dll")] static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr result);
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    static TransferEngine Engine(ConflictChoice choice = ConflictChoice.Overwrite, CancellationToken token = default, Action<TransferProgress>? progress = null)
        => new(progress ?? (_ => { }), _ => new(choice, true), token);
    static string Dir(string name) { var p = Path.Combine(root, name); Directory.CreateDirectory(p); return p; }
    static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--menus")
        {
            Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));
            try { foreach (var item in ShellMenuProbe.Background(args[1])) Console.WriteLine(item); return 0; }
            finally { OleUninitialize(); }
        }
        if (args.Length == 2 && args[0] == "--menu-regression")
        {
            Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));
            try { ShellMenuProbe.VerifySelectionModel(args[1]); return 0; }
            finally { OleUninitialize(); }
        }
        Directory.CreateDirectory(root);
        root = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        TransferEngine.CacheOverride = Dir("cache");
        try
        {
            Test("Unicode, multiselect, nested and empty directories, exact binary content", () =>
            {
                var source = Dir("input/中文 文件夹"); Directory.CreateDirectory(Path.Combine(source, "空目录"));
                Directory.CreateDirectory(Path.Combine(source, "sub"));
                byte[] bytes = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 17);
                File.WriteAllBytes(Path.Combine(source, "sub", "binary.dat"), bytes);
                string loose = Path.Combine(Dir("input"), "文本.txt"); File.WriteAllText(loose, "你好 tar.gz!");
                var p = Engine().Pack(new[] { source, loose });
                Assert(TransferEngine.LoadPackage(p.Archive) == p, "metadata mismatch");
                var dest = Dir("output"); Engine().Paste(p, dest);
                Assert(File.ReadAllBytes(Path.Combine(dest, "中文 文件夹/sub/binary.dat")).SequenceEqual(bytes), "content mismatch");
                Assert(Directory.Exists(Path.Combine(dest, "中文 文件夹/空目录")), "empty dir missing");
                Assert(File.ReadAllText(Path.Combine(dest, "文本.txt")) == "你好 tar.gz!", "text mismatch");
                Assert(!Directory.EnumerateDirectories(dest, ".PackPaste-*").Any(), "destination temporary directory remains");
                TransferEngine.DeletePackage(p); Assert(!File.Exists(p.Archive), "source package remains");
            });
            Test("Parent/child selection deduplicated; empty files", () =>
            {
                var source = Dir("dedup/folder"); var file = Path.Combine(source, "empty"); File.WriteAllBytes(file, Array.Empty<byte>());
                var p = Engine().Pack(new[] { source, file }); var dest = Dir("dedup-out"); Engine().Paste(p, dest);
                Assert(!File.Exists(Path.Combine(dest, "empty")), "duplicate child selected");
                Assert(File.Exists(Path.Combine(dest, "folder/empty")), "empty file missing"); TransferEngine.DeletePackage(p);
            });
            foreach (var choice in Enum.GetValues<ConflictChoice>()) Test("Conflict " + choice, () =>
            {
                string source = Path.Combine(Dir("conflict-in-" + choice), "same.txt"); File.WriteAllText(source, "new");
                var p = Engine().Pack(new[] { source }); var dest = Dir("conflict-out-" + choice);
                File.WriteAllText(Path.Combine(dest, "same.txt"), "old"); Engine(choice).Paste(p, dest);
                Assert(File.ReadAllText(Path.Combine(dest, "same.txt")) == (choice == ConflictChoice.Overwrite ? "new" : "old"), "conflict choice incorrect");
                if (choice == ConflictChoice.Rename) Assert(File.ReadAllText(Path.Combine(dest, "same (2).txt")) == "new", "renamed missing");
                TransferEngine.DeletePackage(p);
            });
            Test("Directory merge and apply-to-all conflicts", () =>
            {
                var source = Dir("merge-in/folder"); File.WriteAllText(Path.Combine(source, "a"), "new"); File.WriteAllText(Path.Combine(source, "b"), "new");
                var dest = Dir("merge-out"); var existing = Dir("merge-out/folder"); File.WriteAllText(Path.Combine(existing, "a"), "old"); File.WriteAllText(Path.Combine(existing, "b"), "old"); File.WriteAllText(Path.Combine(existing, "keep"), "keep");
                var p = Engine().Pack(new[] { source }); int prompts = 0;
                new TransferEngine(_ => { }, _ => { prompts++; return new(ConflictChoice.Overwrite, true); }, default).Paste(p, dest);
                Assert(prompts == 1, "apply-all prompts"); Assert(File.ReadAllText(Path.Combine(existing, "keep")) == "keep", "unrelated file lost");
                Assert(File.ReadAllText(Path.Combine(existing, "b")) == "new", "merge failed"); TransferEngine.DeletePackage(p);
            });
            Test("File/directory type conflict overwrite", () =>
            {
                var source = Dir("type-in/item"); File.WriteAllText(Path.Combine(source, "x"), "new");
                var dest = Dir("type-out"); File.WriteAllText(Path.Combine(dest, "item"), "old");
                var p = Engine().Pack(new[] { source }); Engine().Paste(p, dest);
                Assert(File.ReadAllText(Path.Combine(dest, "item/x")) == "new", "type conflict failed"); TransferEngine.DeletePackage(p);
            });
            Test("Duplicate top-level names retained", () =>
            {
                string a = Path.Combine(Dir("dup-a"), "x.txt"), b = Path.Combine(Dir("dup-b"), "x.txt");
                File.WriteAllText(a, "a"); File.WriteAllText(b, "b"); var p = Engine().Pack(new[] { a, b });
                var dest = Dir("dup-out"); Engine().Paste(p, dest); Assert(Directory.GetFiles(dest).Length == 2, "duplicate root lost"); TransferEngine.DeletePackage(p);
            });
            Test("Cancel during pack removes partial package", () =>
            {
                string source = Path.Combine(Dir("cancel-in"), "large"); File.WriteAllBytes(source, RandomNumberGenerator.GetBytes(4 * 1024 * 1024));
                using var cancel = new CancellationTokenSource(); var before = Directory.GetDirectories(TransferEngine.CacheRoot).Length;
                try { Engine(token: cancel.Token, progress: p => { if (p.Phase == "正在打包压缩") cancel.Cancel(); }).Pack(new[] { source }); throw new Exception("not cancelled"); }
                catch (OperationCanceledException) { }
                Assert(before == Directory.GetDirectories(TransferEngine.CacheRoot).Length, "partial package remains");
            });
            Test("Cancel extraction preserves archive and original destination", () =>
            {
                string source = Path.Combine(Dir("cancel-paste-in"), "large"); File.WriteAllBytes(source, RandomNumberGenerator.GetBytes(4 * 1024 * 1024));
                var p = Engine().Pack(new[] { source }); var dest = Dir("cancel-paste-out"); File.WriteAllText(Path.Combine(dest, "large"), "original");
                using var cancel = new CancellationTokenSource();
                try { Engine(token: cancel.Token, progress: v => { if (v.Phase == "正在解压") cancel.Cancel(); }).Paste(p, dest); throw new Exception("not cancelled"); }
                catch (OperationCanceledException) { }
                Assert(File.Exists(p.Archive), "retry archive lost"); Assert(File.ReadAllText(Path.Combine(dest, "large")) == "original", "original changed on cancel"); TransferEngine.DeletePackage(p);
            });
            Test("Archive path traversal, ADS, device names rejected", () =>
            {
                foreach (var name in new[] { "../escape", "/absolute", "C:/absolute", "a/../../b", "a:ads", "CON", "aux.txt", "folder/./file", "trailing. ", "a\\..\\b" })
                {
                    try { TransferEngine.SafeEntryPath(root, name); throw new Exception("accepted unsafe path " + name); }
                    catch (IOException) { }
                }
            });
            Test("Malicious archive cannot write outside staging", () =>
            {
                var input = Path.Combine(Dir("evil-in"), "file"); File.WriteAllText(input, "x"); var p = Engine().Pack(new[] { input });
                using (var file = File.Create(p.Archive)) using (var gzip = new GZipStream(file, CompressionLevel.Fastest)) using (var tar = new TarWriter(gzip))
                { var entry = new PaxTarEntry(TarEntryType.RegularFile, "../../escape"); entry.DataStream = new MemoryStream(new byte[] { 1 }); tar.WriteEntry(entry); }
                try { Engine().Paste(p, Dir("evil-out")); throw new Exception("malicious accepted"); } catch (IOException) { }
                Assert(!File.Exists(Path.Combine(root, "evil-out/escape")), "path traversal"); Assert(File.Exists(p.Archive), "failed archive lost"); TransferEngine.DeletePackage(p);
            });
            Test("Corrupted archive preserves destination", () =>
            {
                var input = Path.Combine(Dir("corrupt-in"), "file"); File.WriteAllText(input, "x"); var p = Engine().Pack(new[] { input });
                File.WriteAllText(p.Archive, "not gzip"); var dest = Dir("corrupt-out");
                try { Engine().Paste(p, dest); throw new Exception("corrupt accepted"); } catch (IOException) { }
                Assert(Directory.GetFiles(dest).Length == 0, "corrupt modified destination"); TransferEngine.DeletePackage(p);
            });
            Test("Shell CF_HDROP preserves multi-selection and Unicode", () =>
            {
                var paths = new[] { Path.Combine(root, "中文 文件.txt"), Path.Combine(root, "folder") };
                var data = new DataObject(); var files = new System.Collections.Specialized.StringCollection(); files.AddRange(paths); data.SetFileDropList(files);
                var actual = DropHandler.ReadPaths((System.Runtime.InteropServices.ComTypes.IDataObject)data);
                Assert(actual.SequenceEqual(paths), "shell selection lost");
            });
            Test("Installer payload contains application and MIT license, no runtime", () =>
            {
                using var stream = typeof(PackPaste.Setup.SetupForm).Assembly.GetManifestResourceStream("payload.zip")!;
                using var zip = new ZipArchive(stream);
                Assert(zip.Entries.Count == 2 && zip.GetEntry("PackPaste.exe") != null && zip.GetEntry("LICENSE") != null, "unexpected payload");
                Assert(zip.GetEntry("PackPaste.exe")!.Length < 2_000_000, "runtime unexpectedly bundled");
                using var licenseText = new StreamReader(zip.GetEntry("LICENSE")!.Open());
                var license = licenseText.ReadToEnd();
                Assert(license.StartsWith("MIT License") && license.Contains("Copyright (c) 2026 日月汐 (RiYueXi)"), "MIT license or attribution missing");
            });
            Test("Installer and conflict UI render", () =>
            {
                Application.EnableVisualStyles();
                using var setup = new PackPaste.Setup.SetupForm(false); setup.Show(); Application.DoEvents();
                using var setupImage = new Bitmap(setup.Width, setup.Height); setup.DrawToBitmap(setupImage, new Rectangle(Point.Empty, setup.Size)); setupImage.Save(Path.Combine(root, "setup-ui.png"));
                using var conflict = new ConflictForm(@"C:\示例文件夹\同名文件.txt"); conflict.Show(); Application.DoEvents();
                using var conflictImage = new Bitmap(conflict.Width, conflict.Height); conflict.DrawToBitmap(conflictImage, new Rectangle(Point.Empty, conflict.Size)); conflictImage.Save(Path.Combine(root, "conflict-ui.png"));
            });
            Test("Published COM server activates across processes without installation", () =>
            {
                string app = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../artifacts/app/PackPaste.exe"));
                using var server = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(app, "--com") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden })!;
                Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));
                try
                {
                    var clsid = new Guid(ShellServer.ClassId); var iid = typeof(PackPaste.IDropTarget).GUID; IntPtr ptr = IntPtr.Zero; int hr = -1;
                    for (int i = 0; i < 30; i++) { hr = CoCreateInstance(ref clsid, IntPtr.Zero, 4, ref iid, out ptr); if (hr == 0) break; Thread.Sleep(100); }
                    Marshal.ThrowExceptionForHR(hr);
                    object proxy = Marshal.GetObjectForIUnknown(ptr);
                    try { uint effect = 0; Assert(((PackPaste.IDropTarget)proxy).DragOver(0, 0, ref effect) == 0 && effect == 1, "remote COM call failed");
                        var data = new DataObject(); data.SetData(DataFormats.FileDrop, new[] { Path.Combine(root, "中文.txt"), Path.Combine(root, "folder") });
                        Assert(((PackPaste.IDropTarget)proxy).DragEnter((System.Runtime.InteropServices.ComTypes.IDataObject)data, 0, 0, ref effect) == 0 && effect == 1, "remote data object marshal failed");
                        ((PackPaste.IDropTarget)proxy).DragLeave(); }
                    finally { Marshal.Release(ptr); if (Marshal.IsComObject(proxy)) Marshal.FinalReleaseComObject(proxy); }
                }
                finally { OleUninitialize(); if (!server.HasExited) { server.Kill(); server.WaitForExit(); } }
            });
            Test("COM factory exposes IDropTarget", () =>
            {
                var factory = new Factory(); var iid = typeof(PackPaste.IDropTarget).GUID;
                Assert(factory.CreateInstance(IntPtr.Zero, ref iid, out var ptr) == 0, "factory failed");
                try { var target = (PackPaste.IDropTarget)Marshal.GetObjectForIUnknown(ptr); uint effect = 0; target.DragOver(0, 0, ref effect); Assert(effect == 1, "drop effect"); }
                finally { Marshal.Release(ptr); }
                Assert(factory.CreateInstance((IntPtr)1, ref iid, out _) != 0, "aggregation accepted");
            });
            Test("Successful copy and paste windows close automatically", () =>
            {
                foreach (var title in new[] { "打包完成", "粘贴完成" })
                {
                    using var form = new TransferForm(null, root, false); form.Show();
                    form.Finish(title, "完成", autoClose: true);
                    var until = DateTime.UtcNow.AddSeconds(3);
                    while (!form.IsDisposed && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(10); }
                    Assert(form.IsDisposed, "success window did not close: " + title);
                }
            });
            Test("Failure, cancellation and cleanup warnings stay visible", () =>
            {
                foreach (var title in new[] { "操作未完成", "已取消", "粘贴完成" })
                {
                    using var form = new TransferForm(null, root, false); form.Show();
                    form.Finish(title, "需要查看的信息");
                    var until = DateTime.UtcNow.AddMilliseconds(600);
                    while (DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(10); }
                    Assert(!form.IsDisposed && form.Visible, "important result was hidden");
                }
            });
            Test("Transfer UI renders at standard DPI", () =>
            {
                Application.EnableVisualStyles(); using var form = new TransferForm(new[] { "example.txt" }, null, false);
                form.Show(); Application.DoEvents(); using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(Path.Combine(root, "transfer-ui.png"));
            });
            Console.WriteLine($"All {passed} tests passed. Output: {root}"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
