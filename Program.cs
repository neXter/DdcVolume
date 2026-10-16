// DdcVolume – tray app that controls monitor speaker volume via DDC/CI (VCP 0x62).
//
// Left-click tray icon : slider popup (mouse wheel / arrow keys work, Esc closes)
// Right-click          : monitor selection, mute, start with Windows, exit
// Hotkeys              : Ctrl+Alt+Up / Ctrl+Alt+Down / Ctrl+Alt+M (see Config)

using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DdcVolume;

static class Config
{
    public const int Step = 5;
    public const uint Modifiers = Native.MOD_CONTROL | Native.MOD_ALT;
    public const Keys VolumeUpKey = Keys.Up;
    public const Keys VolumeDownKey = Keys.Down;
    public const Keys MuteKey = Keys.M;
    public static readonly Color IconColor = Color.FromArgb(0, 103, 192);
}

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "DdcVolume.SingleInstance", out bool isNew);
        if (!isNew) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}

// ---------------------------------------------------------------------------
// Tray application
// ---------------------------------------------------------------------------
sealed class TrayApp : ApplicationContext
{
    const string AppName = "DdcVolume";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string SettingsKey = @"Software\DdcVolume";

    readonly NotifyIcon _tray = new();
    readonly ContextMenuStrip _menu = new();
    readonly ToolStripMenuItem _monitorsItem = new("Monitor");
    readonly ToolStripMenuItem _muteItem = new("Mute");
    readonly ToolStripMenuItem _autostartItem = new("Start with Windows");
    readonly ToolStripMenuItem _defaultItem = new();
    readonly VolumePopup _popup = new();
    readonly HotkeyWindow _hotkeys = new();
    readonly System.Windows.Forms.Timer _applyTimer = new() { Interval = 60 };
    readonly System.Windows.Forms.Timer _refreshTimer = new();
    readonly System.Windows.Forms.Timer _retryTimer = new() { Interval = 10_000 };
    readonly SynchronizationContext _ui;
    readonly object _ddcLock = new();

    List<DdcMonitor> _monitors = new();
    int _selected;
    int _volume;
    int _volumeBeforeMute;
    bool _muted;
    int? _startupVolume;     // persisted default, applied once at launch
    bool _startupApplied;
    volatile bool _dirty, _busy;
    IntPtr _iconHandle;
    string? _iconText;
    DateTime _popupHiddenAt;

    DdcMonitor? Current => _selected < _monitors.Count ? _monitors[_selected] : null;

    public TrayApp()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _selected = LoadSelected();

