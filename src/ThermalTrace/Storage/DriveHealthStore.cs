using System.Text;
using ThermalTrace.Core;
using ThermalTrace.Model;

namespace ThermalTrace.Storage;

/// <summary>
/// SSD 寿命计数器的读写（<c>health\ssd.csv</c>，一天一行）。
/// 见非 CPU 信号说明。
///
/// 【为什么单独一个目录而不是并进 daily\】
/// <c>daily\</c> 下的文件是"每 30 分钟 × 每负载档"的切片行，而
/// <see cref="DailyStore.ReadAll"/> 会枚举该目录里<b>所有</b> *.csv 按切片解析。
/// 寿命是"每天一行"的另一种粒度，混进去既污染解析又要到处写兼容分支；
/// 单独放 health\ 最省心，也便于将来再加别的累计量。
///
/// 【幂等】同一天只写一行：反复启动 / 重启都不会产生重复行。
/// </summary>
internal static class DriveHealthStore
{
    private static readonly string[] Fields =
        ["date", "percentage_used", "available_spare", "data_written_gb", "power_on_hours", "life"];

    private static readonly string Header = string.Join(',', Fields);

    public static string FilePath => Path.Combine(AppPaths.HealthDir, "ssd.csv");

    /// <summary>当天是否已有记录（每次调用都会读一次文件，调用方按天节流）。</summary>
    public static bool HasDay(DateOnly day)
    {
        try
        {
            foreach (var line in File.ReadLines(FilePath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("date,")) continue;
                if (DriveHealth.Parse(line) is { } row && row.Date == day) return true;
            }
        }
        catch
        {
            // 文件不存在 / 损坏：当作"还没有记录"，让调用方去写
        }
        return false;
    }

    /// <summary>写入一行；当天已有则跳过。返回是否真的写了。</summary>
    public static bool AppendOnce(DriveHealth row)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.HealthDir);
            if (HasDay(row.Date)) return false;

            var path = FilePath;
            var isNew = !File.Exists(path) || new FileInfo(path).Length == 0;

            var sb = new StringBuilder();
            if (isNew) sb.AppendLine(Header);
            sb.AppendLine(row.ToCsv());

            File.AppendAllText(path, sb.ToString(), new UTF8Encoding(true));   // BOM：Excel 开中文不乱码
            return true;
        }
        catch
        {
            // 记不下来绝不影响采样主流程
            return false;
        }
    }

    /// <summary>读取全部寿命记录，按日期升序。文件不存在 / 损坏时返回空列表。</summary>
    /// <remarks>
    /// 【为什么不加解析缓存】这个文件一天最多一行（<see cref="AppendOnce"/> 幂等），
    /// 一年也就 365 行——与 <see cref="DailyStore"/> 那种"每 10 秒全量解析 365 个文件"
    /// 完全不是一个量级，不值得为它引入缓存失效逻辑。
    /// </remarks>
    public static IReadOnlyList<DriveHealth> ReadAll()
    {
        var result = new List<DriveHealth>();
        try
        {
            if (!File.Exists(FilePath)) return Array.Empty<DriveHealth>();

            foreach (var line in File.ReadLines(FilePath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("date,")) continue;
                if (DriveHealth.Parse(line) is { } row) result.Add(row);
            }
        }
        catch
        {
            // 读不出来绝不影响调用方（GUI 刷新 / CLI 打印都还得继续）
            return Array.Empty<DriveHealth>();
        }

        result.Sort((a, b) => a.Date.CompareTo(b.Date));
        return result;
    }
}
