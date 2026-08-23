using Acme.Desktop.Mvvm;
using Acme.Desktop.Shared.Shell;
using Aether.AI.Core.Agents;

namespace Acme.Desktop.Shared.Hosting;

/// <summary>
/// 运行关键节点信号（<see cref="RunProgress"/>）+ 桌宠逐字流（<see cref="IPetTokenStream"/>）的可换目标路由器。
/// 桌宠（<see cref="Shell.PetWindow"/>）是懒建的——首次进入桌宠形态（X 关闭/最小化）才实例化，而
/// <see cref="ChatWorkspaceViewModel"/> 在容器构建时即解析 sink。故注册常驻路由器单例（同时实现两个接口，
/// 指向同一活的 PetWindow），桌宠建好后 WorkbenchWindow 调 <see cref="SetTarget"/>；未建宠物时 Post/AppendToken
/// 静默丢弃（聊天行为不变）。桌宠关闭时清空回 null，聊天流回到无通知状态。
/// 关键节点信号（IRunProgressSink）与手机端（onRemoteProgress）同源、零依赖重复；逐字流（IPetTokenStream）是桌宠专属。
/// </summary>
public sealed class PetStatusFeedRouter : IRunProgressSink, IPetTokenStream
{
    private IRunProgressSink? _sink;
    private IPetTokenStream? _tokens;

    /// <summary>把路由器指向活的桌宠（建好/重建时）或清空（桌宠关闭时）。PetWindow 同时实现两接口。</summary>
    public void SetTarget(PetWindow? target)
    {
        _sink = target;
        _tokens = target;
    }

    public void Post(RunProgress signal) => _sink?.Post(signal);

    public void AppendToken(string token) => _tokens?.AppendToken(token);

    public void EndAnswer() => _tokens?.EndAnswer();
}
