using System.Drawing.Drawing2D;
using System.Drawing.Text;
using ThermalTrace.Analysis;
using ThermalTrace.Core;
using ThermalTrace.Diagnostics;
using ThermalTrace.Model;
#if PUSH_ENABLED
using ThermalTrace.Push;
#endif
using ThermalTrace.Services;

namespace ThermalTrace.UI.Tabs;

/// <summary>
/// 「设置」页：把命令行专属能力全部 GUI 化 —— 日常使用不再需要开终端。
///
/// 【控件清单】
///  - 开机自启开关 + 状态标签（<c>AutoStart.Status() / Enable() / Disable()</c>，后端已就绪）
///  - 经纬度 / 城市名 手填 + 联网获取气温 + 立即重新定位
///  - 主题下拉（Follow / Light / Dark → <c>UiOptions.ThemeMode</c>）
///  - 负载类别桶边界（折叠区，改完立即重算）
///  - 清灰基准日（DateTimePicker → <c>CleanDateIso</c>，替代 <c>--reset-clean-date</c>）
///  - 重新自检（<c>Commands.SelfCheck()</c>）
///  - 「墨水屏推送」分区（极趣推送版）：启用开关 / 设备 MAC（可拉云端设备列表）/
///    槽 1、槽 2 图片页页位 / 推送时刻 / API Key（掩码输入 + 清除）+「立即推送两页」+ 逐槽上次推送与适用状态
///
/// 【自启开关（产品边界 R4）】
/// 状态标签显示 <c>AutoStart.Status().Describe()</c> 的**四态真实结果**，
/// 绝不能因为"调用 Enable() 没抛异常"就打「已启用」——旧实现正是这样把自启失效瞒了三天
/// （自启的真实状态）。失败时把 <c>result.Message</c> 原样弹给用户。
///
/// 【为什么自己算排版】
/// 六块分区要在 900×600 单页内放下、760×520 下也不该出现滚动条，所以排成两列并显式算宽高。
/// 不用 Dock 流是因为"Fill 该先加还是后加"全靠 z-order，这种布局里太容易埋雷；
/// <see cref="LayoutSections"/> 一处算清楚，改起来也只有一个地方。
///
/// 【不做的事】不新增任何设置项，也不改存储层格式。
/// 本页只是给已有的 settings.json 字段配一套可视化入口。
/// </summary>
internal sealed class SettingsTab : ThemedTabPage
{
    private const int ColGap = 12;
    private const int RowGap = 8;

    /// <summary>折叠态高度：标题区 32 + 表头 26 + 底部留白 8。</summary>
    private const int BoundsCollapsedHeight = 66;

    private readonly Panel _host = new() { Dock = DockStyle.Fill, AutoScroll = true, Margin = new Padding(0) };
    private readonly List<SettingsSection> _left = new();
    private readonly List<SettingsSection> _right = new();

    // ---------------- 开机自启 ----------------
    private readonly AccentButton _asToggle = new() { Width = 132, Height = 30, Margin = new Padding(0) };
    private readonly Label _asStatus = new()
    {
        Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true, Margin = new Padding(10, 0, 0, 0)
    };
    private readonly Label _asDetail = new()
    {
        Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.TopLeft,
        AutoEllipsis = true, Margin = new Padding(0)
    };

    // ---------------- 位置与环境气温 ----------------
    private readonly NumericUpDown _lat = new();
    private readonly NumericUpDown _lon = new();
    private readonly TextBox _city = new() { Width = 148 };
    private readonly CheckBox _online = new() { AutoSize = true };
    private readonly AccentButton _relocate = new() { Width = 116, Height = 28, Margin = new Padding(10, 2, 0, 0) };
    private readonly Label _ambientNote = new()
    {
        Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.TopLeft,
        AutoEllipsis = true, Margin = new Padding(0)
    };

    // ---------------- 主题 ----------------
    private readonly ComboBox _themeBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 146, Margin = new Padding(0, 4, 0, 0) };
    private readonly Label _themeHint = new()
    {
        Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.TopLeft,
        AutoEllipsis = true, Margin = new Padding(0)
    };

    // ---------------- 负载类别边界 ----------------
    private readonly SettingsSection _boundsSection = new("负载类别边界") { Height = BoundsCollapsedHeight };
    private readonly AccentButton _boundsToggle = new() { Width = 72, Height = 26, Primary = false, Margin = new Padding(0) };
    private readonly Label _boundsSummary = new()
    {
        Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true, Margin = new Padding(10, 0, 0, 0)
    };
    private readonly FlowLayoutPanel _boundsBody = new()
    {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true,
        Margin = new Padding(0), Visible = false
    };
    private readonly Label _boundsHint = new()
    {
        Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.TopLeft,
        AutoEllipsis = true, Visible = false, Margin = new Padding(0)
    };
    private readonly TableLayoutPanel _boundsGrid = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0) };
    private readonly List<NumericUpDown> _boundBoxes = new();
    private bool _boundsOpen;

    // ---------------- 清灰基准日 ----------------
    private readonly DateTimePicker _cleanDate = new()
    {
        Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd",
        Width = 132, Margin = new Padding(0, 4, 8, 0)
    };
    private readonly Label _cleanNote = new()
    {
        Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.TopLeft,
        AutoEllipsis = true, Margin = new Padding(0)
    };

    // ---------------- 维护 ----------------
    private readonly AccentButton _selfCheck = new() { Width = 132, Height = 30, Margin = new Padding(0, 0, 10, 0) };
    private readonly AccentButton _rebind = new() { Width = 152, Height = 30, Margin = new Padding(0), Primary = false };
    private readonly SamplerService? _sampler;
    private readonly Label _maintenanceNote = new()
    {
        Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.TopLeft,
        AutoEllipsis = true, Margin = new Padding(0)
    };

    // ---------------- 墨水屏推送 ----------------
#if PUSH_ENABLED

    /// <summary>展开态正文高度 = 四行固定行高之和（26+30+26+30）。
    /// 原先多留的 22px 空行已删，用来放第二条逐槽状态行；展开态左列总高仍 ≤ 503。</summary>
    private const int PushBodyHeight = 112;

    private readonly SettingsSection _pushSection = new("墨水屏推送");
    private readonly AccentButton _pushExpand = new() { Width = 72, Height = 26, Primary = false, Margin = new Padding(0) };
    private readonly Label _pushSummary = new()
    {
        Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true, Margin = new Padding(0, 2, 0, 0)
    };
    private readonly TableLayoutPanel _pushGrid = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = new Padding(0) };
    private readonly TableLayoutPanel _pushBody = new() { Dock = DockStyle.Fill, Margin = new Padding(0), Visible = false };
    private readonly CheckBox _pushEnabled = new() { AutoSize = true, Text = "启用每日自动推送", Margin = new Padding(0, 6, 10, 0) };
    private readonly NumericUpDown _pushHour = new() { Width = 56, Margin = new Padding(0, 4, 10, 0) };
    private readonly ComboBox _pushMac = new() { Width = 160, Margin = new Padding(0, 4, 8, 0) };
    private readonly AccentButton _pushFetch = new() { Width = 88, Height = 28, Primary = false, Margin = new Padding(0, 2, 0, 0) };
    private readonly NumericUpDown _pushPage = new() { Width = 56, Margin = new Padding(0, 4, 10, 0) };
    private readonly NumericUpDown _pushPage2 = new() { Width = 56, Margin = new Padding(0, 4, 10, 0) };
    private readonly TextBox _pushKey = new() { Width = 176, PasswordChar = '●', Margin = new Padding(0, 4, 8, 0) };
    private readonly AccentButton _pushKeyClear = new() { Width = 58, Height = 28, Primary = false, Margin = new Padding(0, 2, 0, 0) };
    // 「立即推送两页」放在分区表头而不是正文里：分区默认折叠，按钮永远可见可点。
    // 折叠态左列总高 404（默认态绝不出现滚动条）；展开态总高 496，仍在 503 的可用高度内。
    private readonly AccentButton _pushNow = new() { Width = 112, Height = 26, Margin = new Padding(0, 0, 8, 0) };
    /// <summary>槽 1 的状态行：打开时显示「上次推送」，点推送后显示本次操作反馈。</summary>
    private readonly Label _pushStatus = new()
    {
        Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true, Margin = new Padding(0)
    };
    /// <summary>槽 2 的状态行：上次推送结果 + 当前是否适用（无清灰日 / 有效日不足 3 天 / 页位冲突）。
    /// 单独一行而不是塞进槽 1 那行——两槽状态不同时，挤在一行会被 AutoEllipsis 截掉关键原因。</summary>
    private readonly Label _pushSlot2 = new()
    {
        Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true, Margin = new Padding(0)
    };