        // Menu
        _muteItem.Click += (_, _) => ToggleMute();
        if (AutoStart) AutoStart = true; // refresh stored path in case the exe moved
        _autostartItem.Checked = AutoStart;
        _autostartItem.Click += (_, _) => { AutoStart = !AutoStart; _autostartItem.Checked = AutoStart; };
        _startupVolume = LoadStartupVolume();
        _defaultItem.Click += (_, _) => ToggleStartupVolume();
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _monitorsItem,
            _muteItem,
            _defaultItem,
            _autostartItem,
            new ToolStripMenuItem("Refresh monitors", null, (_, _) => RefreshMonitors()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("About", null, (_, _) => ShowAbout()),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()),
        });
        _tray.ContextMenuStrip = _menu;
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) TogglePopup(); };

        // Popup
        _popup.Slider.ValueChanged += (_, _) => { if (!_popup.Updating) SetVolume(_popup.Slider.Value); };
        _popup.Deactivate += (_, _) => { _popup.Hide(); _popupHiddenAt = DateTime.Now; };

        // Timers
        _applyTimer.Tick += (_, _) => ApplyPending();
        _applyTimer.Start();
        _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); RefreshMonitors(); };
        _retryTimer.Tick += (_, _) => RefreshMonitors();

        // Hotkeys
        _hotkeys.Pressed += id =>
        {
            switch (id)
            {
                case 1: SetVolume(_volume + Config.Step); break;
                case 2: SetVolume(_volume - Config.Step); break;
                case 3: ToggleMute(); break;
            }
        };
        bool hotkeysOk =
            _hotkeys.Register(1, Config.VolumeUpKey, Config.Modifiers) &
            _hotkeys.Register(2, Config.VolumeDownKey, Config.Modifiers) &
            _hotkeys.Register(3, Config.MuteKey, Config.Modifiers | Native.MOD_NOREPEAT);

        // Monitor changes / wake from sleep invalidate DDC handles
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.PowerModeChanged += OnPowerChanged;

        _tray.Visible = true;
        RefreshMonitors();

        if (!hotkeysOk)
            _tray.ShowBalloonTip(3000, "DDC Volume", "Some hotkeys are already used by another app.", ToolTipIcon.Warning);
    }

    // --- Events -------------------------------------------------------------

    void OnDisplayChanged(object? s, EventArgs e) => _ui.Post(_ => DelayedRefresh(2000), null);

    void OnPowerChanged(object? s, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) _ui.Post(_ => DelayedRefresh(5000), null);
    }

    void DelayedRefresh(int ms)
    {
        _refreshTimer.Stop();
        _refreshTimer.Interval = ms;
        _refreshTimer.Start();
    }

    // --- Monitor handling ---------------------------------------------------

    void RefreshMonitors()
    {
        lock (_ddcLock)
        {
            foreach (var m in _monitors) m.Dispose();
            _monitors = DdcMonitor.EnumerateWithVolume();
        }
        if (_selected >= _monitors.Count) _selected = 0;
        ReadVolume();
        if (!_startupApplied && Current != null)
        {
            _startupApplied = true;
            if (_startupVolume is int v) SetVolume(v);
        }
        RebuildMonitorMenu();
        _retryTimer.Enabled = _monitors.Count == 0; // monitor may not be ready right after logon
        UpdateUi();
    }

    void ReadVolume()
    {
        var m = Current;
        if (m == null || _dirty || _busy) return; // don't clobber a pending user change
        lock (_ddcLock)
        {
            if (m.TryGet(DdcMonitor.VcpVolume, out uint cur, out uint max) && max > 0)
            {
                _volume = (int)cur;
                m.Max = (int)max;
            }
        }
        if (_volume > 0) _muted = false;
    }

    void SelectMonitor(int index)
    {
        _selected = index;
        SaveSelected();
        _muted = false;
        ReadVolume();
        RebuildMonitorMenu();
        UpdateUi();
    }

    void RebuildMonitorMenu()
    {
        _monitorsItem.DropDownItems.Clear();
        for (int i = 0; i < _monitors.Count; i++)
        {
            int idx = i;
            _monitorsItem.DropDownItems.Add(
                new ToolStripMenuItem($"{i + 1}: {_monitors[i].Name}", null, (_, _) => SelectMonitor(idx))
                { Checked = i == _selected });
        }
        _monitorsItem.Visible = _monitors.Count > 1;
    }

    // --- Volume -------------------------------------------------------------

    void SetVolume(int value)
    {
        var m = Current;
        if (m == null) return;
        _volume = Math.Clamp(value, 0, m.Max);
        _muted = false;
        _dirty = true; // applied by _applyTimer, coalescing rapid changes
        UpdateUi();
    }

    void ToggleMute()
    {
        if (Current == null) return;
        if (_muted)
        {
            SetVolume(_volumeBeforeMute);
        }
        else
        {
            _volumeBeforeMute = _volume;
            SetVolume(0);
            _muted = true;
            UpdateUi();
        }
    }

    // Writes only the latest requested value; DDC is slow (~50 ms per command).
    void ApplyPending()
    {
        if (!_dirty || _busy) return;
        var m = Current;
        if (m == null) { _dirty = false; return; }

        _dirty = false;
        _busy = true;
        int v = _volume;
        Task.Run(() => { lock (_ddcLock) return m.Set(DdcMonitor.VcpVolume, (uint)v); })
            .ContinueWith(t =>
            {
                _busy = false;
                if (t.Status != TaskStatus.RanToCompletion || !t.Result) DelayedRefresh(1000);
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // --- UI -----------------------------------------------------------------

    void UpdateUi()
    {
        var m = Current;
        int pct = m == null ? 0 : (int)Math.Round(_volume * 100.0 / m.Max);

        string tip = m == null
            ? "DDC Volume: no DDC/CI monitor found"
            : $"{m.Name}: {(_muted ? "muted" : pct + "%")}";
        _tray.Text = tip.Length > 120 ? tip[..120] : tip;

        SetIcon(m == null ? "–" : _muted ? "×" : pct.ToString());
        _muteItem.Checked = _muted;
        _muteItem.Enabled = m != null;
        _defaultItem.Checked = _startupVolume is not null;
        _defaultItem.Text = _startupVolume is int sv
            ? $"Startup volume: {sv}  (click to clear)"
            : $"Use {CurrentLevel} as startup volume";
        _defaultItem.Enabled = m != null || _startupVolume is not null;
        _popup.SetValue(_volume, m?.Max ?? 100, _muted);
    }

    void TogglePopup()
    {
        // Clicking the icon while the popup is open first deactivates (hides) it;
        // don't immediately reopen it.
        if (_popup.Visible || (DateTime.Now - _popupHiddenAt).TotalMilliseconds < 250)
        {
            _popup.Hide();
            return;
        }
        ReadVolume(); // pick up changes made via the monitor's own buttons
        UpdateUi();
        _popup.ShowNear(Cursor.Position);
    }

    void SetIcon(string text)
    {
        if (text == _iconText) return;
        _iconText = text;

        var size = SystemInformation.SmallIconSize;
        using var bmp = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var bg = new SolidBrush(Config.IconColor);
            g.FillRectangle(bg, 0, 0, size.Width, size.Height);

            using var sf = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            float em = size.Height * 0.8f;
            Font font = null!;
            while (true)
            {
                font = new Font("Segoe UI", em, FontStyle.Bold, GraphicsUnit.Pixel);
                if (g.MeasureString(text, font, PointF.Empty, sf).Width <= size.Width || em <= 6) break;
                font.Dispose();
                em -= 0.5f;
            }
            using (font)
                g.DrawString(text, font, Brushes.White, new RectangleF(0, 0, size.Width, size.Height), sf);
        }

        IntPtr handle = bmp.GetHicon();
        var oldIcon = _tray.Icon;
        var oldHandle = _iconHandle;
        _tray.Icon = Icon.FromHandle(handle);
        _iconHandle = handle;
        oldIcon?.Dispose();
        if (oldHandle != IntPtr.Zero) Native.DestroyIcon(oldHandle);
    }

    // --- Settings -----------------------------------------------------------

    static bool AutoStart
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(AppName) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value)
                key.SetValue(AppName, $"\"{Environment.ProcessPath ?? Application.ExecutablePath}\"");
            else
                key.DeleteValue(AppName, false);
        }
    }

    static int LoadSelected()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
        return key?.GetValue("Monitor") is int i ? i : 0;
    }

    void SaveSelected()
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
        key.SetValue("Monitor", _selected, RegistryValueKind.DWord);
    }

    // Level to save as default; when muted, use the level before muting.
    int CurrentLevel => _muted ? _volumeBeforeMute : _volume;

    void ToggleStartupVolume()
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
        if (_startupVolume is null)
        {
            _startupVolume = CurrentLevel;
            key.SetValue("StartupVolume", _startupVolume.Value, RegistryValueKind.DWord);
        }
        else
        {
            _startupVolume = null;
            key.DeleteValue("StartupVolume", false);
        }
        UpdateUi();
    }

    static int? LoadStartupVolume()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
        return key?.GetValue("StartupVolume") is int v ? v : null;
    }

    // --- About --------------------------------------------------------------

    bool _aboutOpen;

    void ShowAbout()
    {
        if (_aboutOpen) return; // avoid stacking dialogs
        _aboutOpen = true;
        var version = typeof(TrayApp).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        using var icon = LoadAppIcon();
        var page = new TaskDialogPage
        {
            Caption = "About DDC Volume",
            Heading = $"DDC Volume {version}",
            Text = "Controls monitor speaker volume via DDC/CI.\n\n" +
                   "Built by Pieter Walsweer, October 2026.",
            Footnote = new TaskDialogFootnote("Developed with the help of Claude by Anthropic."),
            Icon = icon != null ? new TaskDialogIcon(icon) : TaskDialogIcon.Information,
            Buttons = { TaskDialogButton.OK },
        };
        TaskDialog.ShowDialog(page);
        _aboutOpen = false;
    }

    // The icon embedded in the exe via <ApplicationIcon>, at the system's DPI-scaled size.
    static Icon? LoadAppIcon()
    {
        var path = Environment.ProcessPath;
        if (path == null) return null;
        try
        {
            return Icon.ExtractIcon(path, 0, SystemInformation.IconSize.Width)
                   ?? Icon.ExtractAssociatedIcon(path);
        }
        catch
        {
            return null;
        }
    }

    // --- Shutdown -----------------------------------------------------------

    protected override void ExitThreadCore()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        SystemEvents.PowerModeChanged -= OnPowerChanged;
        _applyTimer.Stop();
        _retryTimer.Stop();
        _refreshTimer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _hotkeys.Dispose();
        _popup.Dispose();
        lock (_ddcLock)
            foreach (var m in _monitors) m.Dispose();
        if (_iconHandle != IntPtr.Zero) Native.DestroyIcon(_iconHandle);
        base.ExitThreadCore();
    }
}

