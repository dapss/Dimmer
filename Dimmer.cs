// Tray app that dims the screen below its lowest hardware brightness.
// Uses the Windows fullscreen colour effect instead of an overlay window, and raw Win32 instead of
// WinForms to stay small. It is entirely message-driven: no timers, no polling.
// Build: build.bat (uses the C# compiler that ships with Windows).
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

static class Program
{
    const int MinLevel = 5, MaxLevel = 100;
    // Steps are finer at the dark end, where a 10% change looks like a much bigger jump.
    static readonly int[] Levels = { 100, 90, 80, 70, 60, 50, 40, 30, 25, 20, 15, 10, 5 };
    const string AppName = "Dimmer";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    const uint WM_DESTROY = 0x0002, WM_DISPLAYCHANGE = 0x007E, WM_NULL = 0x0000, WM_POWERBROADCAST = 0x0218,
        WM_WTSSESSION_CHANGE = 0x02B1, WM_HOTKEY = 0x0312, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205,
        WM_TRAY = 0x8001;
    const int PBT_APMRESUMESUSPEND = 0x07, PBT_APMRESUMEAUTOMATIC = 0x12;
    const int WTS_SESSION_LOGON = 0x5, WTS_SESSION_UNLOCK = 0x8;
    const uint MOD_ALT = 1, MOD_CONTROL = 2, VK_PRIOR = 0x21, VK_NEXT = 0x22, VK_HOME = 0x24;
    const int HotkeyBrighter = 1, HotkeyDarker = 2, HotkeyToggle = 3;
    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
    const uint MF_STRING = 0, MF_GRAYED = 1, MF_CHECKED = 8, MF_SEPARATOR = 0x800;
    const uint TPM_RIGHTBUTTON = 2, TPM_RETURNCMD = 0x100;
    const int SM_CXSMICON = 49;
    const int CmdBrighter = 1, CmdDarker = 2, CmdAutostart = 3, CmdExit = 4, CmdLevelBase = 1000;

    static int level = 100;
    static int lastDim = 50;
    static string settingsPath;
    static IntPtr hwnd, currentIcon;
    static uint taskbarCreated;
    static bool applyFailed;
    static readonly WndProc wndProc = Proc; // Held in a field so the GC cannot collect the native callback.

