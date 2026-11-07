using ThermalTrace.Core;
using ThermalTrace.Model;

namespace ThermalTrace.UI;

/// <summary>
/// 一套配色。两套配色只允许通过 Theme.Light / Theme.Dark 取用，
/// 不允许就地修改——否则「改了浅色忘了深色」的漂移会立刻回来（两套配色必须成对维护）。
/// </summary>
internal sealed record ColorPalette
{
    public bool IsDark { get; init; }

    /// <summary>窗口底色。</summary>
    public Color Bg { get; init; }

    /// <summary>卡片/输入框等"浮起一层"的面。</summary>
    public Color Surface { get; init; }

    public Color Text { get; init; }

    /// <summary>次要文字、单位、注记。</summary>
    public Color TextDim { get; init; }

    public Color Accent { get; init; }

    /// <summary>温度语义色：热。</summary>
    public Color Hot { get; init; }

    /// <summary>温度语义色：温。</summary>
    public Color Warm { get; init; }

    /// <summary>温度语义色：凉。</summary>
    public Color Cool { get; init; }

    public Color Border { get; init; }
}

/// <summary>
/// 主题引擎。唯一职责：回答"现在该用什么颜色"，以及"颜色变了要通知谁"。
///
/// 【为什么要有这层】
/// WinForms 控件在 Win10 深色模式下不会自动变黑——Button/TextBox/ComboBox 读的是
/// SystemColors，和 UWP/WinUI 那套 Immersive 色不是一回事。
/// 所以每一个控件的 BackColor / ForeColor 都必须手动赋，
/// 本类集中提供这一步，避免颜色散落各处。
///
/// 【不做的事】
/// 托盘右键菜单刻意不跟随主题：它是系统右键菜单语境，跟系统走更协调，
/// 而自绘 ContextMenuStrip 要写 ToolStripRenderer，收益极低、踩坑概率极高。
/// </summary>
internal static class Theme
{
    private static readonly ColorPalette LightPalette = new()
    {
        IsDark = false,
        Bg = Hex("#F5F6F8"),
        Surface = Hex("#FFFFFF"),
        Text = Hex("#1B1B1F"),
        TextDim = Hex("#6B6B76"),
        Accent = Hex(VisualTones.Accent.GuiLight),   // Win 强调蓝，Win10/11 一致
        Hot = Hex(VisualTones.Hot.GuiLight),
        Warm = Hex(VisualTones.Warm.GuiLight),
        Cool = Hex(VisualTones.Cool.GuiLight),
        Border = Hex("#E3E4E8")
    };

    private static readonly ColorPalette DarkPalette = new()
    {
        IsDark = true,
        Bg = Hex("#181A1E"),
        Surface = Hex("#212429"),
        Text = Hex("#E6E6E6"),
        TextDim = Hex("#9A9AA5"),
        Accent = Hex(VisualTones.Accent.GuiDark),   // 深色下蓝要提亮，#0078D4 会发闷
        Hot = Hex(VisualTones.Hot.GuiDark),
        Warm = Hex(VisualTones.Warm.GuiDark),
        Cool = Hex(VisualTones.Cool.GuiDark),
        Border = Hex("#2E3138")
    };

    private static ColorPalette _current = LightPalette;
    private static ThemeMode _mode = ThemeMode.Follow;
    private static bool _initialized;

    /// <summary>
    /// 当前配色。首次访问会自行初始化（读配置 + 挂系统监听），
    /// 所以后面任何批次的控件直接取 Current 都能拿到正确颜色，不必记得先调 Initialize。
    /// </summary>
    public static ColorPalette Current
    {
        get
        {
            if (!_initialized) Initialize();
            return _current;
        }
    }

    public static bool IsDark => Current.IsDark;

    public static ThemeMode Mode
    {
        get
        {
            if (!_initialized) Initialize();
            return _mode;
        }
    }

    /// <summary>配色已切换。订阅者需自行重绘；自绘控件（BufferedPanel 派生）已自动订阅。</summary>
    public static event EventHandler? Changed;

