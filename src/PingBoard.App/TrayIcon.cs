using System.Runtime.InteropServices;

namespace PingBoard.App;

/// <summary>
/// System tray icon, built directly on <c>Shell_NotifyIcon</c>.
/// <para>
/// WinUI 3 ships no tray support, and the usual third-party answer (<c>H.NotifyIcon.WinUI</c>) has
/// no stable release built against Windows App SDK 2.x, whose 2.0 release changed the package
/// family name and refactored transitive NuGet dependencies. Rather than take a dependency that
/// might half-work, this owns the ~200 lines of Win32 involved — an API that has been stable since
/// Windows 2000 and cannot be broken by an SDK bump.
/// </para>
/// <para>
/// The callback target is a dedicated message-only window rather than the main window's HWND, so
/// nothing here touches WinUI's own window procedure.
/// </para>
/// </summary>
public sealed partial class TrayIcon : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int TrayCallback = WM_APP + 1;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_DESTROY = 0x0002;
    private const int WM_COMMAND = 0x0111;

    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10;
    private const uint NIIF_NONE = 0x00;

    private const uint MF_STRING = 0x0000, MF_SEPARATOR = 0x0800;
    private const uint TPM_RIGHTBUTTON = 0x0002, TPM_RETURNCMD = 0x0100;

    private const int IdShow = 1, IdExit = 2;

    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly MainWindow _window;
    private readonly WndProc _wndProc;          // held so the delegate is not collected
    private readonly string _className;
    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private bool _added;
    private bool _disposed;

    public TrayIcon(MainWindow window)
    {
        _window = window;
        _wndProc = HandleMessage;
        _className = "PingBoardTray_" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        try
        {
            CreateMessageWindow();
            LoadIcon();
            AddIcon();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

    private void CreateMessageWindow()
    {
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = _className,
        };

        if (RegisterClassEx(ref wc) == 0)
        {
            var error = Marshal.GetLastWin32Error();
            // 1410 = ERROR_CLASS_ALREADY_EXISTS, harmless on a re-create.
            if (error != 1410) throw new InvalidOperationException($"RegisterClassEx failed ({error})");
        }

        _hwnd = CreateWindowEx(0, _className, "PingBoardTray", 0, 0, 0, 0, 0,
                               HWND_MESSAGE, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()})");
    }

    private void LoadIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "pingboard.ico");

        if (File.Exists(iconPath))
        {
            // LR_LOADFROMFILE | LR_DEFAULTSIZE | LR_SHARED
            _hIcon = LoadImage(IntPtr.Zero, iconPath, 1, 0, 0, 0x0010 | 0x0040 | 0x8000);
        }

        // IDI_APPLICATION, so a missing asset still leaves a usable tray entry.
        if (_hIcon == IntPtr.Zero) _hIcon = LoadIcon(IntPtr.Zero, new IntPtr(32512));
    }

    private void AddIcon()
    {
        var data = NewData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = TrayCallback;
        data.hIcon = _hIcon;
        data.szTip = "PingBoard";

        _added = Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private NOTIFYICONDATA NewData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
    };

    /// <summary>Updates the hover tooltip — used to show the current up/down tally.</summary>
    public void SetTooltip(string text)
    {
        if (!_added || _disposed) return;

        var data = NewData();
        data.uFlags = NIF_TIP;
        // The tip field is a fixed 128-char buffer; over-long text is silently dropped by the shell.
        data.szTip = text.Length > 127 ? text[..127] : text;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    /// <summary>
    /// Balloon fallback for environments where toast registration failed, so a state change is
    /// never silent in both channels at once.
    /// </summary>
    public void Flash(string title, string body)
    {
        if (!_added || _disposed) return;

        var data = NewData();
        data.uFlags = NIF_INFO;
        data.dwInfoFlags = NIIF_NONE;
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        data.szInfo = body.Length > 255 ? body[..255] : body;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    public void ShowHiddenHint() =>
        SetTooltip("PingBoard — still monitoring. Double-click to reopen.");

    private IntPtr HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case TrayCallback:
                    switch ((int)lParam)
                    {
                        case WM_LBUTTONUP:
                            _window.DispatcherQueue.TryEnqueue(_window.BringToFront);
                            return IntPtr.Zero;

                        case WM_RBUTTONUP:
                            ShowMenu();
                            return IntPtr.Zero;
                    }
                    break;

                case WM_DESTROY:
                    return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        try
        {
            AppendMenu(menu, MF_STRING, IdShow, "Show PingBoard");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, IdExit, "Exit");

            GetCursorPos(out var point);

            // Required, or the menu will not dismiss when the user clicks elsewhere.
            SetForegroundWindow(_hwnd);

            var command = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD,
                                           point.X, point.Y, _hwnd, IntPtr.Zero);

            switch (command)
            {
                case IdShow:
                    _window.DispatcherQueue.TryEnqueue(_window.BringToFront);
                    break;

                case IdExit:
                    _window.DispatcherQueue.TryEnqueue(_window.ExitApplication);
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_added)
            {
                var data = NewData();
                Shell_NotifyIcon(NIM_DELETE, ref data);
                _added = false;
            }

            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }

            UnregisterClass(_className, GetModuleHandle(null));
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

    // ---------------------------------------------------------------- interop

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    // DllImport rather than LibraryImport throughout: NOTIFYICONDATA and WNDCLASSEX carry
    // ByValTStr and LPWStr fields that the source-generated marshaller does not support
    // (SYSLIB1051), and LibraryImport would additionally force AllowUnsafeBlocks on the whole
    // project for the sake of one file.

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, int idNewItem, string? newItem);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr overlay);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);
}
