using System.Runtime.InteropServices;

namespace Frostpane.Interop;

[ComImport, Guid("6d5140c1-7436-11ce-8034-00aa006009fa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IServiceProvider
{
    [PreserveSig] int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
}

[ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellBrowser
{
    [PreserveSig] int GetWindow(out IntPtr phwnd);
    [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool fEnterMode);
    [PreserveSig] int InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);
    [PreserveSig] int SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);
    [PreserveSig] int RemoveMenusSB(IntPtr hmenuShared);
    [PreserveSig] int SetStatusTextSB(IntPtr pszStatusText);
    [PreserveSig] int EnableModelessSB([MarshalAs(UnmanagedType.Bool)] bool fEnable);
    [PreserveSig] int TranslateAcceleratorSB(IntPtr pmsg, ushort wID);
    [PreserveSig] int BrowseObject(IntPtr pidl, uint wFlags);
    [PreserveSig] int GetViewStateStream(uint grfMode, out IntPtr ppStrm);
    [PreserveSig] int GetControlWindow(uint id, out IntPtr phwnd);
    [PreserveSig] int SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr pret);
    [PreserveSig] int QueryActiveShellView(out IntPtr ppshv);
    [PreserveSig] int OnViewWindowActive(IntPtr pshv);
    [PreserveSig] int SetToolbarItems(IntPtr lpButtons, uint nButtons, uint uFlags);
}