    [STAThread]
    static void Main()
    {
        bool first;
        using (new Mutex(true, "Dimmer.SingleInstance", out first))
        {
            if (!first) return;
            SetProcessDPIAware();

            if (!MagInitialize())
            {
                MessageBoxW(IntPtr.Zero, "Could not start the Windows magnification API.", AppName, 0x10);
                return;
            }

            settingsPath = FindSettingsPath();
            LoadSettings();
            RefreshAutostartPath();
            CreateHiddenWindow();
            taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
            WTSRegisterSessionNotification(hwnd, 0);

            Apply();
            AddTrayIcon();
            RegisterHotkeys();
            TrimMemory();

            MSG msg;
            while (GetMessageW(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }

            RemoveTrayIcon();
            WTSUnRegisterSessionNotification(hwnd);
            SetEffect(100);
            MagUninitialize();
        }
    }

    // --- Dimming ---------------------------------------------------------------------------------

    static bool SetEffect(int percent)
    {
        float s = percent / 100f;
        var matrix = new float[25];
        matrix[0] = matrix[6] = matrix[12] = s;
        matrix[18] = matrix[24] = 1f;
        return MagSetFullscreenColorEffect(matrix);
    }

    static void Apply()
    {
        applyFailed = !SetEffect(level);
    }

    static void SetLevel(int value)
    {
        value = Math.Max(MinLevel, Math.Min(MaxLevel, value));
        if (value < 100) lastDim = value;
        if (value == level) return;
        level = value;
        Apply();
        SaveSettings();
        UpdateTrayIcon();
    }

    static void Toggle()
    {
        SetLevel(level < 100 ? 100 : lastDim);
    }

    // The next level in Levels, darker (direction < 0) or brighter. Works from levels not in the list too.
    static int NextLevel(int direction)
    {
        if (direction < 0)
        {
            foreach (int value in Levels)
                if (value < level) return value;
            return MinLevel;
        }
        for (int i = Levels.Length - 1; i >= 0; i--)
            if (Levels[i] > level) return Levels[i];
        return MaxLevel;
    }

    // --- Settings --------------------------------------------------------------------------------

    // Next to the exe when that folder is writable, so Dimmer stays portable. Otherwise, such as in
    // Program Files, in the user's AppData folder.
    static string FindSettingsPath()
    {
        string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.txt");
        if (CanWrite(local)) return local;

        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
        try { Directory.CreateDirectory(folder); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return Path.Combine(folder, "settings.txt");
    }

    static bool CanWrite(string path)
    {
        try
        {
            using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite)) { }
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    static void LoadSettings()
    {
        try
        {
            string[] parts = File.ReadAllText(settingsPath).Split(' ');
            level = Math.Max(MinLevel, Math.Min(MaxLevel, int.Parse(parts[0])));
            lastDim = Math.Max(MinLevel, Math.Min(MaxLevel - 1, int.Parse(parts[1])));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (FormatException) { }
        catch (OverflowException) { }
        catch (IndexOutOfRangeException) { }
    }

    static void SaveSettings()
    {
        try { File.WriteAllText(settingsPath, level + " " + lastDim); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static bool Autostart
    {
        get
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                return key != null && key.GetValue(AppName) != null;
        }
        set
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (value)
                    key.SetValue(AppName, AutostartCommand);
                else
                    key.DeleteValue(AppName, false);
            }
        }
    }

    static string AutostartCommand
    {
        get { return "\"" + System.Reflection.Assembly.GetEntryAssembly().Location + "\""; }
    }

    // If Start with Windows is on but points somewhere else, the exe was moved: point it here instead.
    static void RefreshAutostartPath()
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (key == null) return;
                string saved = key.GetValue(AppName) as string;
                if (saved != null && !string.Equals(saved, AutostartCommand, StringComparison.OrdinalIgnoreCase))
                    key.SetValue(AppName, AutostartCommand);
            }
        }
        catch (System.Security.SecurityException) { }
        catch (UnauthorizedAccessException) { }
    }

    // --- Window and messages ---------------------------------------------------------------------

    static void CreateHiddenWindow()
    {
        IntPtr instance = GetModuleHandleW(null);
        var cls = new WNDCLASS { lpfnWndProc = wndProc, hInstance = instance, lpszClassName = "DimmerWindow" };
        RegisterClassW(ref cls);
        // A real top-level window, because message-only windows miss display and power broadcasts.
        hwnd = CreateWindowExW(0, cls.lpszClassName, AppName, 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
    }

    static IntPtr Proc(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_TRAY:
                uint mouse = (uint)lParam.ToInt64();
                if (mouse == WM_LBUTTONUP) Toggle();
                else if (mouse == WM_RBUTTONUP) ShowMenu();
                return IntPtr.Zero;

            case WM_HOTKEY:
                int id = wParam.ToInt32();
                if (id == HotkeyBrighter) SetLevel(NextLevel(1));
                else if (id == HotkeyDarker) SetLevel(NextLevel(-1));
                else if (id == HotkeyToggle) Toggle();
                return IntPtr.Zero;

            // Windows can drop the effect across these, so it is reapplied when they happen.
            case WM_DISPLAYCHANGE:
                Apply();
                break;
            case WM_POWERBROADCAST:
                int power = wParam.ToInt32();
                if (power == PBT_APMRESUMESUSPEND || power == PBT_APMRESUMEAUTOMATIC) Apply();
                break;
            case WM_WTSSESSION_CHANGE:
                int session = wParam.ToInt32();
                if (session == WTS_SESSION_UNLOCK || session == WTS_SESSION_LOGON) Apply();
                break;

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }

        if (msg == taskbarCreated && taskbarCreated != 0)
        {
            // Explorer restarted and forgot every tray icon.
            AddTrayIcon();
            return IntPtr.Zero;
        }
        return DefWindowProcW(h, msg, wParam, lParam);
    }

    static void RegisterHotkeys()
    {
        bool ok = RegisterHotKey(hwnd, HotkeyBrighter, MOD_CONTROL | MOD_ALT, VK_PRIOR);
        ok &= RegisterHotKey(hwnd, HotkeyDarker, MOD_CONTROL | MOD_ALT, VK_NEXT);
        ok &= RegisterHotKey(hwnd, HotkeyToggle, MOD_CONTROL | MOD_ALT, VK_HOME);
        if (!ok) ShowBalloon("Another app is using a Dimmer hotkey. Use the tray icon for that action.");
    }

    // --- Tray menu -------------------------------------------------------------------------------

    static void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        AppendMenuW(menu, level >= MaxLevel ? MF_GRAYED : MF_STRING, (IntPtr)CmdBrighter, "Brighter\tCtrl+Alt+PgUp");
        AppendMenuW(menu, level <= MinLevel ? MF_GRAYED : MF_STRING, (IntPtr)CmdDarker, "Darker\tCtrl+Alt+PgDn");
        AppendMenuW(menu, MF_SEPARATOR, IntPtr.Zero, null);
        foreach (int value in Levels)
        {
            string label = value == 100 ? "100% (off)\tCtrl+Alt+Home" : value + "%";
            AppendMenuW(menu, value == level ? MF_CHECKED : MF_STRING, (IntPtr)(CmdLevelBase + value), label);
        }
        AppendMenuW(menu, MF_SEPARATOR, IntPtr.Zero, null);
        AppendMenuW(menu, Autostart ? MF_CHECKED : MF_STRING, (IntPtr)CmdAutostart, "Start with Windows");
        AppendMenuW(menu, MF_STRING, (IntPtr)CmdExit, "Exit");

        POINT p;
        GetCursorPos(out p);
        // Without the foreground call and the WM_NULL afterwards, the menu won't close on an outside click.
        SetForegroundWindow(hwnd);
        int cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, p.X, p.Y, 0, hwnd, IntPtr.Zero);
        PostMessageW(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);

        if (cmd >= CmdLevelBase) SetLevel(cmd - CmdLevelBase);
        else if (cmd == CmdBrighter) SetLevel(NextLevel(1));
        else if (cmd == CmdDarker) SetLevel(NextLevel(-1));
        else if (cmd == CmdAutostart) Autostart = !Autostart;
        else if (cmd == CmdExit) DestroyWindow(hwnd);

        TrimMemory();
    }

    // --- Tray icon -------------------------------------------------------------------------------

    static NOTIFYICONDATA NewIconData(uint flags)
    {
        var data = new NOTIFYICONDATA { hWnd = hwnd, uID = 1, uFlags = flags };
        data.cbSize = (uint)Marshal.SizeOf(typeof(NOTIFYICONDATA));
        return data;
    }

    static void FillIconState(ref NOTIFYICONDATA data)
    {
        IntPtr old = currentIcon;
        currentIcon = DrawIcon(level);
        data.hIcon = currentIcon;
        data.szTip = applyFailed ? "Dimmer: could not change brightness" : level == 100 ? "Dimmer: off" : "Dimmer: " + level + "%";
        data.uCallbackMessage = WM_TRAY;
        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    static void AddTrayIcon()
    {
        NOTIFYICONDATA data = NewIconData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        FillIconState(ref data);
        Shell_NotifyIconW(NIM_ADD, ref data);
    }

    static void UpdateTrayIcon()
    {
        NOTIFYICONDATA data = NewIconData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        FillIconState(ref data);
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    static void RemoveTrayIcon()
    {
        NOTIFYICONDATA data = NewIconData(0);
        Shell_NotifyIconW(NIM_DELETE, ref data);
        if (currentIcon != IntPtr.Zero) DestroyIcon(currentIcon);
    }

    static void ShowBalloon(string text)
    {
        NOTIFYICONDATA data = NewIconData(NIF_INFO);
        data.szInfo = text;
        data.szInfoTitle = AppName;
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    // Amber ring filled from the bottom up to the current level. Amber reads on light and dark taskbars.
    static IntPtr DrawIcon(int percent)
    {
        const int Samples = 4;
        int size = GetSystemMetrics(SM_CXSMICON);
        double center = size / 2.0;
        double outer = size / 2.0 - 0.5;
        double inner = outer - Math.Max(1.2, size / 12.0);
        double fillTop = size - 0.5 - (size - 1) * percent / 100.0;

        var color = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int hits = 0;
                for (int sy = 0; sy < Samples; sy++)
                {
                    for (int sx = 0; sx < Samples; sx++)
                    {
                        double px = x + (sx + 0.5) / Samples, py = y + (sy + 0.5) / Samples;
                        double d = Math.Sqrt((px - center) * (px - center) + (py - center) * (py - center));
                        if (d <= outer && (d >= inner || py >= fillTop)) hits++;
                    }
                }
                int i = (y * size + x) * 4;
                color[i] = 0x28;     // B
                color[i + 1] = 0xB4; // G
                color[i + 2] = 0xF5; // R
                color[i + 3] = (byte)(255 * hits / (Samples * Samples));
            }
        }

        // The 1-bit mask rows are padded to 16 bits. All zero, so the alpha channel decides visibility.
        var mask = new byte[((size + 15) / 16) * 2 * size];
        return CreateIcon(IntPtr.Zero, size, size, 1, 32, mask, color);
    }

    static void TrimMemory()
    {
        SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
    }

    // --- Native API ------------------------------------------------------------------------------

    delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASS
    {
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string lpszMenuName, lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("magnification.dll")] static extern bool MagInitialize();
    [DllImport("magnification.dll")] static extern bool MagUninitialize();
    [DllImport("magnification.dll")] static extern bool MagSetFullscreenColorEffect(float[] effect);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATA data);
    [DllImport("wtsapi32.dll")] static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int flags);
    [DllImport("wtsapi32.dll")] static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassW(ref WNDCLASS cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenuW(IntPtr menu, uint flags, IntPtr id, string text);
    [DllImport("user32.dll")] static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern IntPtr CreateIcon(IntPtr instance, int w, int h, byte planes, byte bits, byte[] and, byte[] xor);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
