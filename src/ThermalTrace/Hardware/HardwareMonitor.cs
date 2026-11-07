using System.Runtime.Versioning;
using LibreHardwareMonitor.Hardware;
using ThermalTrace.Model;

namespace ThermalTrace.Hardware;

/// <summary>
/// 对 LibreHardwareMonitorLib 的唯一封装。整个程序只有这一处直接接触第三方库。
///
/// 关键约束：
///  1. 必须管理员权限。LHM 靠 WinRing0 驱动读 Intel MSR；普通权限下所有值为空，
///     部分机型会直接抛异常。错误信息必须能引导用户，而不是抛原始驱动异常。
///  2. <b>Computer.Open() 之后不会自动刷新</b>，必须每拍显式调用 <see cref="Update"/>
///     （内部走 Accept(new HardwareUpdateVisitor())）。这是把 LHM 嵌入自有程序时
///     最常见的错误来源。
///  3. CPU 温度取 MSR 口径（Package / Tdie），不要用 WMI 的 MSAcpi_ThermalZoneTemperature
///     ——后者在笔记本上经常缺失，或与 LiteMonitor 差 3~8℃，会让整个基线失效。
///  4. 传感器 Identifier 会随驱动版本变化，不能硬编码：启动时全量建索引，
///     并在硬件数量变化时（GPU 热插拔、休眠唤醒后设备重枚举）自动重建。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class HardwareMonitor : IDisposable
{
    private Computer? _computer;
    private readonly Dictionary<string, ISensor> _sensors = new(StringComparer.OrdinalIgnoreCase);
    private bool _opened;
    private int _indexedHardwareCount = -1;

    public bool IsOpen => _opened;

    public void Open()
    {
        if (_opened) return;

        // 【2026-10-13 收窄】见非 CPU 信号的取舍说明。
        // 关掉的这三类，实测环境中本来就没有在用：
        //  - 主板与风扇：settings.json 里 fanId 与 mainboardTempId 都是空串，
        //    displayNames 里没有任何 /lpc /superio /mainboard 条目 —— 硬件压根枚举不出来；
        //  - 内存：绑定表里没有任何内存传感器；
        //  - 电池：供电状态走 kernel32 GetSystemPowerStatus（SystemProbe.ReadPowerState），
        //    不经 LHM，CSV 的 ac 列与这里无关。
        // 存储仍然开着，但每 5 秒的 Update 会跳过它（见 Update 的注释）——
        // 留着是为了每天读一次 SSD 寿命计数器，那些传感器挂在存储硬件下。
        // 控制器保持开启是保守选择：别的机型上存储/显卡可能挂在控制器下，关了有误伤风险。
        var computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsControllerEnabled = true,
            IsStorageEnabled = true,
            IsMemoryEnabled = false,
            IsMotherboardEnabled = false,
            IsBatteryEnabled = false,
            IsNetworkEnabled = false
        };

        try
        {
            computer.Open();
        }
        catch (Exception ex)
        {
            try { computer.Close(); } catch { /* noop */ }
            throw new InvalidOperationException(
                "打开硬件监控失败。读取 CPU MSR 需要管理员权限，"
                + "且安全软件可能拦截 WinRing0 驱动（360/火绒等）。"
                + $"请以『管理员身份』重新运行，并检查安全软件拦截记录。原始错误: {ex.Message}", ex);
        }

        _computer = computer;
        _opened = true;
        BuildIndex();

        // 立刻刷新一次并验证温度可读——这是最早暴露权限/驱动问题的时机
        Update();
        if (!HasAnyTemperature())
        {
            Close();
            throw new InvalidOperationException(
                "已打开硬件监控，但读不到任何温度传感器。通常原因: "
                + "(1) 未以管理员身份运行；(2) WinRing0 驱动被安全软件拦截。"
                + "请先确认能以管理员身份启动，再检查安全软件拦截记录。");
        }
    }

    /// <summary>
    /// 显式触发一次硬件刷新。采样循环每拍调用一次。
    ///
    /// 【实测经验】LHM 0.9.x 不再有 Computer.Update()，也没有旧的 HardwareUpdateVisitor；
    /// 刷新入口是 IHardware.Update()，且必须<b>递归</b>调用到子硬件
    /// （SSD挂在 SATA/NVMe 控制器下、GPU 挂在 PCIe 控制器下，不递归会读不到值）。
    /// Computer.Open() 之后不会自动刷新，必须每拍显式调用——
    /// 这是把 LHM 嵌入自有程序时最常见的错误来源。
    /// </summary>
    /// <summary>
    /// 常规刷新（采样循环每 5 秒一次）。<b>跳过存储硬件</b>。
    ///
    /// 【为什么要跳过】每 5 秒向 NVMe 取一次 SMART 健康日志是这棵树里最贵的一笔开销，
    /// 而 SSD 温度对分析毫无用处（主因是磁盘 I/O，与 CPU 负载档不是一个驱动量）。
    /// 存储硬件仍然保持枚举，只是不参与常规刷新；要读寿命计数器时走
    /// <see cref="UpdateStorage"/>，一天一次。见非 CPU 信号说明。
    /// </summary>
    public void Update()
    {
        if (_computer is null) throw new InvalidOperationException("尚未调用 Open()");
        foreach (var hw in _computer.Hardware)
        {
            if (IsStorage(hw)) continue;
            UpdateRecursive(hw);
        }
        EnsureIndexFresh();
    }

    /// <summary>
    /// 只刷新存储硬件。每天读 SSD 寿命计数器前调一次。
    /// 常规刷新跳过了它，所以这里必须单独补一次，否则读到的是启动时那个陈旧值。
    /// </summary>
    public void UpdateStorage()
    {
        if (_computer is null) throw new InvalidOperationException("尚未调用 Open()");
        foreach (var hw in _computer.Hardware)
            if (IsStorage(hw))
                UpdateRecursive(hw);
    }

    /// <summary>
    /// 是否存储设备。
    ///
    /// 【为什么按 Identifier 前缀判断，不用 HardwareType】
    /// 实测 Identifier 是 <c>/nvme/0/temperature/0</c>，而 HardwareType 的枚举名
    /// 在不同 LHM 版本里可能是 Storage 也可能是 Nvme，靠枚举名判断等于在猜；
    /// Identifier 前缀是能直接观测到的事实（settings.json 里就写着）。
    /// 两条判据取"或"，换个版本也不至于整块漏掉。
    /// </summary>
    private static bool IsStorage(IHardware hw)
    {
        var id = hw.Identifier.ToString();
        return IsStorageId(id)
               || hw.HardwareType.ToString().StartsWith("Storage", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStorageId(string id) =>
        id.StartsWith("/nvme/", StringComparison.OrdinalIgnoreCase)
        || id.StartsWith("/hdd/", StringComparison.OrdinalIgnoreCase)
        || id.StartsWith("/ssd/", StringComparison.OrdinalIgnoreCase)
        || id.StartsWith("/storage/", StringComparison.OrdinalIgnoreCase);

    private static void UpdateRecursive(IHardware hardware)
    {
        hardware.Update();
        foreach (var sub in hardware.SubHardware)
            UpdateRecursive(sub);
    }

    private bool HasAnyTemperature()
    {
        foreach (var s in _sensors.Values)
            if (s.SensorType == SensorType.Temperature && s.Value is not null)
                return true;
        return false;
    }

    // ---------------- 索引管理 ----------------

    private void BuildIndex()
    {
        _sensors.Clear();
        if (_computer is null) return;

        foreach (var hw in _computer.Hardware)
        {
            foreach (var sensor in hw.Sensors)
            {
                var id = sensor.Identifier.ToString();
                if (!string.IsNullOrEmpty(id))
                    _sensors[id] = sensor;
            }
        }
        _indexedHardwareCount = _computer.Hardware.Count;
    }

    /// <summary>硬件拓扑变化时自动重建索引（GPU 热插拔、插拔扩展坞、休眠唤醒后设备重枚举）。</summary>
    private void EnsureIndexFresh()
    {
        if (_computer is null) return;
        if (_computer.Hardware.Count != _indexedHardwareCount)
            BuildIndex();
    }

    // ---------------- 读取 ----------------

    /// <summary>
    /// 按 Identifier 读取单个传感器。缺失 / NaN / 无穷一律返回 null——
    /// 调用方必须自己决定填什么，绝不能用 0 代替缺失（会污染统计均值）。
    /// </summary>
    public double? ReadById(string? identifier)
    {
        if (string.IsNullOrEmpty(identifier)) return null;
        if (!_sensors.TryGetValue(identifier, out var sensor)) return null;

        var v = sensor.Value;
        if (v is null || float.IsNaN(v.Value) || float.IsInfinity(v.Value)) return null;
        return v.Value;
    }

    private static double OrNa(double? v) => v ?? double.NaN;

    /// <summary>按绑定表组装一条采样点。</summary>
    public Sample ReadSample(SensorBinding binding, int elapsedSeconds) => new()
    {
        Timestamp = DateTimeOffset.Now,
        ElapsedSeconds = elapsedSeconds,

        CpuPackageC = OrNa(ReadById(binding.CpuPackageId)),
        CpuTdieC = OrNa(ReadById(binding.CpuTdieId)),
        CpuCoreMaxC = OrNa(ReadById(binding.CpuCoreMaxId)),
        CpuCoreAvgC = OrNa(ReadById(binding.CpuCoreAvgId)),
        TjMaxDistanceC = OrNa(ReadById(binding.CpuDistanceToTjMaxId)),

        GpuC = OrNa(ReadById(binding.GpuTempId)),
        GpuC2 = OrNa(ReadById(binding.Gpu2TempId)),
        // GPU 负载（2026-10-16 口径修正）：使用时长从此按"负载 > 0"算，
        // 不再按"采到温度即算使用"——后者等于开机时长。
        // 绑定早在 settings.json 里就有（gpuUtilId / gpu2UtilId），只是以前不读。
        GpuLoadPct = OrNa(ReadById(binding.GpuUtilId)),
        Gpu2LoadPct = OrNa(ReadById(binding.Gpu2UtilId)),
        SsdC = OrNa(ReadById(binding.SsdTempId)),
        MainboardC = OrNa(ReadById(binding.MainboardTempId)),

        FanRpm = OrNa(ReadById(binding.FanId)),
        PackagePowerW = OrNa(ReadById(binding.CpuPackagePowerId)),
        ClampW = OrNa(ReadById(binding.CpuClampId)),
        CpuClockMhz = OrNa(ReadById(binding.CpuClockId)),
        UtilPct = OrNa(ReadById(binding.CpuUtilId)),
        MaxCoreUtilPct = OrNa(ReadById(binding.CpuCoreMaxUtilId))
    };

    /// <summary>
    /// 读一次 SSD 寿命计数器。
    ///
    /// 【为什么按传感器名查找，而不是写进 SensorBinding】
    /// 这几个都是累计量，不参与口径哈希（FingerprintStore 只哈希 CPU 型号、主温度链路、
    /// 功耗、单核峰值、风扇五样），按名查找既不需要用户重新绑定，
    /// Identifier 随驱动版本漂移时也照样能找到。找不到就返回全 NaN，
    /// 由调用方决定记不记 —— 绝不因为缺一项而抛异常。
    ///
    /// 【调用前必须先 <see cref="UpdateStorage"/>】
    /// 常规刷新跳过了存储硬件，不单独刷一次读到的会是启动时那个陈旧值。
    /// </summary>
    public DriveHealth ReadDriveHealth(DateOnly day)
    {
        if (_computer is null) throw new InvalidOperationException("尚未调用 Open()");

        var byName = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var hw in _computer.Hardware)
            if (IsStorage(hw))
                CollectStorageValues(hw, byName);

        double Get(string name) => byName.TryGetValue(name, out var v) ? v : double.NaN;

        return new DriveHealth(
            Date: day,
            PercentageUsed: Get("Percentage Used"),
            AvailableSpare: Get("Available Spare"),
            DataWrittenGb: Get("Data Written"),
            PowerOnHours: Get("Power On Hours"),
            Life: Get("Life"));
    }

    private static void CollectStorageValues(IHardware hw, Dictionary<string, double> into)
    {
        foreach (var s in hw.Sensors)
        {
            var name = s.Name?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            if (s.Value is not { } v || float.IsNaN(v) || float.IsInfinity(v)) continue;
            into.TryAdd(name, v);   // 同名只取第一个：多块盘时不会被后一块盖掉
        }
        foreach (var sub in hw.SubHardware)
            CollectStorageValues(sub, into);
    }

    // ---------------- 枚举 ----------------

    /// <summary>枚举全部可见传感器，供 --list-sensors / --doctor / 绑定向导使用。</summary>
    public IReadOnlyList<SensorDescriptor> EnumerateSensors()
    {
        if (_computer is null) throw new InvalidOperationException("尚未调用 Open()");
        EnsureIndexFresh();

        var list = new List<SensorDescriptor>();
        foreach (var hw in _computer.Hardware)
        {
            foreach (var s in hw.Sensors)
            {
                list.Add(new SensorDescriptor(
                    Identifier: s.Identifier.ToString(),
                    Name: s.Name,
                    HardwareName: hw.Name,
                    HardwareType: hw.HardwareType.ToString(),
                    SensorType: s.SensorType.ToString(),
                    Unit: UnitOf(s.SensorType),
                    ValuePreview: s.Value is { } v ? v.ToString("0.##") : "—"));
            }
        }
        return list;
    }

    internal static string UnitOf(SensorType type) => type switch
    {
        SensorType.Temperature => "°C",
        SensorType.Fan => "RPM",
        SensorType.Power => "W",
        SensorType.Clock => "MHz",
        SensorType.Load => "%",
        SensorType.Voltage => "V",
        SensorType.Level => "%",
        SensorType.Factor => "1",
        _ => ""
    };

    // ---------------- 硬件指纹 ----------------

    public HardwareProbeInfo Probe() => SystemProbe.Probe(_computer);

    public void Close()
    {
        if (!_opened) return;
        _opened = false;
        _sensors.Clear();
        _indexedHardwareCount = -1;
        try
        {
            _computer?.Close();
        }
        catch
        {
            // 驱动释放失败不应影响主流程
        }
        finally
        {
            _computer = null;
        }
    }

    public void Dispose()
    {
        Close();
        GC.SuppressFinalize(this);
    }
}