    /// <summary>幂等。显式调用只为"尽早挂上系统监听"，不调用也不会出错。</summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            SystemTheme.EnsureHook();
            SystemTheme.Changed += (_, _) => Reload();
        }
        catch
        {
            // SystemEvents 挂不上（例如无消息循环的服务场景）：
            // 退化为"启动时定一次色"，不影响可用性。
        }

        Reload();
    }

    /// <summary>重新求值：读 settings.json 的 Ui.ThemeMode → 必要时跟随系统 → 变化则通知。</summary>
    public static void Reload()
    {
        var mode = ThemeMode.Follow;
        try
        {
            mode = SettingsStore.Load().Ui.ThemeMode;
        }
        catch
        {
            // 配置损坏时按"跟随系统"处理，不要让主题问题拖垮窗口
        }

        ApplyMode(mode);
    }

    /// <summary>切换主题并写回 settings.json。</summary>
    public static void SetMode(ThemeMode mode)
    {
        try
        {
            var s = SettingsStore.Load();
            s.Ui = s.Ui with { ThemeMode = mode };
            SettingsStore.Save(s);
        }
        catch (Exception ex)
        {
            ConsoleHost.WriteError($"[warn] 主题设置保存失败（本次仅内存生效）: {ex.Message}");
        }

        ApplyMode(mode);
    }

    private static void ApplyMode(ThemeMode mode)
    {
        _mode = mode;

        var next = mode switch
        {
            ThemeMode.Light => LightPalette,
            ThemeMode.Dark => DarkPalette,
            _ => SystemTheme.IsLightAppTheme() ? LightPalette : DarkPalette
        };

        if (ReferenceEquals(next, _current)) return;
        _current = next;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    // ---------- 刷色 ----------

    /// <summary>把当前配色刷到整个窗口（含所有子控件）。</summary>
    public static void Apply(Form form)
    {
        if (form.IsDisposed) return;
        var p = Current;
        form.BackColor = p.Bg;
        form.ForeColor = p.Text;
        ApplyTree(form);
    }

    /// <summary>只刷子树，不改 root 自身（用于动态塞进来的容器）。</summary>
    public static void ApplyTree(Control root)
    {
        foreach (Control c in root.Controls)
            ApplyControl(c);
    }

    /// <summary>
    /// 按控件类型刷色并递归。
    /// 【为什么必须逐个类型写】WinForms 控件不会自动跟随，
    /// 但这个体力活只能集中在这一处，别散到各个页面里。
    /// </summary>
    public static void ApplyControl(Control c)
    {
        if (c.IsDisposed) return;
        var p = Current;

        switch (c)
        {
            // 自绘控件：自己按 Theme.Current 画，这里只兜底（并让子控件拿到正确容器色）
            case BufferedPanel:
                c.BackColor = p.Bg;
                c.ForeColor = p.Text;
                break;

            case Button b:
                b.FlatStyle = FlatStyle.Flat;
                b.UseVisualStyleBackColor = false;
                b.BackColor = p.Surface;
                b.ForeColor = p.Text;
                b.FlatAppearance.BorderColor = p.Border;
                b.FlatAppearance.MouseOverBackColor = Gfx.Mix(p.Surface, p.Accent, 0.10);
                b.FlatAppearance.MouseDownBackColor = Gfx.Mix(p.Surface, p.Accent, 0.22);
                break;

            case CheckBox cb:
                cb.ForeColor = p.Text;
                cb.BackColor = PanelBackOf(cb, p);
                break;

            case RadioButton rb:
                rb.ForeColor = p.Text;
                rb.BackColor = PanelBackOf(rb, p);
                break;

            case Label l:
                l.ForeColor = p.Text;
                // Label 不做透明：自绘父容器不会响应 WM_PRINTCLIENT，透明会露出底色。
                // 直接取父容器的"内容色"，卡片里就是卡片色，窗口里就是窗口色。
                l.BackColor = (l.Parent as BufferedPanel)?.ContentBackColor ?? p.Bg;
                break;

            case TextBoxBase t:
                t.BackColor = p.Surface;
                t.ForeColor = p.Text;
                t.BorderStyle = BorderStyle.FixedSingle;
                break;

            case NumericUpDown n:
                n.BackColor = p.Surface;
                n.ForeColor = p.Text;
                break;

            case ComboBox cmb:
                cmb.FlatStyle = FlatStyle.Flat;
                cmb.BackColor = p.Surface;
                cmb.ForeColor = p.Text;
                break;

            case ListBox lb:
                lb.BackColor = p.Surface;
                lb.ForeColor = p.Text;
                lb.BorderStyle = BorderStyle.FixedSingle;
                break;

            case DateTimePicker:
                // 系统日历控件要真深色需要 .NET 9 的 SetColorMode；
                // 保持默认（白底黑字）至少始终可读，比硬刷成半黑不白强。
                break;

            default:
                c.BackColor = p.Bg;
                c.ForeColor = p.Text;
                break;
        }

        if (c.HasChildren)
            foreach (Control child in c.Controls)
                ApplyControl(child);
    }

    private static Color PanelBackOf(Control c, ColorPalette p) =>
        (c.Parent as BufferedPanel)?.ContentBackColor ?? p.Bg;

    private static Color Hex(string hex)
    {
        var s = hex.TrimStart('#');
        return Color.FromArgb(255,
            Convert.ToInt32(s[..2], 16),
            Convert.ToInt32(s.Substring(2, 2), 16),
            Convert.ToInt32(s.Substring(4, 2), 16));
    }
}
