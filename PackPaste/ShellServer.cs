using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using ComData = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace PackPaste;

[ComVisible(true), Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDropTarget
{
    [PreserveSig] int DragEnter([MarshalAs(UnmanagedType.Interface)] ComData data, uint keys, long point, ref uint effect);
    [PreserveSig] int DragOver(uint keys, long point, ref uint effect);
    [PreserveSig] int DragLeave();
    [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] ComData data, uint keys, long point, ref uint effect);
}
[ComVisible(true), Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IClassFactory
{
    [PreserveSig] int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr result);
    [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool value);
}
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class DropHandler : IDropTarget
{
    public int DragEnter(ComData data, uint keys, long point, ref uint effect) { effect = 1; return 0; }
    public int DragOver(uint keys, long point, ref uint effect) { effect = 1; return 0; }
    public int DragLeave() => 0;
    public int Drop(ComData data, uint keys, long point, ref uint effect)
    {
        effect = 0;
        try
        {
            var paths = ReadPaths(data);
            if (paths.Length > 0) ShellServer.Enqueue(paths);
            effect = 1;
            return 0;
        }
        catch (Exception ex) { return Marshal.GetHRForException(ex); }
    }
    internal static string[] ReadPaths(ComData data)
    {
            var format = new FORMATETC { cfFormat = 15, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
            data.GetData(ref format, out var medium);
            string[] paths;
            try
            {
                uint count = DragQueryFile(medium.unionmember, uint.MaxValue, null, 0);
                paths = new string[count];
                for (uint i = 0; i < count; i++)
                {
                    var buffer = new StringBuilder((int)DragQueryFile(medium.unionmember, i, null, 0) + 1);
                    DragQueryFile(medium.unionmember, i, buffer, (uint)buffer.Capacity);
                    paths[i] = buffer.ToString();
                }
            }
            finally { ReleaseStgMedium(ref medium); }
            return paths;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder? path, uint length);
    [DllImport("ole32.dll")] static extern void ReleaseStgMedium(ref STGMEDIUM medium);
}
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class Factory : IClassFactory
{
    public int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (outer != IntPtr.Zero) return unchecked((int)0x80040110);
        var unknown = Marshal.GetIUnknownForObject(new DropHandler());
        try { return Marshal.QueryInterface(unknown, in iid, out result); }
        finally { Marshal.Release(unknown); }
    }
    public int LockServer(bool value) => 0;
}
internal static class ShellServer
{
    public const string ClassId = "{91AE144F-295F-4807-93C1-F47597495B52}";
    static Control dispatcher = null!;
    static int active;
    static DateTime last = DateTime.UtcNow;
    public static void Enqueue(string[] paths)
    {
        last = DateTime.UtcNow;
        dispatcher.BeginInvoke(() =>
        {
            active++;
            var form = new TransferForm(paths, null);
            form.FormClosed += (_, _) => { active--; last = DateTime.UtcNow; };
            form.Show();
        });
    }
    public static void Run()
    {
        Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));
        using var control = new Control();
        dispatcher = control;
        _ = control.Handle;
        var clsid = new Guid(ClassId);
        var factory = new Factory();
        Marshal.ThrowExceptionForHR(CoRegisterClassObject(ref clsid, factory, 4, 1, out uint cookie));
        using var timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += (_, _) => { if (active == 0 && DateTime.UtcNow - last > TimeSpan.FromSeconds(45)) Application.ExitThread(); };
        timer.Start();
        try { Application.Run(); }
        finally { CoRevokeClassObject(cookie); OleUninitialize(); GC.KeepAlive(factory); }
    }
    [DllImport("ole32.dll")] static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] static extern void OleUninitialize();
    [DllImport("ole32.dll")] static extern int CoRegisterClassObject(ref Guid clsid, [MarshalAs(UnmanagedType.Interface)] IClassFactory factory, uint context, uint flags, out uint cookie);
    [DllImport("ole32.dll")] static extern int CoRevokeClassObject(uint cookie);
}
