using System.Runtime.CompilerServices;

namespace ThermalTrace.UI;

/// <summary>
/// 自绘基类。页面上的卡片与图表全部从它派生，省掉每次重写一遍 SetStyle。
///
/// 三个样式位各自的理由：
///  - UserPaint           → 交给 OnPaint，不走系统绘制
///  - AllPaintingInWmPaint → 跳过 WM_ERASEBKGND，闪烁的主要来源
///  - OptimizedDoubleBuffer → 双缓冲，自绘面积大时没有它必然闪
///  - ResizeRedraw        → 拖窗口边框时不留残影
///
/// 另外它自动订阅 Theme.Changed：主题一变就自行 Invalidate 重绘。
/// 这样"切主题立即生效"对派生类是免费的，不用每个控件各自记着。
/// </summary>
internal class BufferedPanel : Panel
{
    protected BufferedPanel()
    {
        SetStyle(ControlStyles.UserPaint
               | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.ResizeRedraw
               | ControlStyles.SupportsTransparentBackColor, true);
        DoubleBuffered = true;

        BackColor = Theme.Current.Bg;
        ForeColor = Theme.Current.Text;

        ThemeWatch.Track(this, ApplyThemeChange);
    }

    /// <summary>当前 DPI 缩放系数（96 = 100%）。图表/圆角坐标一律乘它，否则 125%/150% 下会错位发虚。</summary>
    protected float DpiScale => DeviceDpi <= 0 ? 1f : DeviceDpi / 96f;

    /// <summary>
    /// 子控件（尤其 Label）应当继承的底色。
    /// 派生类覆盖它，让 Theme.ApplyControl 能把子控件刷成正确的容器色。
    /// </summary>
    public virtual Color ContentBackColor => Theme.Current.Surface;

    private void ApplyThemeChange()
    {
        if (IsDisposed) return;
        BackColor = Theme.Current.Bg;
        ForeColor = Theme.Current.Text;
        OnThemeChanged();
        Invalidate();
    }

    /// <summary>主题切换后派生类若要重建缓存画笔/字体，重写这里（随后会自动 Invalidate）。</summary>
    protected virtual void OnThemeChanged()
    {
    }
}

/// <summary>
/// 主题变更的唯一订阅入口。句柄建立时挂上 <see cref="Theme.Changed"/>，销毁时摘掉，
/// 回调统一切回控件所在线程。自绘件与页签都只调 <see cref="Track"/>，不再各写生命周期方法。
/// </summary>
internal static class ThemeWatch
{
    private static readonly ConditionalWeakTable<Control, State> States = new();

    private sealed class State
    {
        public required Action Apply { get; init; }
        public EventHandler? Handler { get; set; }
    }

    /// <summary>让 <paramref name="owner"/> 在每次主题切换后执行 <paramref name="apply"/>（UI 线程）。</summary>
    public static void Track(Control owner, Action apply)
    {
        if (States.TryGetValue(owner, out _)) return;

        var state = new State { Apply = apply };
        States.Add(owner, state);
        owner.HandleCreated += OnHandleCreated;
        owner.HandleDestroyed += OnHandleDestroyed;

        // 极少数控件在构造时已有句柄，直接补挂，别等下一次重建
        if (owner.IsHandleCreated) Attach(owner, state);
    }

    private static void OnHandleCreated(object? sender, EventArgs e)
    {
        if (sender is not Control owner || !States.TryGetValue(owner, out var state)) return;
        Attach(owner, state);
    }

    private static void OnHandleDestroyed(object? sender, EventArgs e)
    {
        if (sender is not Control owner || !States.TryGetValue(owner, out var state)) return;
        if (state.Handler is null) return;

        Theme.Changed -= state.Handler;
        state.Handler = null;
    }

    private static void Attach(Control owner, State state)
    {
        if (state.Handler is not null) return;

        EventHandler handler = (_, _) => Dispatch(owner, state.Apply);
        state.Handler = handler;
        Theme.Changed += handler;
    }

    private static void Dispatch(Control owner, Action apply)
    {
        if (owner.IsDisposed) return;

        // SystemEvents 理论上在 UI 线程回调，但不保证；跨线程时切回控件所在线程再刷新
        if (owner.IsHandleCreated && owner.InvokeRequired)
        {
            owner.BeginInvoke(new Action(() => Dispatch(owner, apply)));
            return;
        }
        apply();
    }
}

/// <summary>
/// 需要跟随主题整页刷色的 TabPage 基类。
/// 派生页只实现 <see cref="ApplyTheme"/>；订阅与跨线程由 <see cref="ThemeWatch"/> 统一处理。
/// </summary>
internal abstract class ThemedTabPage : TabPage
{
    protected ThemedTabPage()
    {
        ThemeWatch.Track(this, () => ApplyTheme());
    }

    /// <summary>主题切换后刷新本页配色；需要连带重算数据的页在自己的实现里调用。</summary>
    protected abstract void ApplyTheme();
}

/// <summary>
/// 主题刷色的共用件。「把非自绘容器的底色压成卡片色」原先在设置页与解读卡各写一份，
/// 这里只保留一处；<see cref="Theme"/> 仍负责标准控件的按类型刷色。
/// </summary>
internal static class ThemeOps
{
    /// <summary>把子树里非自绘控件的底色统一成 <paramref name="surface"/>。</summary>
    public static void Flatten(Control root, Color surface)
    {
        foreach (Control c in root.Controls)
        {
            // 自绘件自己按 Theme.Current 画，别动它的底色
            if (c is BufferedPanel) continue;
            // 系统日历控件不跟随深色（主题处理约定）
            if (c is DateTimePicker) continue;

            c.BackColor = surface;
            if (c.HasChildren) Flatten(c, surface);
        }
    }
}
