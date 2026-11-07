namespace ThermalTrace.Model;

/// <summary>
/// 当前时间片的工作快照：运行期临时文件，片落盘成功后立即删除。
/// 不是数据文件，不进 CSV 解析、不进趋势分析、不进任何导出。
/// 保留温度列表是为了重启后仍能算精确 P95，见 <see cref="SliceAggregate"/> 的类注释。
/// </summary>
public sealed class SliceSnapshot
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public int SliceMinutes { get; set; }
    public string SliceStart { get; set; } = "";
    public List<SliceAggregateState> Aggregates { get; set; } = new();
}

/// <summary>单个 (负载档, 供电) 聚合器的完整累加器状态，可无损还原成 SliceAggregate。</summary>
public sealed class SliceAggregateState
{
    public string SliceStart { get; set; } = "";
    public int Band { get; set; }
    public bool OnAcPower { get; set; }

    public int Count { get; set; }
    public double TempSum { get; set; }
    public double PowerSum { get; set; }
    public double CoreMaxSum { get; set; }
    public double UtilSum { get; set; }
    public double GpuTempSum { get; set; }
    public int GpuCount { get; set; }
    public double[] GpuBucketTempSum { get; set; } = new double[SliceAggregate.GpuBucketCount];
    public long[] GpuBucketN { get; set; } = new long[SliceAggregate.GpuBucketCount];
    public double GpuUtilSum { get; set; }
    public long GpuUtilN { get; set; }

    public double TempMin { get; set; } = double.NaN;
    public double TempMax { get; set; } = double.NaN;
    public double TjmaxMin { get; set; } = double.NaN;
    public int Over85Count { get; set; }
    public int Over90Count { get; set; }

    /// <summary>片内原始温度值，用于精确 P95。最多约 360 个，几 KB。</summary>
    public List<double> Temps { get; set; } = new();
}