#endif
    private readonly ToolTip _tip = new();
#if PUSH_ENABLED
    private readonly List<ZectrixDeviceInfo> _pushDevices = new();
    private bool _pushOpen;
    /// <summary>本次会话是否已手动推送过。是则状态行留着那次的结果（带秒级时间戳），
    /// RefreshData 不许用 push-state.json 的旧状态覆盖——否则用户刚看到的"19:52 已推送"会被打回旧文案。</summary>
    private bool _pushActed;
    private bool _pushing;
    private bool _fetching;
    private bool _pushKeyDirty;
    private bool _suppressKeyDirty;
    private NoteKind _pushStatusKind = NoteKind.Info;
    private NoteKind _pushSlot2Kind = NoteKind.Info;

    /// <summary>槽 2 是否适用及原因：由 <c>EinkSnapshotBuilder.Build(..., EinkSlot.Slot2)</c> 的
    /// <c>SkipReason</c> 得出，与推送 / CLI 判定同一条链。每次切回本页重算，不缓存旧结论。</summary>
    private bool _slot2Applicable;
    private string _slot2Reason = "读取中…";
#endif

    /// <summary>
    /// 程序化回填 / 程序化改写控件期间的保存抑制。
    /// 一次交互里由程序连带触发的多个变更事件全部合并在这段区间内，区间结束时保存处理器再按“值真变”落盘；
    /// 不引入定时器，也不依赖离开页面或关闭窗口。
    /// </summary>
    private bool _loading;
    private bool _checking;
    private bool _rebinding;
    private bool _boundsValid = true;
    private bool _firstBoundIgnored;
    private int _lastWidth;

    // ---------------- 异步回填（首次进入本页不阻塞 UI） ----------------
    /// <summary>开窗阶段预热只做一次；后续开窗不再重复写日期值。</summary>
    private bool _warmedUp;
    /// <summary>自启状态的后台查询代号：每次发起新查询递增，旧结果回来时按代号丢弃。</summary>
    private int _autoStartGen;
    /// <summary>最近一次成功回填的自启状态；点击开关时按它判断方向，避免再同步起 schtasks。</summary>
    private AutoStartStatus? _autoStartStatus;
#if PUSH_ENABLED
    /// <summary>槽 2 适用性扫描代号与「计算中」标记，防止过期结果回填。</summary>
    private int _slot2Gen;
    private bool _slot2Pending;
#endif

    public SettingsTab(SamplerService? sampler = null)
    {
        _sampler = sampler;
        Text = "设置";
        UseVisualStyleBackColor = false;
        BackColor = Theme.Current.Bg;
        ForeColor = Theme.Current.Text;
        Padding = new Padding(16);

        _host.BackColor = Theme.Current.Bg;
        Controls.Add(_host);

        BuildAutoStartSection();
        BuildAmbientSection();
#if PUSH_ENABLED
        BuildPushSection();
#endif
        BuildThemeSection();
        BuildBoundsSection();
        BuildCleanDateSection();
        BuildMaintenanceSection();

        _host.ClientSizeChanged += (_, _) => LayoutSections();

        ApplyColors();
        // 不在这里 RefreshData：开窗时宿主只同步刷当前页，本页首次切过去时才由 OnVisibleChanged 取数。
    }

    /// <summary>
    /// 开窗阶段预热（本页首次显示提速）：把 <see cref="DateTimePicker"/> 的原生句柄创建
    /// 与首次赋值（实测 143–184 ms）从"首次点设置页"挪到窗体刚显示时。
    /// 只建句柄并写一次日期值，控件外观、布局与交互都不变；随后 RefreshData 会写回真实清灰日。
    /// 必须在窗体 <c>Show()</c> 之后调用，此时父容器 TabControl 已有句柄。
    /// </summary>
    internal void WarmUp()
    {
        if (IsDisposed || _warmedUp) return;
        _warmedUp = true;

        _ = Handle;               // 先建本页句柄，避免子控件挂在尚未创建的父容器下
        _ = _cleanDate.Handle;    // 预创建 DateTimePicker 原生窗口
        try
        {
            _loading = true;      // 预热赋值不触发保存
            _cleanDate.Value = DateTime.Today;
        }
        finally
        {
            _loading = false;
        }
    }

    // ==================================================================
    // 分区搭建
    // ==================================================================

    /// <summary>
    /// 登记一块分区。**必须真的 Add 到 <see cref="_host"/>**：
    /// 只记进 <see cref="_left"/> / <see cref="_right"/> 的话，控件永远没有父容器、
    /// 永远不会被绘制 —— 2026-10-11 实机表现就是"设置页整片空白"
    /// （`AutoScrollMinSize` 照样把滚动条算出来，所以从截图看像是"排了版但什么都没画"）。
    /// </summary>
    private void AddSection(SettingsSection sec, bool toLeft)
    {
        _host.Controls.Add(sec);
        (toLeft ? _left : _right).Add(sec);
    }

    private void BuildAutoStartSection()
    {
        var sec = new SettingsSection("开机自启") { Height = 104 };

        _asToggle.Click += (_, _) => ToggleAutoStart();

        var body = Rows(34f);
        body.Controls.Add(TwoColumn(_asToggle, _asStatus), 0, 0);
        body.Controls.Add(_asDetail, 0, 1);
        sec.Controls.Add(body);

        AddSection(sec, toLeft: true);
    }

    private void BuildAmbientSection()
    {
        var sec = new SettingsSection("位置与环境气温") { Height = 158 };

        SetupCoordinateBox(_lat, -90m, 90m);
        SetupCoordinateBox(_lon, -180m, 180m);
        // 经纬度也走同一条保存路径：以前只有回填赋值，单独改坐标不会落盘。
        _lat.ValueChanged += (_, _) => SaveAmbient();
        _lon.ValueChanged += (_, _) => SaveAmbient();
        _city.TextChanged += (_, _) => SaveAmbient();
        _online.Text = "联网获取环境温度";
        _online.CheckedChanged += (_, _) => SaveAmbient();
        _relocate.Text = "立即重新定位";
        _relocate.Click += OnRelocate;

        var row1 = FlowRow(TextLabel("纬度"), _lat, TextLabel("经度"), _lon);
        var row2 = FlowRow(TextLabel("城市"), _city, _relocate);
        var row3 = FlowRow(_online);

        var body = Rows(30f, 32f, 28f);
        body.Controls.Add(row1, 0, 0);
        body.Controls.Add(row2, 0, 1);
        body.Controls.Add(row3, 0, 2);
        body.Controls.Add(_ambientNote, 0, 3);
        sec.Controls.Add(body);

        AddSection(sec, toLeft: true);
    }