/// <summary>硬件环境指纹。跨会话对比前必须先比对它。</summary>
internal sealed record HardwareProbeInfo(
    string CpuName,
    int LogicalCores,
    int PhysicalCores,
    string BiosVersion,
    string GpuList,
    int TotalSensorCount,
    bool CanReadMsr)
{
    /// <summary>与已有会话指纹比对，返回人类可读的差异描述。</summary>
    public IReadOnlyList<string> Diff(HardwareProbeInfo other)
    {
        var diffs = new List<string>();
        if (!string.Equals(CpuName, other.CpuName, StringComparison.OrdinalIgnoreCase))
            diffs.Add($"CPU 型号变化: {other.CpuName} -> {CpuName}");
        if (PhysicalCores != other.PhysicalCores)
            diffs.Add($"物理核数变化: {other.PhysicalCores} -> {PhysicalCores}");
        if (!string.Equals(BiosVersion, other.BiosVersion, StringComparison.OrdinalIgnoreCase))
            diffs.Add($"BIOS 版本变化: {other.BiosVersion} -> {BiosVersion}"
                      + "（可能改变 PL1/PL2 与风扇策略，跨会话数据需谨慎对比）");
        if (!string.Equals(GpuList, other.GpuList, StringComparison.OrdinalIgnoreCase))
            diffs.Add($"GPU 组合变化: {other.GpuList} -> {GpuList}");
        return diffs;
    }

    public string ShortSignature => $"{CpuName}|{PhysicalCores}|{BiosVersion}|{GpuList}";
}