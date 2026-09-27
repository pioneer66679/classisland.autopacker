using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Extensions.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClassIsland.AutoPacker;

/// <summary>
/// 插件入口。
/// </summary>
public class Plugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        // 把插件配置目录传给配置界面（SettingsPageBase 拿不到 PluginConfigFolder）
        AutoPackerSettingsPage.SetConfigFolder(PluginConfigFolder);

        services.AddSettingsPage<AutoPackerSettingsPage>();
    }
}
