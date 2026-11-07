using System.Security.Principal;

namespace ThermalTrace.Core;

/// <summary>
/// 当前进程是否已提权。
///
/// 【为什么必须有它】
/// 读 MSR 需要管理员权限（app.manifest 声明 requireAdministrator），
/// 而创建"最高权限"计划任务同样需要提权。旧代码的教训是：
/// 注册动作写成功了却永远不会生效（HKCU Run 与提权互斥），
/// 所以凡是"注册自启"的入口都必须先确认自己真的提权了，否则明确报错。
/// </summary>
internal static class Elevation
{
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                // 域环境/受限令牌下可能抛异常，按未提权处理（宁可报错也不要假成功）
                return false;
            }
        }
    }
}