#if PUSH_ENABLED
    // ==================================================================
    // 墨水屏推送
    // ==================================================================

    /// <summary>
    /// 「墨水屏推送」分区（P3.1–P3.3）。默认折叠：表头 = 「立即推送两页」+ 展开/收起 + 一行摘要，
    /// 主操作永远可见可点；展开才是全套配置控件。
    /// 放在左列并算了高度的：右列现有内容已 390，加一块必破 760×520 不出现滚动条的目标；
    /// 左列折叠态总高 404、展开态 496，均低于 503 的可用高度，两种状态都不出滚动条。
    /// </summary>
    private void BuildPushSection()
    {
        _pushExpand.Click += (_, _) => SetPushOpen(!_pushOpen);
        _tip.SetToolTip(_pushEnabled,
            "保存推送配置。一次推送同时更新槽 1 与槽 2 两页；槽 2 不适用时只推槽 1（原因见状态行）。");
        _tip.SetToolTip(_pushMac,
            "极趣 Note 4 的 MAC（形如 AA:BB:CC:DD:EE:FF）。不确定时可点「获取列表」从云端拉取。");
        _tip.SetToolTip(_pushPage, "槽 1 图片页页位（1–5），槽 1 始终推送该页。");
        _tip.SetToolTip(_pushPage2,
            "槽 2 图片页页位（1–5）。不能与槽 1 同页位；槽 2 只在清灰以来有效日 ≥ 3 天时推送。");

        _pushHour.Minimum = 0;
        _pushHour.Maximum = 23;
        _pushPage.Minimum = 1;
        _pushPage.Maximum = 5;
        _pushPage.DecimalPlaces = 0;
        _pushPage2.Minimum = 1;
        _pushPage2.Maximum = 5;
        _pushPage2.DecimalPlaces = 0;
        _pushPage.Value = 1;
        _pushPage2.Value = 2;   // 与 PushOptions.PageId2 默认值一致，设置读取失败时也不会落成 1

        _pushEnabled.CheckedChanged += (_, _) => SavePush();
        _pushHour.ValueChanged += (_, _) => SavePush();
        _pushPage.ValueChanged += (_, _) => SavePush();
        _pushPage2.ValueChanged += (_, _) => SavePush();
        _pushMac.TextChanged += (_, _) =>
        {
            if (_loading) return;
            // 下拉选中时文本会先变成「别名 — MAC」，紧接着 SelectedIndexChanged 会回填纯 MAC 并保存；
            // 这里跳过展示态，避免把别名拼进 DeviceId 写一条中间值。
            var i = _pushMac.SelectedIndex;
            if (i >= 0 && i < _pushMac.Items.Count
                && _pushMac.Text == _pushMac.GetItemText(_pushMac.Items[i]))
                return;
            SavePush();
        };
        _pushMac.SelectedIndexChanged += (_, _) => OnPushMacSelected();
        _pushKey.TextChanged += (_, _) =>
        {
            // 回填 / 提交后清空不算"用户改过"；其余情况标记待提交（离开焦点或点推送时才加密落盘）
            if (!_suppressKeyDirty) _pushKeyDirty = true;
        };
        _pushKey.Leave += (_, _) => CommitApiKey();
        _pushKeyClear.Text = "清除";
        _pushKeyClear.Click += (_, _) =>
        {
            _pushKey.Text = "";
            CommitApiKey();
        };
        _pushFetch.Text = "获取列表";
        _pushFetch.Click += async (_, _) => await OnFetchDevices().ConfigureAwait(true);
        _pushNow.Text = "立即推送两页";
        _pushNow.Click += async (_, _) => await OnPushNow().ConfigureAwait(true);

        var body = Rows(26f, 30f, 26f, 30f);
        body.Controls.Add(FlowRow(_pushEnabled, TextLabel("推送时刻"), _pushHour), 0, 0);
        body.Controls.Add(FlowRow(TextLabel("设备 MAC"), _pushMac, _pushFetch), 0, 1);
        body.Controls.Add(FlowRow(TextLabel("槽 1 页位"), _pushPage, TextLabel("槽 2 页位"), _pushPage2), 0, 2);
        body.Controls.Add(FlowRow(TextLabel("API Key"), _pushKey, _pushKeyClear), 0, 3);
        _pushBody.Controls.Add(body);

        // 表头只放按钮：立即推送（主操作，折叠态也可见可点）+ 展开/收起。
        // 摘要行折叠时可见（展开时配置全在眼前，摘要就是冗余）；两条状态行始终可见——
        // 推送结果与槽 2 适用状态必须能在不展开分区的情况下看到，且不能藏在会折叠的正文里。
        var header = FlowRow(_pushNow, _pushExpand);

        _pushGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _pushGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));   // 表头
        _pushGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 20f));   // 摘要行（折叠时可见）
        _pushGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 20f));   // 槽 1 状态行（始终可见）
        _pushGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 20f));   // 槽 2 状态行（始终可见）
        _pushGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 0f));    // 正文（折叠时高 0）
        _pushGrid.Controls.Add(header, 0, 0);
        _pushGrid.Controls.Add(_pushSummary, 0, 1);
        _pushGrid.Controls.Add(_pushStatus, 0, 2);
        _pushGrid.Controls.Add(_pushSlot2, 0, 3);
        _pushGrid.Controls.Add(_pushBody, 0, 4);
        _pushSection.Controls.Add(_pushGrid);

        AddSection(_pushSection, toLeft: true);
    }

    /// <summary>整份替换 <see cref="PushOptions"/>（record + init，改值必须整体替换）。
    /// 值真变才落盘：重复触发到同一个值、程序性回填都不写文件。</summary>
    private void SavePush()
    {
        if (_loading) return;

        var s = SettingsStore.Load();
        var next = s.Push with
        {
            Enabled = _pushEnabled.Checked,
            DeviceId = _pushMac.Text.Trim(),
            PageId = ((int)_pushPage.Value).ToString(),
            PageId2 = ((int)_pushPage2.Value).ToString(),
            Hour = (int)_pushHour.Value
        };
        if (next == s.Push) return;

        s.Push = next;
        if (!TrySaveSettings(s, msg => SetPushStatus(msg, NoteKind.Err))) return;

        UpdatePushSummary();
        UpdateSlot2Status();   // 页位改到与槽 1 冲突时，状态行要立刻改口
    }

    /// <summary>
    /// 把 API Key 输入框里的明文加密落盘（DPAPI）。只在离开焦点 / 点「清除」/ 点「立即推送」时调——
    /// 每次击键都加密写盘太吵。加密失败走 <see cref="CredentialStore"/> 的内存降级（本次会话可推，重启后需重填）。
    /// 落盘后清空输入框：框里留明文既是安全隐患，也会让人误以为框里显示的就是当前生效值。
    /// </summary>
    private void CommitApiKey()
    {
        if (!_pushKeyDirty) return;
        _pushKeyDirty = false;

        var cleared = _pushKey.Text.Trim().Length == 0;
        var s = SettingsStore.Load();
        var ok = CredentialStore.TryEncryptAndStore(s, _pushKey.Text, out var err);
        var saved = TrySaveSettings(s, msg => SetPushStatus(msg, NoteKind.Err));

        if (saved)
            SetPushStatus(ok
                ? (cleared ? "API Key 已清除。" : "API Key 已加密保存。")
                : $"API Key 加密失败，仅本次会话有效（重启后需重新填写）：{err}",
                ok ? NoteKind.Ok : NoteKind.Err);

        // 清空也要抑制"变脏"标记，否则下一次 Leave 会把刚存好的 Key 当成待清除再清一遍
        _suppressKeyDirty = true;
        try { _pushKey.Text = ""; }
        finally { _suppressKeyDirty = false; }

        UpdatePushSummary();
    }

    /// <summary>下拉选中一项 → 框里换成纯 MAC（项文本是"别名 — MAC"，不能整个存进 DeviceId）。</summary>
    private void OnPushMacSelected()
    {
        if (_loading) return;
        var i = _pushMac.SelectedIndex;
        if (i < 0 || i >= _pushDevices.Count) return;

        _loading = true;
        try { _pushMac.Text = _pushDevices[i].DeviceId; }
        finally { _loading = false; }
        SavePush();
    }

    /// <summary>
    /// 「获取列表」：调 <c>GET /devices</c> 拉账号下设备，填进 MAC 下拉框（PT.1 的 GUI 落地）。
    /// 只有一台、或当前填的值不在列表里时，自动选中第一台——多数用户就一台设备。
    /// </summary>
    private async Task OnFetchDevices()
    {
        if (_fetching) return;
        _fetching = true;
        _pushFetch.Enabled = false;
        _pushFetch.Text = "获取中…";
        SetPushStatus("正在向极趣云查询设备列表…", NoteKind.Info);

        try
        {
            var s = SettingsStore.Load();
            var credential = CredentialStore.Resolve(s);
            if (credential.Key is not { } key)
            {
                // 缺 Key 与密文损坏分开提示：后者必须重新保存，而不是“请先填写”
                SetPushStatus(credential.Message, NoteKind.Err);
                return;
            }

            using var client = new ZectrixClient();
            var devices = await client.GetDevicesAsync(key).ConfigureAwait(true);

            _pushDevices.Clear();
            _pushMac.Items.Clear();
            foreach (var d in devices.Devices)
            {
                _pushDevices.Add(d);
                _pushMac.Items.Add(string.IsNullOrWhiteSpace(d.Alias) ? d.DeviceId : $"{d.Alias} — {d.DeviceId}");
            }

            if (_pushDevices.Count == 0)
            {
                SetPushStatus("云端账号下没有设备。请确认 Note 4 已开机、已联网、已绑定到本账号。", NoteKind.Err);
            }
            else
            {
                var current = _pushMac.Text.Trim();
                if (current.Length == 0 || !_pushDevices.Any(d => d.DeviceId == current))
                    _pushMac.SelectedIndex = 0;   // 经 OnPushMacSelected 回填纯 MAC
                SetPushStatus($"找到 {_pushDevices.Count} 台设备"
                            + (_pushDevices.Count > 1 ? "，请从下拉列表选择。" : "，已填入。"), NoteKind.Ok);
            }
        }
        catch (Exception ex)
        {
            SetPushStatus("获取设备列表失败：" + ex.Message, NoteKind.Err);
        }
        finally
        {
            if (!IsDisposed)
            {
                _pushFetch.Enabled = true;
                _pushFetch.Text = "获取列表";
            }
            _fetching = false;
        }
    }

    /// <summary>
    /// 「立即推送两页」。异步执行，按钮三态：推送中… / 已推送 / 失败：{原因}；
    /// 当日幂等跳过是第四种中性态（<see cref="PushOutcome.Skipped"/>）——手动按钮遵循每日一次
    /// ，想反复推请用命令行 <c>--force</c>。槽 2 不适用时只推槽 1，结果里会写原因。
    /// </summary>
    private async Task OnPushNow()
    {
        if (_pushing) return;
        if (_pushKeyDirty) CommitApiKey();   // 刚输完 Key 就点推送：先加密落盘再推
        if (!_pushOpen) SetPushOpen(true);   // 折叠态点推送：先展开，让过程与结果有地方显示

        _pushing = true;
        _pushNow.Enabled = false;
        _pushNow.Text = "推送中…";
        SetPushStatus($"{DateTime.Now:HH:mm:ss} 推送中…（生成快照 → 极趣云）", NoteKind.Info);

        PushOutcome outcome;
        try
        {
            var s = SettingsStore.Load();
            // force: true —— 按钮是用户明确的一次点击，"想推就推"；每日幂等是调度的事，不绑手动按钮。
            // 日志回调把重试进度也实时打到状态行（带秒级时间戳）：断网时用户能看到"第 1 次失败、30 秒后重试"，
            // 而不是干等一分钟没有下文。
            outcome = await PushService.PushAsync(
                s, force: true, dryRun: false,
                w => SetPushStatus($"{DateTime.Now:HH:mm:ss} {w}", NoteKind.Info)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // PushService 自己已吞掉网络错误；这里兜的是"根本没想到会抛"的情况，绝不转圈
            outcome = new PushOutcome(false, "推送异常：" + ex.Message);
        }

        if (IsDisposed) return;

        _pushing = false;
        _pushNow.Enabled = true;
        _pushNow.Text = "立即推送两页";

        // 秒级时间戳是硬需求（2026-10-15 用户反馈）：要能对着设备判断"这一屏是不是刚才那次推的"
        var now = DateTime.Now.ToString("HH:mm:ss");
        if (outcome.Ok)
            SetPushStatus($"{now} {outcome.Message}", NoteKind.Ok);
        else if (outcome.Skipped)
            SetPushStatus($"{now} {outcome.Message}", NoteKind.Info);   // 今日已推过：中性提示，不是失败
        else
            SetPushStatus($"{now} 失败：{outcome.Message}", NoteKind.Err);

        // 本次会话已手动推过：槽 1 行留着这次的结果，不许 RefreshData 用文件状态覆盖掉；
        // 槽 2 行仍按状态文件 + 当前适用性刷新，让「不适用」的原因不因一次手动推送而丢失。
        _pushActed = true;
        UpdateSlot2Status();
    }

    /// <summary>「上次推送」行：读 <c>push-state.json</c> 的槽 1 状态，日期 + 成功 / 失败原因。</summary>
    private void RefreshPushLast()
    {
        var (date, result, error) = PushService.GetLastStatus(EinkSlot.Slot1);

        if (string.IsNullOrWhiteSpace(date))
        {
            SetPushStatus("槽 1：从未推送", NoteKind.Info);
            return;
        }

        switch (result)
        {
            case "ok":
                SetPushStatus($"槽 1：{date} · 上次成功", NoteKind.Ok);
                break;
            case "error":
                SetPushStatus($"槽 1：{date} · 上次失败：{error}", NoteKind.Err);
                break;
            default:
                SetPushStatus($"槽 1：{date} · {result}", NoteKind.Info);
                break;
        }
    }

    /// <summary>把槽 2 适用性算出来：与推送 / CLI 共用 <see cref="EinkSnapshotBuilder.Slot2Applicability"/> 一处判定与文案。
    /// 每次切回设置页重算（读 daily CSV），不做缓存；D 项起只做轻量事实扫描，不再为这一行状态构建完整 TrendReport。
    /// 本轮起扫描与判定移入后台线程：UI 线程只取共享报告快照并回填，首次进入不再等 CSV 全量解析。</summary>
    private void RefreshSlot2ApplicabilityAsync(AppSettings s)
    {
        if (IsDisposed) return;

        if (s.CleanDate is null)
        {
            _slot2Applicable = false;
            _slot2Pending = false;
            _slot2Reason = "未设置清灰基准日";
            return;
        }

        // 共享报告是宿主上的普通字典，只允许在 UI 线程读；后台线程只做 daily 轻量扫描。
        var shared = MainWindowHost.PeekSharedReport(s, TrendWindow.Week, true);
        var gen = ++_slot2Gen;
        _slot2Pending = true;
        _slot2Reason = "读取中…";

        Task.Run(() =>
        {
            bool applicable;
            string reason;
            try
            {
                // 命中共享报告就直接用，不新建；未命中才做轻量扫描——
                // 只回答"首末日 + 窗口内有效日数"，不构建完整 TrendReport。
                // 两种来源最后都走同一个 Slot2Applicability，判定与文案只有一处。
                Slot2Verdict verdict;
                if (shared is { } report)
                {
                    verdict = EinkSnapshotBuilder.Slot2Applicability(s, report.FirstDay, report.LastDay,
                        EinkSnapshotBuilder.Slot2EffectiveDays(s, report));
                }
                else
                {
                    var (first, last, effectiveDays) = TrendAnalyzer.ScanEinkSlot2Facts(s, onAc: true);
                    verdict = EinkSnapshotBuilder.Slot2Applicability(s, first, last, effectiveDays);
                }

                applicable = verdict.Applicable;
                reason = verdict.Applicable
                    ? $"适用（{verdict.SpanDays} 天 / 窗口 {verdict.SmoothWindowDays} 天）"
                    : ShortReason(verdict.SkipReason);
            }
            catch (Exception ex)
            {
                applicable = false;
                reason = "适用性读取失败：" + ex.Message;
            }

            if (IsDisposed) return;
            try
            {
                BeginInvoke(new Action(() => ApplySlot2Applicability(applicable, reason, gen)));
            }
            catch { /* 窗口已关闭、句柄已销毁 */ }
        });
    }

    private void ApplySlot2Applicability(bool applicable, string reason, int gen)
    {
        if (IsDisposed || gen != _slot2Gen) return;

        _slot2Applicable = applicable;
        _slot2Reason = reason;
        _slot2Pending = false;
        UpdateSlot2Status();
    }

    /// <summary>把 SkipReason 压成窄列也放得下的短句；先判"晚于"，否则它会被"清灰"分支吞掉。</summary>
    private static string ShortReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "原因未知";
        if (reason.Contains("晚于")) return "清灰日晚于最后数据日";
        if (reason.Contains("未设置清灰")) return "未设置清灰基准日";
        if (reason.Contains("有效日")) return "有效日 <3 天";
        if (reason.Contains("页位") || reason.Contains("同页")) return "页位与槽 1 相同";
        return reason;
    }

    /// <summary>槽 2 状态行：上次推送（逐槽读 state）+ 当前是否适用 + 页位冲突提示。</summary>
    private void UpdateSlot2Status()
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(UpdateSlot2Status)); } catch { /* 句柄没了就认命 */ }
            return;
        }

        var s = SettingsStore.Load();
        var page1 = PushService.ResolvePageId(s.Push.PageId, 1);
        var page2 = PushService.ResolvePageId(s.Push.PageId2, 2);

        var (date, result, error) = PushService.GetLastStatus(EinkSlot.Slot2);
        string last;
        if (string.IsNullOrWhiteSpace(date)) last = "从未推送";
        else if (result == "ok") last = $"{date} 成功";
        else if (result == "error") last = $"{date} 失败：{error}";
        else last = $"{date} {result}";

        NoteKind kind;
        string text;
        if (!page1.Ok || !page2.Ok)
        {
            // 与推送 / CLI 同一结论：非法页位明确报出来，不静默按回落值继续
            var problems = new List<string>(2);
            if (!page1.Ok) problems.Add($"槽 1 {page1.Problem}");
            if (!page2.Ok) problems.Add($"槽 2 {page2.Problem}");
            kind = NoteKind.Err;
            text = $"页位无效：{string.Join("；", problems)}";
        }
        else if (page1.Value == page2.Value)
        {
            kind = NoteKind.Err;
            text = $"槽 2：页位与槽 1 相同（{page2.Value}），将跳过";
        }
        else if (_slot2Pending)
        {
            // 适用性在后台算：先给占位，算完会由 ApplySlot2Applicability 再刷一次
            kind = NoteKind.Info;
            text = $"槽 2：{last} · 适用性读取中…";
        }
        else if (_slot2Applicable)
        {
            kind = NoteKind.Ok;
            text = $"槽 2：{last} · {_slot2Reason}";
        }
        else
        {
            // 「不适用」是正常的跳过态（无清灰日 / 有效日不足），不是失败，用中性色；
            // 只有读取异常才按失败色显示
            kind = _slot2Reason.StartsWith("适用性读取失败") ? NoteKind.Err : NoteKind.Info;
            text = $"槽 2：{last} · 不适用（{_slot2Reason}）";
        }

        _tip.SetToolTip(_pushSlot2, text);
        _pushSlot2Kind = kind;
        _pushSlot2.Text = text;
        _pushSlot2.ForeColor = NoteColor(kind);
    }

    /// <summary>折叠态摘要行：一眼看到"开没开、几点推、两槽各推哪页、Key 在不在"。</summary>
    private void UpdatePushSummary()
    {
        var s = SettingsStore.Load();
        var p = s.Push;
        var page1 = PushService.ResolvePageId(p.PageId, 1);
        var page2 = PushService.ResolvePageId(p.PageId2, 2);
        var credential = CredentialStore.Resolve(s);

        var pages = !page1.Ok || !page2.Ok
            ? "页位无效（需 1–5）"
            : page1.Value == page2.Value
                ? $"两槽均 {page1.Value} 页（冲突）"
                : $"槽 1 → {page1.Value} 页 / 槽 2 → {page2.Value} 页";
        var key = credential.Status switch
        {
            CredentialStatus.Ok => "Key 已保存",
            CredentialStatus.MemoryOnly => "Key 仅本会话（重启失效）",
            CredentialStatus.Corrupt => "Key 损坏，需重新保存",
            _ => "Key 未配置"
        };

        var parts = new List<string>
        {
            p.Enabled ? "已启用" : "未启用",
            $"{Math.Clamp(p.Hour, 0, 23):00}:00",
            pages,
            key
        };
        _pushSummary.Text = string.Join(" · ", parts);
    }

    private void SetPushOpen(bool open)
    {
        _pushOpen = open;
        _pushExpand.Text = open ? "收起" : "展开";
        _pushBody.Visible = open;
        _pushSummary.Visible = !open;   // 摘要只在折叠时出现（展开时配置本身就是摘要）
        LayoutSections();
    }

    /// <summary>推送区高度：折叠 = 标题 32 + 表头 26 + 摘要 20 + 两条状态 40 + 底部 8 = 126；
    /// 展开 = 收起摘要行、加正文 112。左列折叠态 404、展开态 496，均不超 503 的可用高度。</summary>
    private void UpdatePushHeight()
    {
        _pushGrid.RowStyles[1] = new RowStyle(SizeType.Absolute, _pushOpen ? 0f : 20f);   // 摘要：展开时让位给正文
        _pushGrid.RowStyles[4] = new RowStyle(SizeType.Absolute, _pushOpen ? PushBodyHeight : 0f);
        _pushSection.Height = 32 + 26 + (_pushOpen ? 0 : 20) + 20 + 20
                              + (_pushOpen ? PushBodyHeight : 0) + 8;
    }

    /// <summary>写状态行（P3.2 操作反馈与 P3.3 上次推送共用这一行）。
    /// 会被后台线程调到（推送重试的日志回调），必须自己 marshaling 回 UI 线程。</summary>
    private void SetPushStatus(string text, NoteKind kind)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(() => SetPushStatus(text, kind))); } catch { /* 句柄没了就认命 */ }
            return;
        }

        _pushStatus.Text = text;
        _pushStatusKind = kind;
        _pushStatus.ForeColor = NoteColor(kind);
    }

    private Color NoteColor(NoteKind kind) => kind switch
    {
        NoteKind.Ok => Theme.Current.Cool,
        NoteKind.Err => Theme.Current.Hot,
        _ => Theme.Current.TextDim
    };
