using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WinDndToggle;

/// <summary>界面语言：中文系统显示中文，其他一律英文。就十几条字符串，不值得上 resx。</summary>
internal static class Res
{
    private static readonly bool Zh = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    public static string Tr(string zh, string en) => Zh ? zh : en;
}

internal sealed class TrayContext : ApplicationContext
{
    private const int HotkeyId = 0x4E44; // 'ND'

    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly HotkeyWindow _hotkeyWindow;
    private readonly System.Windows.Forms.Timer _poll;

    private readonly Icon _iconOn = IconFactory.Build(Color.FromArgb(139, 92, 246), badge: true);
    private readonly Icon _iconOff = IconFactory.Build(Color.FromArgb(148, 163, 184), badge: false);

    private string _hotkeyText;      // 菜单里显示的，可能带 "(已被占用)" 之类的后缀
    private string _hotkeyRawText;   // 不带后缀的原始配置，重新注册时要靠它
    private uint _hotkeyMods;
    private uint _hotkeyVk;
    private bool _hotkeyRegistered;
    private bool _usable;
    private bool _warned;
    private string _lastError = "";
    private DndState _state = DndState.Unknown;

    public TrayContext(string[] args)
    {
        QuietHours.EnsureCom();

        // exe 挪过位置的话，Run 键里的旧路径会让开机自启静默失效，启动时顺手修好
        Autostart.RepairIfNeeded();

        _hotkeyRawText = _hotkeyText = LoadHotkeyText(args);

        _statusItem = new ToolStripMenuItem { Enabled = false };
        _toggleItem = new ToolStripMenuItem();
        _toggleItem.Click += (_, _) => Toggle();

        var recheckItem = new ToolStripMenuItem(Res.Tr("重新检测", "Recheck"));
        recheckItem.Click += (_, _) => Recheck();

        var autostartItem = new ToolStripMenuItem(Res.Tr("开机自启", "Run at startup"))
        {
            CheckOnClick = true,
            Checked = Autostart.IsEnabled(),
        };
        autostartItem.Click += (_, _) =>
        {
            bool enable = autostartItem.Checked;
            if (Autostart.SetEnabled(enable, out string error))
            {
                ShowBalloon(Res.Tr("已开启开机自启", "Run at startup: on"),
                    Res.Tr("登录 Windows 后会自动启动。", "The app will start with Windows."),
                    ToolTipIcon.Info);
            }
            else
            {
                // 写注册表失败就回滚勾选，别让菜单状态和实际不一致
                autostartItem.Checked = !enable;
                ShowBalloon(Res.Tr("开机自启修改失败", "Failed to change startup setting"),
                    error, ToolTipIcon.Warning);
            }
        };

        var hotkeyItem = new ToolStripMenuItem(Res.Tr("更换热键...", "Change hotkey..."));
        hotkeyItem.Click += (_, _) => ChangeHotkey();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(recheckItem);
        menu.Items.Add(autostartItem);
        menu.Items.Add(hotkeyItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(Res.Tr("退出", "Exit"), null, (_, _) => ExitApp()));
        // 每次弹出菜单时重读一下，跟上外部改动（比如任务管理器那边删了自启项）
        menu.Opening += (_, _) => autostartItem.Checked = Autostart.IsEnabled();

        _tray = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                Toggle();
            }
        };

        _hotkeyWindow = new HotkeyWindow();
        _hotkeyWindow.Pressed += Toggle;
        RegisterHotkey();
        _toggleItem.ShortcutKeyDisplayString = _hotkeyText;

        if (!Refresh())
        {
            _warned = true;
            InfoDialog.Show("WinDndToggle",
                Res.Tr("这台机器上用不了。\n\n", "Not available on this machine.\n\n") + _lastError +
                Res.Tr("\n\n程序会留在托盘里，右键菜单里可以「重新检测」。",
                       "\n\nThe app stays in the tray; use \"Recheck\" in the context menu to try again."));
        }

        // 别的入口（Win+N 铃铛、设置页、计划专注）改了状态时，图标要跟上
        _poll = new System.Windows.Forms.Timer { Interval = 3000 };
        _poll.Tick += (_, _) =>
        {
            if (_usable)
            {
                Refresh();
            }
        };
        _poll.Start();
    }

    /// <summary>读一次状态并刷新界面。返回是否可用。</summary>
    private bool Refresh()
    {
        bool ok = QuietHours.TryRead(out DndState state, out string error);
        _usable = ok;
        _lastError = error;

        if (ok)
        {
            _state = state;
        }

        Apply();
        return ok;
    }

    private void Apply()
    {
        if (!_usable)
        {
            _tray.Icon = _iconOff;
            _tray.Text = Res.Tr("勿扰：不可用", "DND: unavailable");
            _statusItem.Text = Res.Tr("勿扰：不可用", "DND: unavailable");
            _toggleItem.Text = Res.Tr("切换勿扰", "Toggle DND");
            _toggleItem.Enabled = false;
            return;
        }

        string text = _state switch
        {
            DndState.Off => Res.Tr("勿扰：已关闭", "DND: off"),
            DndState.PriorityOnly => Res.Tr("勿扰：已开启（仅优先事项）", "DND: on (priority only)"),
            DndState.AlarmsOnly => Res.Tr("勿扰：已开启（仅闹钟）", "DND: on (alarms only)"),
            _ => Res.Tr("勿扰：状态未知", "DND: unknown"),
        };

        bool on = _state is DndState.PriorityOnly or DndState.AlarmsOnly;
        _tray.Icon = on ? _iconOn : _iconOff;
        _tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        _statusItem.Text = text;
        _toggleItem.Text = on ? Res.Tr("关闭勿扰", "Turn DND off") : Res.Tr("开启勿扰", "Turn DND on");
        _toggleItem.Enabled = true;
    }

    private void Toggle()
    {
        if (!_usable && !Refresh())
        {
            if (!_warned)
            {
                _warned = true;
                InfoDialog.Show("WinDndToggle", Res.Tr("切换失败：\n\n", "Toggle failed:\n\n") + _lastError);
            }
            else
            {
                ShowBalloon(Res.Tr("切换失败", "Toggle failed"), _lastError, ToolTipIcon.Warning);
            }

            return;
        }

        if (!QuietHours.TryToggle(out DndState state, out string error))
        {
            ShowBalloon(Res.Tr("切换失败", "Toggle failed"), error, ToolTipIcon.Warning);
            Refresh();
            return;
        }

        _state = state;
        _usable = true;
        Apply();

        bool on = state is DndState.PriorityOnly or DndState.AlarmsOnly;
        ShowBalloon(on ? Res.Tr("勿扰已开启", "DND is on") : Res.Tr("勿扰已关闭", "DND is off"),
            on ? Res.Tr("通知会先攒着，不弹出来了。", "Notifications will be held silently.")
               : Res.Tr("通知恢复正常弹出。", "Notifications will pop up normally."),
            ToolTipIcon.Info);
    }

    private void Recheck()
    {
        bool ok = Refresh();
        ShowBalloon(Res.Tr("重新检测完成", "Recheck done"),
            ok ? Res.Tr("读写正常，当前：", "Read/write OK, current: ") + _statusItem.Text : _lastError,
            ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    /// <summary>
    /// 弹对话框收一个新的组合键。真正的冲突检测就是注册这一步：
    /// 注册失败（被别的程序占用）就恢复旧热键，菜单显示保持一致。
    /// </summary>
    private void ChangeHotkey()
    {
        // 对话框开着的时候先把当前热键放掉：不然按下的组合如果正好是当前热键，
        // 会被全局热键截走直接触发切换，对话框永远凑不齐按键（实测踩过）
        if (_hotkeyRegistered)
        {
            if (!HotkeyApi.UnregisterHotKey(_hotkeyWindow.Handle, HotkeyId))
            {
                ShowBalloon(Res.Tr("更换热键失败", "Hotkey change failed"),
                    Res.Tr("无法释放当前热键，操作已取消。", "Could not release the current hotkey; operation cancelled."),
                    ToolTipIcon.Warning);
                return;
            }

            _hotkeyRegistered = false;
        }

        bool changed = false;
        try
        {
            using var dlg = new HotkeyDialog(_hotkeyRawText);
            if (dlg.ShowDialog() == DialogResult.OK && dlg.Result is not null)
            {
                string text = dlg.Result;
                if (!HotkeyParser.TryParse(text, out uint mods, out uint vk))
                {
                    ShowBalloon(Res.Tr("更换热键失败", "Hotkey change failed"),
                        Res.Tr("无法解析这个组合键。", "That combination could not be parsed."),
                        ToolTipIcon.Warning);
                }
                else if (!RegisterSingle(mods, vk))
                {
                    ShowBalloon(Res.Tr("热键已被占用", "Hotkey already in use"),
                        Res.Tr("这个组合键被其他程序占用了，原来的热键保持不变。",
                               "That combination is taken by another app. Your previous hotkey is unchanged."),
                        ToolTipIcon.Warning);
                }
                else
                {
                    _hotkeyMods = mods;
                    _hotkeyVk = vk;
                    _hotkeyRawText = text;
                    _hotkeyText = text;
                    _toggleItem.ShortcutKeyDisplayString = _hotkeyText;
                    changed = true;

                    if (SaveHotkeyToRegistry(text))
                    {
                        ShowBalloon(Res.Tr("热键已更换", "Hotkey changed"), text, ToolTipIcon.Info);
                    }
                    else
                    {
                        ShowBalloon(Res.Tr("热键已更换", "Hotkey changed"),
                            Res.Tr("本次运行已生效，但写入注册表失败，下次启动会回到旧热键。",
                                   "Active for this session, but saving to the registry failed; the old hotkey returns next launch."),
                            ToolTipIcon.Warning);
                    }
                }
            }
        }
        finally
        {
            if (changed)
            {
                _hotkeyRegistered = true;
            }
            else
            {
                // 取消或失败：把旧热键装回去。RegisterSingle 会先清同 id 的条目再注册，
                // 就算之前有叠加的残留也会一起清掉
                if (HotkeyParser.TryParse(_hotkeyRawText, out uint om, out uint ov))
                {
                    _hotkeyRegistered = RegisterSingle(om, ov);
                }
            }
        }
    }

    private void ShowBalloon(string title, string text, ToolTipIcon icon)
    {
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = text;
        _tray.BalloonTipIcon = icon;
        _tray.ShowBalloonTip(2500);
    }

    /// <summary>
    /// 同一个 (hWnd, id) 在 Windows 里注册不同组合会「追加」而不是替换（实测两个组合会同时生效），
    /// 所以每次注册前先清掉同 id 的旧条目，保证系统里至多挂着一个组合。
    /// </summary>
    private bool RegisterSingle(uint mods, uint vk)
    {
        HotkeyApi.UnregisterHotKey(_hotkeyWindow.Handle, HotkeyId);
        return HotkeyApi.RegisterHotKey(_hotkeyWindow.Handle, HotkeyId, mods | HotkeyApi.MOD_NOREPEAT, vk);
    }

    private void RegisterHotkey()
    {
        if (!HotkeyParser.TryParse(_hotkeyText, out _hotkeyMods, out _hotkeyVk))
        {
            _hotkeyText = _hotkeyRawText + " " + Res.Tr("(配置无效)", "(invalid config)");
            return;
        }

        _hotkeyRegistered = RegisterSingle(_hotkeyMods, _hotkeyVk);

        if (!_hotkeyRegistered)
        {
            _hotkeyText = _hotkeyRawText + " " + Res.Tr("(已被占用)", "(already in use)");
            // 默认键被别的程序占是首次用户的常见情况，光在菜单里标个小字不够，主动说一声
            ShowBalloon(Res.Tr("热键不可用", "Hotkey unavailable"),
                Res.Tr("{0} 已被其他程序占用，热键没有生效。右键托盘图标，用「更换热键...」换一个组合。",
                       "{0} is taken by another app, so the hotkey is inactive. Right-click the tray icon and use \"Change hotkey...\" to pick another.")
                    .Replace("{0}", _hotkeyRawText),
                ToolTipIcon.Warning);
        }
    }

    private void ExitApp()
    {
        _poll.Stop();
        _poll.Dispose();

        if (_hotkeyRegistered)
        {
            HotkeyApi.UnregisterHotKey(_hotkeyWindow.Handle, HotkeyId);
        }

        _tray.Visible = false;
        _tray.Dispose();
        _hotkeyWindow.DestroyHandle();
        ExitThread();
    }

    // ---------- 配置 ----------

    private const string DefaultHotkey = "Ctrl+Alt+D";

    // GUI 换热键后存这里；优先级：命令行参数 > config.json > 注册表 > 默认
    private const string SettingsKeyPath = @"Software\WinDndToggle";
    private const string HotkeyValueName = "Hotkey";

    private static string? LoadHotkeyFromRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
            return key?.GetValue(HotkeyValueName) as string;
        }
        catch
        {
            return null;
        }
    }

    private static bool SaveHotkeyToRegistry(string text)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath);
            key.SetValue(HotkeyValueName, text, RegistryValueKind.String);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string LoadHotkeyText(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--hotkey", StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        try
        {
            // 手动配置：只读不写。想固定热键就自己放一个 config（会盖过 GUI 保存的值）。
            string path = Path.Combine(AppContext.BaseDirectory, "WinDndToggle.config.json");
            if (File.Exists(path))
            {
                string? parsed = ExtractJsonString(File.ReadAllText(path), "hotkey");
                if (!string.IsNullOrWhiteSpace(parsed))
                {
                    return parsed!;
                }
            }
        }
        catch
        {
            // 配置坏了不该让程序起不来
        }

        // GUI 里保存过的热键
        string? saved = LoadHotkeyFromRegistry();
        if (!string.IsNullOrWhiteSpace(saved))
        {
            return saved!;
        }

        return DefaultHotkey;
    }

    /// <summary>
    /// 从 {"hotkey":"Ctrl+Alt+D"} 里抠出字符串值。
    /// 只为这一个字段，不值得为 net48 单独引一个 JSON 库。
    /// </summary>
    private static string? ExtractJsonString(string json, string key)
    {
        int k = json.IndexOf("\"" + key + "\"", StringComparison.OrdinalIgnoreCase);
        if (k < 0)
        {
            return null;
        }

        int colon = json.IndexOf(':', k);
        if (colon < 0)
        {
            return null;
        }

        int open = json.IndexOf('"', colon + 1);
        if (open < 0)
        {
            return null;
        }

        int close = json.IndexOf('"', open + 1);
        if (close < 0)
        {
            return null;
        }

        return json.Substring(open + 1, close - open - 1);
    }
}

