using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WizLightWidget.Native;

public record ThumbButtonDef(uint Id, IntPtr Icon, string Tooltip);

public class TaskbarThumbnailManager
{
    private const int WM_COMMAND = 0x0111;
    private const int THBN_CLICKED = 0x1800;

    private readonly uint _wmTaskbarButtonCreated;
    private ITaskbarList3? _taskbar;
    private IntPtr _hwnd = IntPtr.Zero;
    private HwndSource? _source;
    private List<ThumbButtonDef> _buttons = new();

    public event Action<uint>? ButtonClicked;

    public TaskbarThumbnailManager()
    {
        _wmTaskbarButtonCreated = RegisterWindowMessage("TaskbarButtonCreated");
    }

    public void Attach(Window window)
    {
        var helper = new WindowInteropHelper(window);
        _hwnd = helper.EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);

        _taskbar = (ITaskbarList3)new TaskbarListCom();
        _taskbar.HrInit();
    }

    public void SetButtons(IReadOnlyList<ThumbButtonDef> defs)
    {
        _buttons = defs.ToList();
        ApplyButtons();
    }

    public void UpdateButtons(IReadOnlyList<ThumbButtonDef> defs)
    {
        _buttons = defs.ToList();
        if (_taskbar == null || _hwnd == IntPtr.Zero) return;
        var arr = _buttons.Select(ToNative).ToArray();
        _taskbar.ThumbBarUpdateButtons(_hwnd, (uint)arr.Length, arr);
    }

    private void ApplyButtons()
    {
        if (_taskbar == null || _hwnd == IntPtr.Zero || _buttons.Count == 0) return;
        var arr = _buttons.Select(ToNative).ToArray();
        _taskbar.ThumbBarAddButtons(_hwnd, (uint)arr.Length, arr);
    }

    private static ThumbButton ToNative(ThumbButtonDef b) => new()
    {
        dwMask = ThumbButtonMask.Icon | ThumbButtonMask.Tooltip | ThumbButtonMask.Flags,
        iId = b.Id,
        iBitmap = 0,
        hIcon = b.Icon,
        szTip = b.Tooltip,
        dwFlags = ThumbButtonFlags.Enabled
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)_wmTaskbarButtonCreated)
        {
            _taskbar = (ITaskbarList3)new TaskbarListCom();
            _taskbar.HrInit();
            ApplyButtons();
        }
        else if (msg == WM_COMMAND)
        {
            long w = wParam.ToInt64();
            int loword = (int)(w & 0xFFFF);
            int hiword = (int)((w >> 16) & 0xFFFF);
            if (hiword == THBN_CLICKED)
            {
                ButtonClicked?.Invoke((uint)loword);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);
}

[ComImport]
[Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
[ClassInterface(ClassInterfaceType.None)]
internal class TaskbarListCom { }

[ComImport]
[Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITaskbarList3
{
    // ITaskbarList
    [PreserveSig] void HrInit();
    [PreserveSig] void AddTab(IntPtr hwnd);
    [PreserveSig] void DeleteTab(IntPtr hwnd);
    [PreserveSig] void ActivateTab(IntPtr hwnd);
    [PreserveSig] void SetActiveAlt(IntPtr hwnd);

    // ITaskbarList2
    [PreserveSig] void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);

    // ITaskbarList3
    [PreserveSig] void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
    [PreserveSig] void SetProgressState(IntPtr hwnd, uint tbpFlags);
    [PreserveSig] void RegisterTab(IntPtr hwndTab, IntPtr hwndMDI);
    [PreserveSig] void UnregisterTab(IntPtr hwndTab);
    [PreserveSig] void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
    [PreserveSig] void SetTabActive(IntPtr hwndTab, IntPtr hwndMDI, uint tbatFlags);
    [PreserveSig] int ThumbBarAddButtons(IntPtr hwnd, uint cButtons, [MarshalAs(UnmanagedType.LPArray)] ThumbButton[] pButtons);
    [PreserveSig] int ThumbBarUpdateButtons(IntPtr hwnd, uint cButtons, [MarshalAs(UnmanagedType.LPArray)] ThumbButton[] pButtons);
    [PreserveSig] void ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
    [PreserveSig] void SetOverlayIcon(IntPtr hwnd, IntPtr hIcon, [MarshalAs(UnmanagedType.LPWStr)] string? pszDescription);
    [PreserveSig] void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string? pszTip);
    [PreserveSig] void SetThumbnailClip(IntPtr hwnd, IntPtr prcClip);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct ThumbButton
{
    public ThumbButtonMask dwMask;
    public uint iId;
    public uint iBitmap;
    public IntPtr hIcon;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string szTip;
    public ThumbButtonFlags dwFlags;
}

[Flags]
internal enum ThumbButtonMask : uint
{
    Bitmap = 0x1,
    Icon = 0x2,
    Tooltip = 0x4,
    Flags = 0x8
}

[Flags]
internal enum ThumbButtonFlags : uint
{
    Enabled = 0,
    Disabled = 0x1,
    DismissOnClick = 0x2,
    NoBackground = 0x4,
    Hidden = 0x8,
    NonInteractive = 0x10
}