// ---------------------------------------------------------------------------
// Slider popup
// ---------------------------------------------------------------------------
sealed class VolumePopup : Form
{
    public readonly TrackBar Slider = new()
    {
        Minimum = 0,
        Maximum = 100,
        TickStyle = TickStyle.None,
        SmallChange = 1,
        LargeChange = 5,
        AutoSize = false,
        Dock = DockStyle.Fill,
    };

    readonly Label _label = new()
    {
        Dock = DockStyle.Right,
        Width = 52,
        TextAlign = ContentAlignment.MiddleCenter,
        Font = new Font("Segoe UI", 11f, FontStyle.Bold),
    };

    public bool Updating { get; private set; }

    public VolumePopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        BackColor = SystemColors.Window;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(270, 48);
        Padding = new Padding(10, 8, 6, 4);

        Controls.Add(Slider);
        Controls.Add(_label);
        Slider.BringToFront(); // Fill control must be docked last

        Slider.ValueChanged += (_, _) => _label.Text = Slider.Value.ToString();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
    }

    public void SetValue(int value, int max, bool muted)
    {
        Updating = true;
        Slider.Maximum = Math.Max(1, max);
        Slider.Value = Math.Clamp(value, 0, Slider.Maximum);
        Updating = false;
        _label.Text = muted ? "Mute" : Slider.Value.ToString();
    }

    public void ShowNear(Point p)
    {
        var wa = Screen.FromPoint(p).WorkingArea;
        int x = Math.Clamp(p.X - Width / 2, wa.Left, wa.Right - Width);
        int y = p.Y - Height - 12 < wa.Top ? p.Y + 12 : p.Y - Height - 12;
        y = Math.Clamp(y, wa.Top, wa.Bottom - Height);
        Location = new Point(x, y);
        Show();
        Activate();
        Slider.Focus();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2; // DWMWCP_ROUND – rounded corners on Windows 11, ignored on 10
        Native.DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
    }
}

