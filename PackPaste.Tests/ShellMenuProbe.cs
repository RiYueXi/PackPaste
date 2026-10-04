using System.Runtime.InteropServices;
using System.Text;

internal static class ShellMenuProbe
{
    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellFolder
    {
        void ParseDisplayName(IntPtr hwnd, IntPtr bind, [MarshalAs(UnmanagedType.LPWStr)] string name, out uint eaten, out IntPtr pidl, ref uint attr);
        void EnumObjects(IntPtr hwnd, uint flags, out IntPtr result);
        void BindToObject(IntPtr pidl, IntPtr bind, in Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellFolder result);
        void BindToStorage(IntPtr pidl, IntPtr bind, in Guid iid, out IntPtr result);
        [PreserveSig] int CompareIDs(IntPtr param, IntPtr a, IntPtr b);
        void CreateViewObject(IntPtr hwnd, in Guid iid, [MarshalAs(UnmanagedType.Interface)] out object result);
        void GetAttributesOf(uint count, IntPtr pidls, ref uint attrs);
        void GetUIObjectOf(IntPtr hwnd, uint count, IntPtr pidls, in Guid iid, IntPtr reserved, out IntPtr result);
        void GetDisplayNameOf(IntPtr pidl, uint flags, IntPtr result);
        void SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IntPtr result);
    }
    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellView
    {
        void GetWindow(out IntPtr hwnd);
        void ContextSensitiveHelp(bool enter);
        [PreserveSig] int TranslateAccelerator(IntPtr msg);
        void EnableModeless(bool enabled);
        void UIActivate(uint state);
        void Refresh();
        void CreateViewWindow(IntPtr previous, IntPtr settings, IntPtr browser, IntPtr rect, out IntPtr hwnd);
        void DestroyViewWindow();
        void GetCurrentInfo(IntPtr settings);
        void AddPropertySheetPages(uint reserved, IntPtr callback, IntPtr lparam);
        void SaveViewState();
        void SelectItem(IntPtr pidl, uint flags);
        void GetItemObject(uint item, in Guid iid, [MarshalAs(UnmanagedType.Interface)] out IContextMenu result);
    }
    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);
        void InvokeCommand(IntPtr info);
        void GetCommandString(UIntPtr id, uint type, IntPtr reserved, IntPtr name, uint max);
    }
    [DllImport("shell32.dll")] static extern int SHGetDesktopFolder(out IShellFolder folder);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHParseDisplayName(string name, IntPtr bind, out IntPtr pidl, uint flags, out uint attrs);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll")] static extern IntPtr GetSubMenu(IntPtr menu, int position);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetMenuString(IntPtr menu, uint item, StringBuilder text, int max, uint flags);
    [DllImport("shell32.dll")] static extern void SHChangeNotify(uint eventId, uint flags, IntPtr one, IntPtr two);
    public static void VerifySelectionModel(string path)
    {
        string name = "PackPaste.Regression-" + Guid.NewGuid().ToString("N");
        string key = @"Software\Classes\Directory\Background\shell\" + name;
        using var root = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryView.Registry64);
        try
        {
            using (var verb = root.CreateSubKey(key))
            {
                verb.SetValue("", name); verb.SetValue("MultiSelectModel", "Single");
                using var command = verb.CreateSubKey("command"); command.SetValue("", "\"" + Environment.ProcessPath + "\" --unused");
            }
            SHChangeNotify(0x08000000, 0x1000, IntPtr.Zero, IntPtr.Zero);
            if (Background(path).Contains(name)) throw new Exception("Expected old Single model to hide the background verb.");
            PackPaste.Setup.Program.ApplyPasteSelectionModel(root, key, true);
            SHChangeNotify(0x08000000, 0x1000, IntPtr.Zero, IntPtr.Zero);
            if (!Background(path).Contains(name)) throw new Exception("Corrected background verb still missing.");
            Console.WriteLine("PASS Real Windows background menu: old Single verb hidden, corrected verb visible.");
        }
        finally
        {
            root.DeleteSubKeyTree(key, false);
            SHChangeNotify(0x08000000, 0x1000, IntPtr.Zero, IntPtr.Zero);
        }
    }
    public static string[] Background(string path)
    {
        Marshal.ThrowExceptionForHR(SHGetDesktopFolder(out var desktop));
        Marshal.ThrowExceptionForHR(SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _));
        IShellFolder? folder = null; object? view = null; IContextMenu? context = null; IntPtr menu = IntPtr.Zero;
        try
        {
            desktop.BindToObject(pidl, IntPtr.Zero, typeof(IShellFolder).GUID, out folder);
            folder.CreateViewObject(IntPtr.Zero, typeof(IShellView).GUID, out view);
            ((IShellView)view).GetItemObject(0, typeof(IContextMenu).GUID, out context);
            menu = CreatePopupMenu(); Marshal.ThrowExceptionForHR(context.QueryContextMenu(menu, 0, 1, 0x7fff, 0));
            var items = new List<string>();
            void Read(IntPtr handle)
            {
                for (uint i = 0; i < GetMenuItemCount(handle); i++)
                {
                    var text = new StringBuilder(1024); GetMenuString(handle, i, text, text.Capacity, 0x400);
                    if (text.Length > 0) items.Add(text.ToString());
                    var sub = GetSubMenu(handle, (int)i); if (sub != IntPtr.Zero) Read(sub);
                }
            }
            Read(menu); return items.ToArray();
        }
        finally
        {
            if (menu != IntPtr.Zero) DestroyMenu(menu);
            if (context != null) Marshal.FinalReleaseComObject(context);
            if (view != null) Marshal.FinalReleaseComObject(view);
            if (folder != null) Marshal.FinalReleaseComObject(folder);
            Marshal.FinalReleaseComObject(desktop); Marshal.FreeCoTaskMem(pidl);
        }
    }
}