/// <summary>只用来收 WM_HOTKEY 的隐藏窗口。</summary>
internal sealed class HotkeyWindow : NativeWindow
{
    private const int WM_HOTKEY = 0x0312;

    public event Action? Pressed;

    public HotkeyWindow()
    {
        CreateHandle(new CreateParams());
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == 0x4E44)
        {
            Pressed?.Invoke();
            return;
        }

        base.WndProc(ref m);
    }
}

internal static class HotkeyApi
{
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

internal static class HotkeyParser
{
    /// <summary>支持 "Ctrl+Alt+D" / "Ctrl+Shift+F9" / "Win+Alt+Q" 这类写法，必须至少带一个修饰键。</summary>
    public static bool TryParse(string text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] parts = text.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = parts[i].Trim();
        }

        if (parts.Length < 2)
        {
            return false;
        }

        for (int i = 0; i < parts.Length; i++)
        {
            string p = parts[i];

            if (i == parts.Length - 1)
            {
                if (p.Length == 1 && char.IsLetterOrDigit(p[0]))
                {
                    vk = char.ToUpperInvariant(p[0]);
                }
                else if (p.Length >= 2 && char.ToUpperInvariant(p[0]) == 'F' &&
                         int.TryParse(p.Substring(1), out int n) && n >= 1 && n <= 24)
                {
                    vk = (uint)(0x70 + n - 1);
                }
                else
                {
                    return false;
                }

                continue;
            }

            switch (p.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= HotkeyApi.MOD_CONTROL;
                    break;
                case "alt":
                    modifiers |= HotkeyApi.MOD_ALT;
                    break;
                case "shift":
                    modifiers |= HotkeyApi.MOD_SHIFT;
                    break;
                case "win":
                case "windows":
                    modifiers |= HotkeyApi.MOD_WIN;
                    break;
                default:
                    return false;
            }
        }

