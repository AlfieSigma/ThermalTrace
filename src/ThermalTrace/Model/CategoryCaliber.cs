namespace ThermalTrace.Model;

/// <summary>
/// 负载档位区间的展示文案与校验。概览页头、设置页摘要与保存校验共用这一份，
/// 避免出现"摘要写空闲≥8、实际却从 0 起算"这类文字与归档不一致
/// （分类口径）。
///
/// 【首档固定从 0 起算】<see cref="AppSettings.CategoryOf"/> 对低于首档下限的样本也归入首档，
/// 所以首档的实际区间永远是 0 到下一档下限减 1。配置里的非 0 首档只作为提示，不改变归档。
/// </summary>
internal static class CategoryCaliber
{
    /// <summary>某一档实际生效的下限：首档固定 0，其余取配置值。</summary>
    public static int EffectiveLower(int[] bounds, int index) =>
        index <= 0 ? 0 : bounds[index];

    /// <summary>
    /// 区间文案：<c>空闲 0–7% · 轻度 8–34% · … · 满载 ≥92%</summary>
    /// 上界由下一档下限减 1 得到，末档用 <c>≥</c>。档位为空时返回空串。
    public static string Describe(int[] bounds, string[]? names)
    {
        if (bounds is null || bounds.Length == 0) return "";

        var parts = new string[bounds.Length];
        for (int i = 0; i < bounds.Length; i++)
            parts[i] = $"{Name(names, i)} {DescribeRange(bounds, i)}";
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// 单档区间：首档从 0 起，中间档上界为下一档下限减 1，末档用 <c>≥</c>；
    /// 例如 <c>0–7%</c>、<c>≥92%</c>。概览负载卡在档名旁用它（G5）。
    /// 越界或空配置返回空串，不猜替代值。
    /// </summary>
    public static string DescribeRange(int[] bounds, int index)
    {
        if (bounds is null || bounds.Length == 0 || index < 0 || index >= bounds.Length) return "";

        var lower = EffectiveLower(bounds, index);
        return index == bounds.Length - 1
            ? $"≥{lower}%"
            : $"{lower}–{bounds[index + 1] - 1}%";
    }

    /// <summary>
    /// 严格递增校验。合法返回 null；否则指出哪一档会永远为空：
    /// 相邻两档相等或倒序时，前档的区间（下限到后档下限减 1）没有可用取值，
    /// <see cref="AppSettings.CategoryOf"/> 也永远不会选中它。
    /// </summary>
    public static string? Validate(int[] bounds, string[]? names)
    {
        if (bounds is null) return null;

        for (int i = 1; i < bounds.Length; i++)
        {
            if (bounds[i] == bounds[i - 1])
                return $"「{Name(names, i - 1)}」这一档会永远为空：两档下限都是 {bounds[i]}%。";
            if (bounds[i] < bounds[i - 1])
                return $"「{Name(names, i - 1)}」这一档会永远为空：{bounds[i - 1]}% 与「{Name(names, i)}」的 "
                     + $"{bounds[i]}% 倒序。";
        }
        return null;
    }

    /// <summary>手改配置把首档下限写成非 0 时的提示；正常返回 null。</summary>
    public static string? FirstBoundIgnored(int[] bounds) =>
        bounds is { Length: > 0 } && bounds[0] != 0
            ? $"首档下限被忽略：配置为 {bounds[0]}%，运行时从 0 起算。"
            : null;

    public static string Name(string[]? names, int index) =>
        names is not null && index >= 0 && index < names.Length ? names[index] : "类别" + index;
}
