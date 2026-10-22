using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WinDndToggle;

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

    private string _hotkeyText;
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

        _hotkeyText = LoadHotkeyText(args);

        _statusItem = new ToolStripMenuItem { Enabled = false };
        _toggleItem = new ToolStripMenuItem();
        _toggleItem.Click += (_, _) => Toggle();

        var recheckItem = new ToolStripMenuItem("重新检测");
        recheckItem.Click += (_, _) => Recheck();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(recheckItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitApp()));

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
                "这台机器上用不了。\n\n" + _lastError +
                "\n\n程序会留在托盘里，右键菜单里可以「重新检测」。");
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
            _tray.Text = "勿扰：不可用";
            _statusItem.Text = "勿扰：不可用";
            _toggleItem.Text = "切换勿扰";
            _toggleItem.Enabled = false;
            return;
        }

        string text = _state switch
        {
            DndState.Off => "勿扰：已关闭",
            DndState.PriorityOnly => "勿扰：已开启（仅优先事项）",
            DndState.AlarmsOnly => "勿扰：已开启（仅闹钟）",
            _ => "勿扰：状态未知",
        };

        bool on = _state is DndState.PriorityOnly or DndState.AlarmsOnly;
        _tray.Icon = on ? _iconOn : _iconOff;
        _tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        _statusItem.Text = text;
        _toggleItem.Text = on ? "关闭勿扰" : "开启勿扰";
        _toggleItem.Enabled = true;
    }

    private void Toggle()
    {
        if (!_usable && !Refresh())
        {
            if (!_warned)
            {
                _warned = true;
                InfoDialog.Show("WinDndToggle", "切换失败：\n\n" + _lastError);
            }
            else
            {
                ShowBalloon("切换失败", _lastError, ToolTipIcon.Warning);
            }

            return;
        }

        if (!QuietHours.TryToggle(out DndState state, out string error))
        {
            ShowBalloon("切换失败", error, ToolTipIcon.Warning);
            Refresh();
            return;
        }

        _state = state;
        _usable = true;
        Apply();

        bool on = state is DndState.PriorityOnly or DndState.AlarmsOnly;
        ShowBalloon(on ? "勿扰已开启" : "勿扰已关闭",
            on ? "通知会先攒着，不弹出来了。" : "通知恢复正常弹出。",
            ToolTipIcon.Info);
    }

    private void Recheck()
    {
        bool ok = Refresh();
        ShowBalloon("重新检测完成",
            ok ? "读写正常，当前：" + _statusItem.Text : _lastError,
            ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    private void ShowBalloon(string title, string text, ToolTipIcon icon)
    {
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = text;
        _tray.BalloonTipIcon = icon;
        _tray.ShowBalloonTip(2500);
    }

    private void RegisterHotkey()
    {
        if (!HotkeyParser.TryParse(_hotkeyText, out _hotkeyMods, out _hotkeyVk))
        {
            _hotkeyText = "(配置无效)";
            return;
        }

        _hotkeyRegistered = HotkeyApi.RegisterHotKey(
            _hotkeyWindow.Handle, HotkeyId, _hotkeyMods | HotkeyApi.MOD_NOREPEAT, _hotkeyVk);

        if (!_hotkeyRegistered)
        {
            _hotkeyText += " (已被占用)";
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
            // 只读不写：程序跑起来不该在磁盘上留任何东西。
            // 想换热键就自己在这个位置放一个 config，或者用 --hotkey 启动。
            string path = Path.Combine(AppContext.BaseDirectory, "WinDndToggle.config.json");
            if (!File.Exists(path))
            {
                return DefaultHotkey;
            }

            string text = File.ReadAllText(path);
            string? parsed = ExtractJsonString(text, "hotkey");
            if (!string.IsNullOrWhiteSpace(parsed))
            {
                return parsed!;
            }
        }
        catch
        {
            // 配置坏了不该让程序起不来
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