        return vk != 0 && modifiers != 0;
    }
}

/// <summary>
/// 开机自启：HKCU 的 Run 键（Software\Microsoft\Windows\CurrentVersion\Run）。
/// 不用管理员权限，任务管理器的「启动应用」里也能看到、能禁用。
/// </summary>
internal static class Autostart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WinDndToggle";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string v && v.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>exe 挪过位置的话，Run 键还指着旧路径，开机自启会静默失效。启动时把路径修成当前的。</summary>
    public static void RepairIfNeeded()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(ValueName) is not string v || v.Length == 0)
            {
                return;
            }

            string current = "\"" + Application.ExecutablePath + "\"";
            if (!string.Equals(v, current, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(ValueName, current, RegistryValueKind.String);
            }
        }
        catch
        {
            // 修不了就算了，别影响正常启动
        }
    }

    public static bool SetEnabled(bool enable, out string error)
    {
        error = "";
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enable)
            {
                // 路径带引号，含空格的目录不会被拆开
                key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

/// <summary>自己画托盘图标：紫/灰月亮，开启时右下角加一个红点。</summary>
internal static class IconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Build(Color crescent, bool badge)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using (var path = new GraphicsPath(FillMode.Alternate))
            {
                path.AddEllipse(4, 4, 23, 23);
                path.AddEllipse(13, 0, 23, 23);
                using var brush = new SolidBrush(crescent);
                g.FillPath(brush, path);
            }

            if (badge)
            {
                using var dot = new SolidBrush(Color.FromArgb(239, 68, 68));
                g.FillEllipse(dot, 20, 20, 11, 11);
            }
        }

        IntPtr h = bmp.GetHicon();
        try
        {
            using Icon tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(h);
        }
    }
}