// ---------------------------------------------------------------------------
// Global hotkeys
// ---------------------------------------------------------------------------
sealed class HotkeyWindow : NativeWindow, IDisposable
{
    readonly List<int> _ids = new();
    public event Action<int>? Pressed;

    public HotkeyWindow() => CreateHandle(new CreateParams());

    public bool Register(int id, Keys key, uint modifiers)
    {
        bool ok = Native.RegisterHotKey(Handle, id, modifiers, (uint)key);
        if (ok) _ids.Add(id);
        return ok;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY) Pressed?.Invoke((int)m.WParam);
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (var id in _ids) Native.UnregisterHotKey(Handle, id);
        DestroyHandle();
    }
}

// ---------------------------------------------------------------------------
// DDC/CI monitor wrapper
// ---------------------------------------------------------------------------
sealed class DdcMonitor : IDisposable
{
    public const byte VcpVolume = 0x62;

    public IntPtr Handle { get; }
    public string Name { get; }
    public int Max { get; set; } = 100;

    DdcMonitor(IntPtr handle, string name)
    {
        Handle = handle;
        Name = string.IsNullOrWhiteSpace(name) ? "Monitor" : name;
    }

    public bool TryGet(byte code, out uint current, out uint max) =>
        Native.GetVCPFeatureAndVCPFeatureReply(Handle, code, IntPtr.Zero, out current, out max);

    public bool Set(byte code, uint value) => Native.SetVCPFeature(Handle, code, value);

    public void Dispose() => Native.DestroyPhysicalMonitor(Handle);

    // Returns only monitors that answer a volume query.
    public static List<DdcMonitor> EnumerateWithVolume()
    {
        var hmons = new List<IntPtr>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) => { hmons.Add(h); return true; }, IntPtr.Zero);

        var result = new List<DdcMonitor>();
        foreach (var hmon in hmons)
        {
            if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(hmon, out uint count) || count == 0) continue;
            var arr = new Native.PHYSICAL_MONITOR[count];
            if (!Native.GetPhysicalMonitorsFromHMONITOR(hmon, count, arr)) continue;

            foreach (var pm in arr)
            {
                var mon = new DdcMonitor(pm.hPhysicalMonitor, pm.szPhysicalMonitorDescription);
                if (mon.Probe()) result.Add(mon);
                else mon.Dispose();
            }
        }
        return result;
    }

    bool Probe()
    {
        for (int i = 0; i < 3; i++) // some monitors drop the first DDC request
        {
            if (TryGet(VcpVolume, out _, out uint max) && max > 0)
            {
                Max = (int)max;
                return true;
            }
            Thread.Sleep(50);
        }
        return false;
    }
}

// ---------------------------------------------------------------------------
// P/Invoke
// ---------------------------------------------------------------------------
static class Native
{
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;
    public const int WM_HOTKEY = 0x0312;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint size,
        [Out] PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool DestroyPhysicalMonitor(IntPtr hMonitor);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte code, IntPtr pvct,
        out uint currentValue, out uint maximumValue);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool SetVCPFeature(IntPtr hMonitor, byte code, uint newValue);
}