// IShellWindows is a dual interface; the four IDispatch slots are declared explicitly so the
// vtable layout is unambiguous.
[ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellWindows
{
    [PreserveSig] int GetTypeInfoCount(out uint pctinfo);
    [PreserveSig] int GetTypeInfo(uint iTInfo, uint lcid, out IntPtr ppTInfo);
    [PreserveSig] int GetIDsOfNames(ref Guid riid, IntPtr rgszNames, uint cNames, uint lcid, IntPtr rgDispId);
    [PreserveSig] int Invoke(int dispIdMember, ref Guid riid, uint lcid, ushort wFlags, IntPtr pDispParams, IntPtr pVarResult, IntPtr pExcepInfo, IntPtr puArgErr);

    [PreserveSig] int get_Count(out int Count);
    [PreserveSig] int Item([MarshalAs(UnmanagedType.Struct)] object index, out IntPtr Folder);
    [PreserveSig] int _NewEnum(out IntPtr ppunk);
    [PreserveSig] int Register(IntPtr pid, int hwnd, int swClass, out int plCookie);
    [PreserveSig] int RegisterPending(int lThreadId, [MarshalAs(UnmanagedType.Struct)] ref object pvarloc, [MarshalAs(UnmanagedType.Struct)] ref object pvarlocRoot, int swClass, out int plCookie);
    [PreserveSig] int Revoke(int lCookie);
    [PreserveSig] int OnNavigate(int lCookie, [MarshalAs(UnmanagedType.Struct)] ref object pvarloc);
    [PreserveSig] int OnActivated(int lCookie, [MarshalAs(UnmanagedType.Bool)] bool fActive);
    [PreserveSig] int FindWindowSW([MarshalAs(UnmanagedType.Struct)] ref object pvarLoc, [MarshalAs(UnmanagedType.Struct)] ref object pvarLocRoot, int swClass, out int phwnd, int swfwOptions, out IntPtr ppdispOut);
    [PreserveSig] int OnCreated(int lCookie, IntPtr punk);
    [PreserveSig] int ProcessAttachDetach([MarshalAs(UnmanagedType.Bool)] bool fAttach);
}

[ComImport, Guid("1af3a467-214f-4298-908e-06b03e0b39f9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IFolderView2
{
    // --- IFolderView ---
    [PreserveSig] int GetCurrentViewMode(out uint pViewMode);
    [PreserveSig] int SetCurrentViewMode(uint ViewMode);
    [PreserveSig] int GetFolder(ref Guid riid, out IntPtr ppv);
    [PreserveSig] int Item(int iItemIndex, out IntPtr ppidl);
    [PreserveSig] int ItemCount(uint uFlags, out int pcItems);
    [PreserveSig] int Items(uint uFlags, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetSelectionMarkedItem(out int piItem);
    [PreserveSig] int GetFocusedItem(out int piItem);
    [PreserveSig] int GetItemPosition(IntPtr pidl, out POINT ppt);
    [PreserveSig] int GetSpacing(out POINT ppt);
    [PreserveSig] int GetDefaultSpacing(out POINT ppt);
    [PreserveSig] int GetAutoArrange();
    [PreserveSig] int SelectItem(int iItem, uint dwFlags);
    // COM interop marshals arrays as SAFEARRAY unless told otherwise; these are plain C arrays.
    [PreserveSig] int SelectAndPositionItems(uint cidl,
                                             [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
                                             [MarshalAs(UnmanagedType.LPArray)] POINT[] apt,
                                             uint dwFlags);

    // --- IFolderView2 ---
    [PreserveSig] int SetGroupBy(IntPtr key, [MarshalAs(UnmanagedType.Bool)] bool fAscending);
    [PreserveSig] int GetGroupBy(IntPtr pkey, IntPtr pfAscending);
    [PreserveSig] int SetViewProperty(IntPtr pidl, IntPtr propkey, IntPtr propvar);
    [PreserveSig] int GetViewProperty(IntPtr pidl, IntPtr propkey, IntPtr ppropvar);
    [PreserveSig] int SetTileViewProperties(IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszPropList);
    [PreserveSig] int SetExtendedTileViewProperties(IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszPropList);
    [PreserveSig] int SetText(int iType, [MarshalAs(UnmanagedType.LPWStr)] string pwszText);
    [PreserveSig] int SetCurrentFolderFlags(uint dwMask, uint dwFlags);
    [PreserveSig] int GetCurrentFolderFlags(out uint pdwFlags);
    [PreserveSig] int GetSortColumnCount(out int pcColumns);
    [PreserveSig] int SetSortColumns(IntPtr rgSortColumns, int cColumns);
    [PreserveSig] int GetSortColumns(IntPtr rgSortColumns, int cColumns);
    [PreserveSig] int GetItem(int iItem, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetVisibleItem(int iStart, [MarshalAs(UnmanagedType.Bool)] bool fPrevious, out int piItem);
    [PreserveSig] int GetSelectedItem(int iStart, out int piItem);
    [PreserveSig] int GetSelection([MarshalAs(UnmanagedType.Bool)] bool fNoneImpliesFolder, out IntPtr ppsia);
    [PreserveSig] int GetSelectionState(IntPtr pidl, out uint pdwFlags);
    [PreserveSig] int InvokeVerbOnSelection([MarshalAs(UnmanagedType.LPStr)] string? pszVerb);
    [PreserveSig] int SetViewModeAndIconSize(uint uViewMode, int iImageSize);
    [PreserveSig] int GetViewModeAndIconSize(out uint puViewMode, out int piImageSize);
    [PreserveSig] int SetGroupSubsetCount(uint cVisibleRows);
    [PreserveSig] int GetGroupSubsetCount(out uint pcVisibleRows);
    [PreserveSig] int SetRedraw([MarshalAs(UnmanagedType.Bool)] bool fRedrawOn);
    [PreserveSig] int IsMoveInSameFolder();
    [PreserveSig] int DoRename();
}

[ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItem
{
    [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetParent(out IShellItem ppsi);
    [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
    [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
    [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
}

internal static class ShellIds
{
    public const int SWC_DESKTOP = 8;
    public const int SWFO_NEEDDISPATCH = 1;

    public const uint SVSI_SELECT = 0x0001;
    public const uint SVSI_DESELECTOTHERS = 0x0004;
    public const uint SVSI_ENSUREVISIBLE = 0x0008;
    public const uint SVSI_FOCUSED = 0x0010;
    public const uint SVSI_POSITIONITEM = 0x0080;
    public const uint SVSI_NOSTATECHANGE = 0x80000000;

    public const uint FWF_AUTOARRANGE = 0x00000001;
    public const uint FWF_SNAPTOGRID = 0x00000004;

    public const uint SVGIO_ALLVIEW = 0x00000002;

    /// <summary>SIGDN_DESKTOPABSOLUTEPARSING — the full path, stable across renames of the display name.</summary>
    public const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;
    public const uint SIGDN_NORMALDISPLAY = 0x00000000;

    public static Guid CLSID_ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    public static Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    public static Guid IID_IShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    public static Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
}