/// <summary>
/// 自己画一个提示框：MessageBox 遇到中文长句会把宽度算歪、把字切掉，托盘程序的说明又偏偏是长句。
/// </summary>
internal static class InfoDialog
{
    private const int TextWidthDip = 420;
    private const int PadDip = 16;

    public static void Show(string title, string message)
    {
        Font font = SystemFonts.MessageBoxFont ?? Control.DefaultFont;

        using var form = new Form
        {
            Text = title,
            Font = font,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowIcon = false,
            ShowInTaskbar = true,
            // 自己按 DPI 换算坐标，不让 WinForms 再缩一遍（缩了就会把字切掉）
            AutoScaleMode = AutoScaleMode.None,
        };

        _ = form.Handle; // 先建句柄，DeviceDpi 才有值
        float dpi = form.DeviceDpi / 96f;
        int Sc(int dip) => (int)Math.Round(dip * dpi);

        int pad = Sc(PadDip);
        int textWidth = Sc(TextWidthDip);

        Size textSize = TextRenderer.MeasureText(
            message, font, new Size(textWidth, 0), TextFormatFlags.WordBreak);

        int iconSize = Sc(32);
        int textLeft = pad + iconSize + Sc(12);
        int buttonW = Sc(88);
        int buttonH = Sc(28);
        int buttonTop = pad + textSize.Height + pad;

        form.ClientSize = new Size(textLeft + textWidth + pad, buttonTop + buttonH + pad);

        var icon = new PictureBox
        {
            Image = SystemIcons.Warning.ToBitmap(),
            SizeMode = PictureBoxSizeMode.StretchImage,
            Bounds = new Rectangle(pad, pad + Sc(2), iconSize, iconSize),
        };

        var label = new Label
        {
            Text = message,
            Font = font,
            AutoSize = false,
            Bounds = new Rectangle(textLeft, pad, textWidth, textSize.Height),
        };

        var ok = new Button
        {
            Text = "确定",
            DialogResult = DialogResult.OK,
            Font = font,
            Bounds = new Rectangle(form.ClientSize.Width - pad - buttonW, buttonTop, buttonW, buttonH),
        };

        form.Controls.Add(icon);
        form.Controls.Add(label);
        form.Controls.Add(ok);
        form.AcceptButton = ok;
        form.ShowDialog();
    }
}
