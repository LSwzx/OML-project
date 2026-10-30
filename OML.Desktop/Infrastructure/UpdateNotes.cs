namespace OML.Desktop.Infrastructure;

/// <summary>内置更新日志（Velopack 未提供 Notes 时使用）。</summary>
public static class UpdateNotes
{
    public const string Latest = """
        OML 1.2.1 合规更新（本次为必须更新）

        • 仅保留微软正版登录
          离线账号与外置登录（LittleSkin / Yggdrasil）已按合规要求从程序中物理移除。

        • 全新的微软登录界面
          点击「微软正版登录」后，启动器会显示设备代码与验证地址；
          复制代码、在浏览器中完成授权即可，无需手动填写密码。

        • 旧账号处理
          如果配置中仍是离线账号，启动器会提示「不支持此账号类型」，
          请使用微软正版账号重新登录后再启动游戏。

        • 启动前校验
          启动 Java 版与基岩版前都会校验微软账号与基岩版授权。

        更新完成后启动器会自动重启。
        """;
}
