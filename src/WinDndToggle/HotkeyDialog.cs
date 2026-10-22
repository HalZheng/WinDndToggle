using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WinDndToggle;

/// <summary>
/// 换热键的小对话框：按下新组合键实时显示，OK 才交给 TrayContext 去注册。
/// 真正的冲突检测在 RegisterHotKey 那一步，这里只负责把按键翻译成 "Ctrl+Alt+F9" 这种文本。
/// 主键只收字母 / 数字 / F1-F24，跟 HotkeyParser 支持的范围保持一致，避免存进去解析不回来。
/// </summary>
internal sealed class HotkeyDialog : Form
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    /// <summary>OK 时才有值，形如 "Ctrl+Shift+F9"。</summary>
    public string? Result { get; private set; }

    private readonly Label _display;
    private readonly Label _warning;
    private readonly Button _ok;
    private string? _captured;

    public HotkeyDialog(string currentText)
    {
        Text = Res.Tr("更换热键", "Change hotkey");
        Font = SystemFonts.MessageBoxFont ?? Control.DefaultFont;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        // 焦点可能落在按钮上，按键先给窗体
        KeyPreview = true;
        // 和 InfoDialog 一样：自己按 DPI 换算坐标，不让 WinForms 再缩一遍
        AutoScaleMode = AutoScaleMode.None;

        _ = Handle; // 先建句柄，DeviceDpi 才有值
        float dpi = DeviceDpi / 96f;
        int Sc(int dip) => (int)Math.Round(dip * dpi);

        int pad = Sc(16);
        int width = Sc(400);

        var hint = new Label
        {
            Text = Res.Tr("按下新的组合键（需含 Ctrl / Alt / Shift / Win 至少一个），Esc 取消。",
                          "Press a new combination (must include Ctrl / Alt / Shift / Win). Esc to cancel."),
            AutoSize = false,
            Bounds = new Rectangle(pad, pad, width - pad * 2, Sc(34)),
        };

        _display = new Label
        {
            Text = Res.Tr("当前：", "Current: ") + currentText,
            Font = new Font(Font.FontFamily, Font.Size + 3f, FontStyle.Bold),
            ForeColor = SystemColors.Highlight,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(pad, hint.Bottom + Sc(8), width - pad * 2, Sc(40)),
            BackColor = SystemColors.Window,
            BorderStyle = BorderStyle.FixedSingle,
        };

        _warning = new Label
        {
            Text = "",
            ForeColor = Color.Firebrick,
            AutoSize = false,
            Bounds = new Rectangle(pad, _display.Bottom + Sc(8), width - pad * 2, Sc(18)),
        };

        _ok = new Button
        {
            Text = Res.Tr("确定", "OK"),
            DialogResult = DialogResult.OK,
            Enabled = false,
        };
        _ok.Click += (_, _) => Result = _captured;

        var cancel = new Button
        {
            Text = Res.Tr("取消", "Cancel"),
            DialogResult = DialogResult.Cancel,
        };

        int buttonW = Sc(88), buttonH = Sc(28);
        int buttonTop = _warning.Bottom + Sc(12);
        cancel.Bounds = new Rectangle(width - pad - buttonW, buttonTop, buttonW, buttonH);
        _ok.Bounds = new Rectangle(width - pad - buttonW * 2 - Sc(8), buttonTop, buttonW, buttonH);

        ClientSize = new Size(width, buttonTop + buttonH + pad);
        Controls.Add(hint);
        Controls.Add(_display);
        Controls.Add(_warning);
        Controls.Add(_ok);
        Controls.Add(cancel);
        CancelButton = cancel;

        KeyDown += OnKeyDownCapture;
    }

    private void OnKeyDownCapture(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            Close();
            return;
        }

        bool ctrl = e.Control;
        bool shift = e.Shift;
        bool alt = e.Alt;
        bool win = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

        Keys main = e.KeyCode & Keys.KeyCode;

        // 还在按修饰键，等主键
        bool isModifier = main is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin;
        if (isModifier)
        {
            UpdateDisplay(ctrl, shift, alt, win, mainKey: null);
            e.Handled = true;
            return;
        }

        string? mainName = DescribeMainKey(main);
        if (mainName is null || (!ctrl && !shift && !alt && !win))
        {
            // 主键不支持，或者没按修饰键：都提示而不是静默忽略
            UpdateDisplay(ctrl, shift, alt, win, mainKey: null);
            _warning.Text = mainName is null
                ? Res.Tr("这个键不支持，请用字母、数字或 F1-F24。", "That key is not supported. Use a letter, number or F1-F24.")
                : Res.Tr("至少要带一个修饰键（Ctrl / Alt / Shift / Win）。", "Include at least one modifier (Ctrl / Alt / Shift / Win).");
            e.Handled = true;
            return;
        }

        _warning.Text = "";
        var parts = new System.Collections.Generic.List<string>(5);
        if (ctrl) parts.Add("Ctrl");
        if (shift) parts.Add("Shift");
        if (alt) parts.Add("Alt");
        if (win) parts.Add("Win");
        parts.Add(mainName);
        _captured = string.Join("+", parts);

        _display.Text = _captured;
        _ok.Enabled = true;
        e.Handled = true;
    }

    private void UpdateDisplay(bool ctrl, bool shift, bool alt, bool win, Keys? mainKey)
    {
        var parts = new System.Collections.Generic.List<string>(4);
        if (ctrl) parts.Add("Ctrl");
        if (shift) parts.Add("Shift");
        if (alt) parts.Add("Alt");
        if (win) parts.Add("Win");

        _display.Text = parts.Count == 0
            ? Res.Tr("按下新的组合键...", "Press a new combination...")
            : string.Join("+", parts) + (mainKey is null ? "+..." : "");
    }

    private static string? DescribeMainKey(Keys key)
    {
        if (key >= Keys.A && key <= Keys.Z)
        {
            return key.ToString(); // A-Z
        }

        if (key >= Keys.D0 && key <= Keys.D9)
        {
            return ((char)('0' + (key - Keys.D0))).ToString();
        }

        if (key >= Keys.F1 && key <= Keys.F24)
        {
            return "F" + (key - Keys.F1 + 1);
        }

        return null;
    }
}