#endif

    private void BuildThemeSection()
    {
        var sec = new SettingsSection("外观") { Height = 98 };

        _themeBox.Items.AddRange(["跟随系统", "浅色", "深色"]);
        _themeBox.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            Theme.SetMode(_themeBox.SelectedIndex switch
            {
                1 => ThemeMode.Light,
                2 => ThemeMode.Dark,
                _ => ThemeMode.Follow
            });
        };
        _themeHint.Text = "跟随系统 = Win10「个性化 → 颜色 → 默认应用模式」";

        var body = Rows(32f);
        body.Controls.Add(FlowRow(TextLabel("主题"), _themeBox), 0, 0);
        body.Controls.Add(_themeHint, 0, 1);
        sec.Controls.Add(body);

        AddSection(sec, toLeft: false);
    }

    private void BuildBoundsSection()
    {
        _boundsToggle.Click += (_, _) => SetBoundsOpen(!_boundsOpen);

        _boundsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _boundsGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));
        _boundsGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 0f));
        _boundsGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 0f));
        _boundsGrid.Controls.Add(TwoColumn(_boundsToggle, _boundsSummary), 0, 0);
        _boundsGrid.Controls.Add(_boundsBody, 0, 1);
        _boundsGrid.Controls.Add(_boundsHint, 0, 2);
        _boundsSection.Controls.Add(_boundsGrid);

        AddSection(_boundsSection, toLeft: false);
    }

    private void BuildCleanDateSection()
    {
        var sec = new SettingsSection("清灰基准日") { Height = 96 };

        _cleanDate.ValueChanged += (_, _) => SaveCleanDate();

        var body = Rows(32f);
        body.Controls.Add(FlowRow(TextLabel("最近一次清灰"), _cleanDate), 0, 0);
        body.Controls.Add(_cleanNote, 0, 1);
        sec.Controls.Add(body);

        AddSection(sec, toLeft: false);
    }

    private void BuildMaintenanceSection()
    {
        var sec = new SettingsSection("维护") { Height = 98 };

        _selfCheck.Text = "重新自检";
        _selfCheck.Click += (_, _) => RunSelfCheck();

        //【B6】与「今日概览」页同一个动作的第二入口：设置页是用户习惯翻的地方，
        //而新用户往往先看到今日页的空态。两个入口指向同一段后端逻辑。
        // 两个按钮并排放在同一行（FlowRow 不换行），分区高度不变 = 98，
        // 所以 760x520 下右列底部仍不超过 419（红线）。
        _rebind.Text = "重新绑定传感器";
        _rebind.Click += (_, _) => RunRebind();
        _maintenanceNote.Text =
            $"自检需连续采样约 {SensorStabilityProbe.EffectiveTicks + SensorStabilityProbe.WarmupTicks} 秒；重新绑定只需几秒。";

        var body = Rows(34f);
        body.Controls.Add(FlowRow(_selfCheck, _rebind), 0, 0);
        body.Controls.Add(_maintenanceNote, 0, 1);
        sec.Controls.Add(body);

        AddSection(sec, toLeft: false);
    }

    // ==================================================================
    // 开机自启（红线：状态必须是真实四态）
    // ==================================================================

    private void ToggleAutoStart()
    {
        // 进入本页时状态已在后台查询并回填；这里用回填值判断方向，
        // 不再点击一次就同步起两次 schtasks（查询卡住时按钮响应会被拖住）。
        var st = _autoStartStatus;
        if (st is null)
        {
            // 尚未拿到状态（上次查询失败）：只重试读取，绝不按默认值盲操作。
            RefreshAutoStartAsync();
            return;
        }

        if (st.IsEnabled)
        {
            if (!AutoStart.Disable())
            {
                MessageBox.Show(FindForm(),
                    "取消开机自启失败\n\n删除计划任务被拒绝（通常需要管理员权限）。\n"
                    + "请以管理员身份重新运行本程序后再试。",
                    "ThermalTrace", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        else
        {
            var result = AutoStart.Enable();
            if (!result.Ok)
            {
                // 原样把原因给用户看 —— 绝不能像旧实现那样"没抛异常就当成功"
                // （自启的真实状态）
                MessageBox.Show(FindForm(),
                    "注册开机自启失败\n\n" + result.Message,
                    "ThermalTrace", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Enable / Disable 内部会清缓存，下一次查询一定拿到切换后的真实状态；
        // 仍然走后台回填，UI 线程不等待 schtasks。
        _autoStartStatus = null;
        RefreshAutoStartAsync();
    }

    /// <summary>
    /// 自启状态异步回填：进入页面先显示「读取中」占位，查询放到后台线程。
    /// schtasks 是整页唯一需要等待系统计划任务服务的同步调用（旧实现两次合计 123–146 ms，
    /// 且子进程无超时）；放在 UI 线程上就是首次点击卡顿的主因。
    /// </summary>
    private void RefreshAutoStartAsync()
    {
        if (IsDisposed) return;

        var gen = ++_autoStartGen;
        ShowAutoStartLoading();

        Task.Run(() =>
        {
            AutoStartStatus? st = null;
            try { st = AutoStart.Status(); }
            catch { /* 查询失败按「读不到」回填，不让异常进入 UI 线程 */ }

            if (IsDisposed) return;
            try
            {
                BeginInvoke(new Action(() => ApplyAutoStart(st, gen)));
            }
            catch { /* 窗口已关闭、句柄已销毁 */ }
        });
    }

    private void ShowAutoStartLoading()
    {
        _asToggle.Enabled = false;
        _asToggle.Primary = false;
        _asToggle.Text = "读取中…";
        _asStatus.Text = "读取中…";
        _asDetail.Text = "正在查询计划任务状态…";
    }

    private void ApplyAutoStart(AutoStartStatus? st, int gen)
    {
        if (IsDisposed || gen != _autoStartGen) return;

        _asToggle.Enabled = true;

        if (st is null)
        {
            // 查询异常（超时本身在 AutoStart 内按「查不到」缓存，不会走到这里）。
            // 保留一个可点的重试入口，不显示四态中的任何一态以免误导。
            _asToggle.Text = "重新读取自启状态";
            _asToggle.Primary = false;
            _asStatus.Text = "自启状态读取失败";
            _asDetail.Text = "未能获取计划任务状态，点击按钮可重试。";
            return;
        }

        _autoStartStatus = st;
        _asToggle.Text = st.IsEnabled ? "取消开机自启" : "启用开机自启";
        _asToggle.Primary = !st.IsEnabled;
        _asStatus.Text = st.Describe();

        var lines = new List<string>
        {
            $"上次运行 {(st.LastRun is null ? "从未运行" : st.LastRun.Value.ToString("yyyy-MM-dd HH:mm"))}"
            + $" · 上次结果 {st.LastResultText}"
        };
        if (st.Command.Length > 0) lines.Add(st.Command);
        if (!Elevation.IsElevated)
            lines.Add("当前进程未提权 —— 注册 / 取消自启都需要管理员权限。");
        _asDetail.Text = string.Join("\n", lines);
    }

    // ==================================================================
    // 位置与环境气温
    // ==================================================================

    private void SaveAmbient()
    {
        if (_loading) return;

        var s = SettingsStore.Load();
        // AmbientOptions 是 record：必须整体替换，不能就地改属性
        var next = s.Ambient with
        {
            Latitude = (double)_lat.Value,
            Longitude = (double)_lon.Value,
            LocationName = _city.Text.Trim(),
            UseOnlineWeather = _online.Checked
        };

        // 值没变就不写盘：程序性回填、同一个值重复触发都跳过
        if (next == s.Ambient)
        {
            ClearAmbientSaveError();
            return;
        }

        s.Ambient = next;
        if (!TrySaveSettings(s, msg =>
            {
                _ambientNote.ForeColor = Theme.Current.Hot;
                _ambientNote.Text = msg;
            }))
            return;

        ClearAmbientSaveError();
    }

    /// <summary>上一次保存失败的提示还挂在注记行上时，换回普通说明色，避免用户以为新值也没存上。</summary>
    private void ClearAmbientSaveError()
    {
        if (!_ambientNote.Text.StartsWith("设置保存失败", StringComparison.Ordinal)) return;
        _ambientNote.ForeColor = Theme.Current.TextDim;
        _ambientNote.Text = "设置已保存。";
    }

    /// <summary>
    /// 统一落盘入口。写盘失败只在对应分区给出提示，绝不把异常抛进 UI 消息循环。
    /// 返回 false 时调用方不要再做“数据已变”的后续动作（标脏、刷新摘要）。
    /// </summary>
    private static bool TrySaveSettings(AppSettings s, Action<string> report)
    {
        try
        {
            SettingsStore.Save(s);
            return true;
        }
        catch (Exception ex)
        {
            report($"设置保存失败：{ex.Message}（本次修改未写入磁盘）");
            return false;
        }
    }

    /// <summary>
    /// 「立即重新定位」。<c>LocateAsync</c> 失败只返回 null，不抛异常，
    /// 所以这里必须自己判空并提示——静默失败会让用户以为"定位好了"。
    /// </summary>
    private async void OnRelocate(object? sender, EventArgs e)
    {
        _relocate.Enabled = false;
        _ambientNote.Text = "正在通过 IP 定位…";

        AmbientWeather.Geo? geo;
        try
        {
            geo = await AmbientWeather.LocateAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            geo = null;
            _ambientNote.Text = "定位异常：" + ex.Message;
        }

        if (IsDisposed) return;
        _relocate.Enabled = true;

        if (geo is null)
        {
            if (!_ambientNote.Text.StartsWith("定位异常"))
                _ambientNote.Text = "自动定位失败（网络不可达或服务不可用）。请在上面手动填写纬度 / 经度 / 城市名。";
            _ambientNote.ForeColor = Theme.Current.Hot;
            return;
        }

        SettingsStore.UpdateAmbientLocation(geo);

        _loading = true;
        try
        {
            _lat.Value = SafeDecimal(geo.Latitude, _lat.Minimum, _lat.Maximum);
            _lon.Value = SafeDecimal(geo.Longitude, _lon.Minimum, _lon.Maximum);
            _city.Text = geo.Name ?? "";
        }
        finally
        {
            _loading = false;
        }

        _ambientNote.ForeColor = Theme.Current.TextDim;
        _ambientNote.Text = $"已定位到 {geo.Name}（{geo.Latitude:0.0000}, {geo.Longitude:0.0000}）并写入设置。";
    }

    private static decimal Clamp(decimal v, decimal min, decimal max) => v < min ? min : v > max ? max : v;

    /// <summary>double → decimal 并夹到区间内。NaN / Infinity 直接落到 0（(decimal)NaN 会抛）。</summary>
    private static decimal SafeDecimal(double v, decimal min, decimal max) =>
        double.IsFinite(v) ? Clamp((decimal)v, min, max) : 0m;

    private static void SetupCoordinateBox(NumericUpDown box, decimal min, decimal max)
    {
        box.Minimum = min;
        box.Maximum = max;
        box.DecimalPlaces = 4;
        box.Increment = 0.01m;
        box.Width = 86;
        box.Margin = new Padding(0, 4, 10, 0);
    }

    // ==================================================================
    // 负载类别边界
    // ==================================================================

    private void SetBoundsOpen(bool open)
    {
        _boundsOpen = open;
        _boundsToggle.Text = open ? "收起" : "展开";
        _boundsBody.Visible = open;
        _boundsHint.Visible = open;
        LayoutSections();
    }

    /// <summary>边界输入框随 <c>CategoryLowerBounds</c> 的长度重建（默认 5 档）。
    /// 首档下限固定为 0（G3b）：区间从 0 起算，输入框只读并标注，避免配出"这一档永远为空"。</summary>
    private void RebuildBoundBoxes(AppSettings s)
    {
        var bounds = s.CategoryLowerBounds ?? [];

        if (_boundBoxes.Count != bounds.Length)
        {
            _boundsBody.Controls.Clear();
            _boundBoxes.Clear();

            for (int i = 0; i < bounds.Length; i++)
            {
                var fixedFirst = i == 0;
                var box = new NumericUpDown
                {
                    Minimum = 0,
                    Maximum = fixedFirst ? 0 : 99,
                    DecimalPlaces = 0, Increment = 1,
                    Width = 58, Margin = new Padding(0, 3, 12, 0),
                    ReadOnly = fixedFirst,
                    Value = fixedFirst ? 0 : Math.Clamp(bounds[i], 0, 99)
                };
                box.ValueChanged += (_, _) => SaveBounds();
                _boundBoxes.Add(box);

                _boundsBody.Controls.Add(TextLabel(s.CategoryName(i) + (fixedFirst ? "（起点固定 0）" : "")));
                _boundsBody.Controls.Add(box);
            }

            Theme.ApplyTree(_boundsBody);
            ThemeOps.Flatten(_boundsBody, Theme.Current.Surface);
        }
        else
        {
            for (int i = 0; i < bounds.Length; i++)
                _boundBoxes[i].Value = i == 0 ? 0 : Math.Clamp(bounds[i], 0, 99);
        }
    }

    private void SaveBounds()
    {
        if (_loading || _boundBoxes.Count == 0) return;

        var arr = new int[_boundBoxes.Count];
        for (int i = 0; i < arr.Length; i++)
            arr[i] = (int)Math.Clamp(_boundBoxes[i].Value, 0m, 99m);

        // 首档固定 0：低于首档下限的样本按 CategoryOf 也归入首档，配置里的非 0 首档不参与归档。
        arr[0] = 0;

        var s = SettingsStore.Load();

        // 相等或倒序意味着前一档的区间为空，拒绝保存并指出是哪一档（G3b）。
        var error = CategoryCaliber.Validate(arr, s.CategoryNames);
        if (error is not null)
        {
            ShowBoundsWarning(error);
            return;
        }

        // 值没变就不写盘：重建控件、把同一组边界重复设一遍都不算修改
        if (s.CategoryLowerBounds is { } current && current.SequenceEqual(arr)) return;

        s.CategoryLowerBounds = arr;
        if (!TrySaveSettings(s, msg =>
            {
                _boundsHint.Text = msg;
                _boundsHint.ForeColor = Theme.Current.Hot;
            }))
            return;

        // 今日卡片 / 趋势图已访问过的话还留着上一版边界的数据：宿主把我页标脏，
        // 切过去时才按新边界重取，不在每次微调时重读全部 CSV。
        MainWindowHost.MarkDataStale();
        UpdateBoundsSummary();
    }

    /// <summary>拒绝保存时把原因留在提示行；下一次成功保存或重新进入本页会覆盖掉。</summary>
    private void ShowBoundsWarning(string message)
    {
        _boundsValid = false;
        _boundsHint.Text = message;
        _boundsHint.ForeColor = Theme.Current.Hot;
    }

    private void UpdateBoundsSummary()
    {
        var s = SettingsStore.Load();
        var bounds = s.CategoryLowerBounds ?? [];

        // 摘要与概览页头共用 CategoryCaliber：首档按 0 起算、上界 = 下一档下限 − 1、末档用 ≥。
        _boundsSummary.Text = CategoryCaliber.Describe(bounds, s.CategoryNames);
        // 窄窗口下 AutoEllipsis 会截断，悬停给出完整口径（G4 设置页收尾）。
        _tip.SetToolTip(_boundsSummary, _boundsSummary.Text);

        var invalid = CategoryCaliber.Validate(bounds, s.CategoryNames);
        var ignored = CategoryCaliber.FirstBoundIgnored(bounds);
        _boundsValid = invalid is null;
        _firstBoundIgnored = ignored is not null;

        if (invalid is not null)
        {
            _boundsHint.Text = invalid;
            _boundsHint.ForeColor = Theme.Current.Hot;
        }
        else if (ignored is not null)
        {
            _boundsHint.Text = ignored;
            _boundsHint.ForeColor = Theme.Current.Warm;
        }
        else
        {
            _boundsHint.Text = "按单核峰值 % 分档。改动后切回概览 / 趋势页时重取，不需要重新采集或重启。";
            _boundsHint.ForeColor = Theme.Current.TextDim;
        }
    }

    // ==================================================================
    // 清灰基准日
    // ==================================================================

    private void SaveCleanDate()
    {
        if (_loading) return;

        var iso = _cleanDate.Value.ToString("yyyy-MM-dd");
        var s = SettingsStore.Load();
        if (s.CleanDateIso == iso) return;

        s.CleanDateIso = iso;
        if (!TrySaveSettings(s, msg =>
            {
                _cleanNote.ForeColor = Theme.Current.Hot;
                _cleanNote.Text = msg;
            }))
            return;

        // 解读页的「自上次清灰」与提示依赖这一项：切回解读 / 趋势页时按新基准日重取。
        MainWindowHost.MarkDataStale();
        UpdateCleanNote();
    }

    private void UpdateCleanNote()
    {
        var s = SettingsStore.Load();
        if (s.CleanDate is not { } d)
        {
            _cleanNote.Text = "未设置基准日。首次运行时会自动记为今天。";
            return;
        }

        var days = (DateOnly.FromDateTime(DateTime.Today).DayNumber - d.DayNumber);
        _cleanNote.Text = $"距上次清灰 {Math.Max(0, days)} 天 · 只进趋势报告表头，不重新采集任何数据。";
    }

    // ==================================================================
    // 重新自检
    // ==================================================================

    private const string EmptySelfCheckMessage = "自检未产生输出。请确认已以管理员权限运行。";

    private void RunSelfCheck()
    {
        if (_checking) return;
        _checking = true;
        _selfCheck.Enabled = false;
        _selfCheck.Text = "自检中…";

        // 后台采集与截断在 MaintenanceActions 里，与概览页、托盘共用同一份实现。
        MaintenanceActions.RunSelfCheck(EmptySelfCheckMessage, FinishSelfCheck);
    }

    private void FinishSelfCheck(string text)
    {
        if (IsDisposed) return;
        if (IsHandleCreated && InvokeRequired)
        {
            BeginInvoke(new Action(() => FinishSelfCheck(text)));
            return;
        }

        _checking = false;
        _selfCheck.Enabled = true;
        _selfCheck.Text = "重新自检";

        MessageBox.Show(FindForm(), text, "ThermalTrace — 传感器自检",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ==================================================================
    // 重新绑定传感器（与「今日概览」页同一动作）
    // ==================================================================

    private const string RebindNotFoundMessage =
        "未能在本机找到可用的 CPU 温度传感器，绑定未保存（配置保持原样）。\n\n"
        + "请确认是否以管理员身份运行，以及安全软件是否拦截了 WinRing0 驱动。\n"
        + "详见 README 的常见问题。";

    private const string RebindSavedHeader = "绑定已保存：\n\n";

    private void RunRebind()
    {
        if (_rebinding) return;
        _rebinding = true;
        _rebind.Enabled = false;
        _rebind.Text = "绑定中…";

        MaintenanceActions.RunRebind(_sampler, RebindNotFoundMessage, RebindSavedHeader, FinishRebind);
    }

    private void FinishRebind(string text)
    {
        if (IsDisposed) return;
        if (IsHandleCreated && InvokeRequired)
        {
            BeginInvoke(new Action(() => FinishRebind(text)));
            return;
        }

        _rebinding = false;
        _rebind.Enabled = true;
        _rebind.Text = "重新绑定传感器";

        MessageBox.Show(FindForm(), text, "ThermalTrace — 传感器绑定",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ==================================================================
    // 取数 / 刷新
    // ==================================================================

    /// <summary>
    /// 从 settings.json 回填所有控件。每次切回本页都会调一次（<see cref="OnVisibleChanged"/>），
    /// 保证用户在终端改了配置、或重新定位写回了经纬度，GUI 上看到的是同一份值。
    /// </summary>
    public void RefreshData()
    {
        if (IsDisposed) return;

        var s = SettingsStore.Load();
        _loading = true;
        try
        {
            _lat.Value = SafeDecimal(s.Ambient.Latitude, _lat.Minimum, _lat.Maximum);
            _lon.Value = SafeDecimal(s.Ambient.Longitude, _lon.Minimum, _lon.Maximum);
            _city.Text = s.Ambient.LocationName ?? "";
            _online.Checked = s.Ambient.UseOnlineWeather;

            _themeBox.SelectedIndex = s.Ui.ThemeMode switch
            {
                ThemeMode.Light => 1,
                ThemeMode.Dark => 2,
                _ => 0
            };

            RebuildBoundBoxes(s);

            var clean = s.CleanDate ?? DateOnly.FromDateTime(DateTime.Today);
            _cleanDate.Value = new DateTime(clean.Year, clean.Month, clean.Day);

#if PUSH_ENABLED
            _pushEnabled.Checked = s.Push.Enabled;
            _pushHour.Value = Math.Clamp(s.Push.Hour, 0, 23);
            _pushMac.Text = s.Push.DeviceId ?? "";
            _pushPage.Value = PushService.ResolvePageId(s.Push.PageId, 1).Value;
            _pushPage2.Value = PushService.ResolvePageId(s.Push.PageId2, 2).Value;
            _pushKey.Text = "";
            _pushKeyDirty = false;
#endif
        }
        finally
        {
            _loading = false;
        }

        UpdateBoundsSummary();
        UpdateCleanNote();
#if PUSH_ENABLED
        UpdatePushSummary();
        // 槽 2 的适用性每次切回本页都按当前设置重算（清灰日 / 数据天数可能已在别处改过）。
        // 扫描放到后台，这里只置「读取中」占位，结果回来再回填。
        RefreshSlot2ApplicabilityAsync(s);
        // 本次会话已经手动推过：槽 1 状态行留着那次的结果（带时间戳），不让文件状态覆盖——
        // 否则每次切回本页 / 重开窗口，"19:52:33 已推送…"就被打回"上次推送：…"，用户刚看的结果没了；
        // 槽 2 行仍要刷新，它承载的是适用性，不是本次操作反馈。
        if (!_pushActed) RefreshPushLast();
        UpdateSlot2Status();
#endif
        // 自启状态同样后台查询：先显示「读取中」，schtasks 返回后再回填。
        RefreshAutoStartAsync();

        _boundsToggle.Text = _boundsOpen ? "收起" : "展开";
        LayoutSections();
        ApplyColors();
    }

    // ==================================================================
    // 排版
    // ==================================================================

    private void LayoutSections()
    {
        if (_host.ClientSize.Width <= 0) return;

        DoLayout(_host.ClientSize.Width - ScrollReserve());

        // 纵向滚动条一出现，可用宽度就少一条；宽度变了再排一次（最多两次，必收敛）
        int again = _host.ClientSize.Width - ScrollReserve();
        if (again != _lastWidth) DoLayout(again);
    }

    private int ScrollReserve() =>
        _host.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth + 2 : 2;

    private void DoLayout(int width)
    {
        if (width <= 0) return;
        _lastWidth = width;

        int colW = Math.Max(220, (width - ColGap) / 2);

        UpdateBoundsHeight(colW);
#if PUSH_ENABLED
        UpdatePushHeight();
#endif

        int y = 0;
        foreach (var sec in _left)
        {
            sec.SetBounds(0, y, colW, sec.Height);
            y += sec.Height + RowGap;
        }
        int leftBottom = Math.Max(0, y - RowGap);

        y = 0;
        foreach (var sec in _right)
        {
            sec.SetBounds(colW + ColGap, y, colW, sec.Height);
            y += sec.Height + RowGap;
        }
        int rightBottom = Math.Max(0, y - RowGap);

        _host.AutoScrollMinSize = new Size(0, Math.Max(leftBottom, rightBottom));
    }

    /// <summary>
    /// 边界区展开时高度取决于"换行成几行"：档位数多、列窄时会多占一行，
    /// 所以按实际宽度问一次 FlowLayoutPanel 的期望高度，而不是写死。
    /// </summary>
    private void UpdateBoundsHeight(int colW)
    {
        if (!_boundsOpen)
        {
            _boundsGrid.RowStyles[1] = new RowStyle(SizeType.Absolute, 0f);
            _boundsGrid.RowStyles[2] = new RowStyle(SizeType.Absolute, 0f);
            _boundsSection.Height = BoundsCollapsedHeight;
            return;
        }

        int innerW = Math.Max(120, colW - 28);
        int bodyH = Math.Max(32, _boundsBody.GetPreferredSize(new Size(innerW, 0)).Height + 6);

        _boundsGrid.RowStyles[1] = new RowStyle(SizeType.Absolute, bodyH);
        _boundsGrid.RowStyles[2] = new RowStyle(SizeType.Absolute, 22f);
        _boundsSection.Height = 32 + 26 + bodyH + 22 + 8;
    }

    // ==================================================================
    // 主题
    // ==================================================================

    private void ApplyColors()
    {
        var p = Theme.Current;
        BackColor = p.Bg;
        ForeColor = p.Text;
        _host.BackColor = p.Bg;

        foreach (var sec in _left.Concat(_right))
        {
            // 标准控件按类型刷（TextBox / NumericUpDown / ComboBox / CheckBox / Button…）
            Theme.ApplyTree(sec);
            // 卡片里那些纯容器（TableLayoutPanel / FlowLayoutPanel）在 ApplyTree 里会落到
            // default 分支拿到窗口底色，必须再压成卡片色，否则卡片上会浮出一块窗口底。
            ThemeOps.Flatten(sec, p.Surface);
            sec.Invalidate();
        }

        // 次要文字：状态与注记用 TextDim，主文字用 Text（ApplyTree 已刷）
        _asStatus.ForeColor = p.Text;
        _asDetail.ForeColor = p.TextDim;
        _ambientNote.ForeColor = _ambientNote.Text.StartsWith("自动定位失败")
                                 || _ambientNote.Text.StartsWith("定位异常")
            ? p.Hot
            : p.TextDim;
        _themeHint.ForeColor = p.TextDim;
        _cleanNote.ForeColor = p.TextDim;
        _maintenanceNote.ForeColor = p.TextDim;
        // 边界提示的色由"校验是否通过 / 首档是否被忽略"决定，ApplyTree 刷成 Text 后这里要按结论还原
        _boundsHint.ForeColor = !_boundsValid ? p.Hot : _firstBoundIgnored ? p.Warm : p.TextDim;
        _boundsSummary.ForeColor = p.TextDim;
#if PUSH_ENABLED
        // 推送区的状态行按语义色重取（Ok=凉 / Err=热 / Info=灰）
        _pushStatus.ForeColor = NoteColor(_pushStatusKind);
        _pushSlot2.ForeColor = NoteColor(_pushSlot2Kind);
        _pushSummary.ForeColor = p.TextDim;
#endif

        _asToggle.Invalidate();
        _relocate.Invalidate();
        _boundsToggle.Invalidate();
        _selfCheck.Invalidate();
        _rebind.Invalidate();
#if PUSH_ENABLED
        _pushExpand.Invalidate();
        _pushFetch.Invalidate();
        _pushKeyClear.Invalidate();
        _pushNow.Invalidate();
#endif
        Invalidate(true);
    }

    /// <summary>主题切换后由 <see cref="ThemedTabPage"/> 调回；本页只需整页重刷一次配色。</summary>
    protected override void ApplyTheme() => ApplyColors();

    /// <summary>切回本页时重读配置（窗口是"隐藏而非销毁"，配置可能被终端改过）。
    /// 打开窗口期间跳过；之后首次切到本页才读，已刷过的页切回来不重复取数。</summary>
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && !MainWindowHost.IsOpening) MainWindowHost.OnTabActivated(this);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutSections();
    }

    // ==================================================================
    // 排版小工具
    // ==================================================================

    /// <summary>分区内的纵向网格：给定若干固定行高，末尾补一行吃掉剩余空间。</summary>
    private static TableLayoutPanel Rows(params float[] heights)
    {
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = heights.Length + 1,
            Margin = new Padding(0),
            BackColor = Theme.Current.Surface
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        foreach (var h in heights) t.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        return t;
    }

    /// <summary>一行：左边按内容定宽，右边吃掉剩余空间。</summary>
    private static TableLayoutPanel TwoColumn(Control fixedPart, Control fillPart)
    {
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
            BackColor = Theme.Current.Surface
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        t.Controls.Add(fixedPart, 0, 0);
        t.Controls.Add(fillPart, 1, 0);
        return t;
    }

    private static FlowLayoutPanel FlowRow(params Control[] items)
    {
        var f = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
            BackColor = Theme.Current.Surface
        };
        foreach (var c in items) f.Controls.Add(c);
        return f;
    }

    private static Label TextLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(0, 8, 6, 0)
    };

    // ==================================================================
    // 分区容器
    // ==================================================================

    /// <summary>注记行的语义色：信息（次要灰）/ 成功（凉绿）/ 失败（热红）。切主题时按它重取色。</summary>
    private enum NoteKind { Info, Ok, Err }

    /// <summary>
    /// 设置页的分区卡：圆角面 + 左上标题。
    ///
    /// 【为什么不直接用 CardPanel】CardPanel 把标题画在 <c>Padding.Top</c> 处，
    /// 而停靠的子控件也从 <c>Padding.Top</c> 开始——两者会重叠。
    /// 这里把标题画在 y=9、内容区从 y=32 起，省掉那笔糊涂账。
    ///
    /// 派生自 <see cref="BufferedPanel"/>：自动双缓冲、自动跟随主题重绘。
    /// </summary>
    private sealed class SettingsSection : BufferedPanel
    {
        private readonly string _title;

        public SettingsSection(string title)
        {
            _title = title;
            Padding = new Padding(14, 32, 14, 12);
        }

        public override Color ContentBackColor => Theme.Current.Surface;

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            var s = DpiScale;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            var r = new RectangleF(0.5f * s, 0.5f * s, Width - 1f * s, Height - 1f * s);
            Gfx.FillRound(g, p.Surface, r, 8f * s);
            Gfx.DrawRound(g, p.Border, 1f * s, r, 8f * s);

            using var font = new Font(Font, FontStyle.Bold);
            using var brush = new SolidBrush(p.Text);
            g.DrawString(_title, font, brush, new PointF(Padding.Left, 9f * s));
        }
    }
}
