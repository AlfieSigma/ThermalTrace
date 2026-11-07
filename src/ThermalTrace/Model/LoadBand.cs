namespace ThermalTrace.Model;

/// <summary>
/// 负载档位：<b>固定 10% 细粒度</b>，共 10 档。
///
/// 【设计决策】为什么存细粒度、展示时才合并：
/// 清灰当天并不清楚自己的实际使用习惯，无法预先确定"轻度/中等/重度"的分界点。
/// 若存储层就用粗档位，三个月后想调整分界就得重新采集数据。
/// 因此存储层永远保持 10% 粒度，展示层再按可配置边界合并成友好类别——
/// 改边界只需改配置，历史数据全部复用。
///
/// 【为什么按"单核峰值"而不是"总利用率"分档】
/// 本方案面向 4 核 8 线程的 Intel CPU。实测静置时总利用率 13% 但单核峰值 47%——
/// 总利用率会严重低估实际发热。产生热量的是那个跑满的核心，
/// 所以分档必须看单核峰值。
/// </summary>
public enum LoadBand
{
    B00_10 = 0,
    B10_20 = 1,
    B20_30 = 2,
    B30_40 = 3,
    B40_50 = 4,
    B50_60 = 5,
    B60_70 = 6,
    B70_80 = 7,
    B80_90 = 8,
    B90_100 = 9
}

public static class LoadBandExtensions
{
    public const int Count = 10;

    /// <summary>按单核峰值利用率（%）归档。NaN 归入最低档。</summary>
    public static LoadBand FromMaxCoreUtil(double maxCoreUtilPct)
    {
        if (double.IsNaN(maxCoreUtilPct)) return LoadBand.B00_10;
        var idx = (int)(maxCoreUtilPct / 10);
        if (idx < 0) idx = 0;
        if (idx >= Count) idx = Count - 1;
        return (LoadBand)idx;
    }

    /// <summary>该档下界（%），闭区间。</summary>
    public static int Lower(this LoadBand b) => (int)b * 10;

    /// <summary>该档上界（%），开区间。</summary>
    public static int Upper(this LoadBand b) => ((int)b + 1) * 10;

    /// <summary>CSV 中使用的稳定键，如 "70-80"。</summary>
    public static string Key(this LoadBand b) => $"{b.Lower()}-{b.Upper()}";

    public static string Label(this LoadBand b) => $"{b.Key()}%";
}