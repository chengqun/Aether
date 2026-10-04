using System.Text.Json;
using Acme.Ctp.Domain;

namespace Acme.Ctp.Sim;

/// <summary>
/// 本地仿真柜台 RPC 线协议的共享常量与信封类型——客户端（<see cref="SimRpcClient"/>，宿主进程内的薄会话用）
/// 与服务端（Acme.Ctp.Sim.Server 的 SimRpcServer）共用。**只引 <c>Acme.Ctp.Domain</c>**：
/// 本项目会进客户端进程（Infrastructure 引用它拿走客户端半边），绝不能把仿真核心带过去。
/// <para>
/// 帧格式见 <see cref="SimFraming"/>（4 字节小端长度前缀 + UTF-8 JSON）。
/// 三类帧：请求 <see cref="SimRequest"/>（客户端→服务端）、应答 <see cref="SimResponse"/>、推送 <see cref="SimPush"/>
/// （后两类服务端→客户端）。Hello 在连接建立后立即握手（协议版本协商位）；
/// 行情随柜台——sim 连接的 tick 恒按订阅集推送。
/// </para>
/// </summary>
public static class SimEndpointDefaults
{
    public const string Host = "127.0.0.1";
    public const int Port = 17800;
}

/// <summary>
/// 传输层专用错误码（客户端进程据此重建 <see cref="CtpBusinessException"/>）。
/// 刻意与仿真核心的 <c>SimErrorCodes</c>（90001-90011，Acme.Ctp.Simulator）分段：
/// 客户端进程不再引用 Simulator，核心错误码经 wire 原样透传（err.code），传输错误用本段。
/// </summary>
public static class SimWireErrors
{
    /// <summary>仿真柜台 host 未启动（连接被拒）。</summary>
    public const int NotRunning = 90012;

    /// <summary>连接已断开（host 死亡/超时后销毁）。</summary>
    public const int ConnectionLost = 90013;

    /// <summary>等待应答超时（连接已重置）。</summary>
    public const int Timeout = 90014;

    /// <summary>协议错误（未知操作/坏帧/内部错误）。</summary>
    public const int Protocol = 90015;
}

/// <summary>操作名（请求帧的 op 字段）。</summary>
public static class SimOps
{
    public const string Hello = "hello";
    public const string Login = "login";
    public const string Logout = "logout";
    public const string QueryAccount = "queryAccount";
    public const string QueryPositions = "queryPositions";
    public const string QueryOrders = "queryOrders";
    /// <summary>查询 host 上市合约表（返回 <see cref="CtpInstrument"/> 列表——行情板的「我就是交易所」数据源）。</summary>
    public const string QueryInstruments = "queryInstruments";
    public const string InsertOrder = "insertOrder";
    public const string CancelOrder = "cancelOrder";
    public const string Subscribe = "subscribe";
    public const string Unsubscribe = "unsubscribe";
}

/// <summary>推送事件名（推送帧的 ev 字段）。</summary>
public static class SimEvents
{
    public const string Order = "order";
    public const string Trade = "trade";
    public const string Tick = "tick";
}

/// <summary>
/// 解析 <c>AETHER_CTP_SIM_ADDR</c>（<c>[host:]port</c>，如 <c>17800</c> / <c>127.0.0.1:17800</c> / <c>localhost:19000</c>）。
/// 客户端（Infrastructure DI）与服务端（SimHostOptions）共用同一份格式与默认值——两端各配各的环境变量，格式只此一处。
/// </summary>
public static class SimAddress
{
    public const string EnvVar = "AETHER_CTP_SIM_ADDR";

    /// <summary>空/空白 → 默认端点；垃圾值抛 <see cref="FormatException"/>（启动即失败，好过静默连不上）。</summary>
    public static (string Host, int Port) Parse(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return (SimEndpointDefaults.Host, SimEndpointDefaults.Port);

        var trimmed = address.Trim();
        string hostPart, portPart;
        var colon = trimmed.LastIndexOf(':');
        if (colon > 0)
        {
            hostPart = trimmed[..colon];
            portPart = trimmed[(colon + 1)..];
        }
        else
        {
            hostPart = SimEndpointDefaults.Host;
            portPart = trimmed;
        }

        if (hostPart.Length == 0
            || !int.TryParse(portPart, out var port) || port is < 1 or > 65535)
            throw new FormatException(
                $"{EnvVar} 格式非法：\"{address}\"（应为 [host:]port，如 17800 或 127.0.0.1:17800）");

        return (hostPart, port);
    }

    /// <summary>
    /// 前置地址是否指向本地仿真柜台——「用地址选柜台」的路由判据（连真柜台还是连 sim，
    /// 由登录窗口填的前置地址决定，不经环境变量/重启）：
    /// <list type="bullet">
    /// <item><c>sim</c>（大小写不敏感、容忍空白）——保留字，永不与真实前置冲突；</item>
    /// <item><c>127.0.0.1:17800</c> / <c>tcp://127.0.0.1:17800</c>——与默认仿真端点等价的直写。</item>
    /// </list>
    /// 其余一律视为真实前置（含空——空地址本来就过不了登录校验）。
    /// </summary>
    public static bool IsSimFront(string? frontAddress)
    {
        var a = frontAddress?.Trim();
        if (string.IsNullOrWhiteSpace(a)) return false;
        if (a.Equals("sim", StringComparison.OrdinalIgnoreCase)) return true;
        var bare = a.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase) ? a[6..] : a;
        return bare.Equals($"{SimEndpointDefaults.Host}:{SimEndpointDefaults.Port}", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Hello 握手结果（协议版本；客户端不校验，仅诊断用）。</summary>
public sealed record SimHello(int ProtocolVersion);

/// <summary>错误应答载荷（服务端 catch 到的 <see cref="CtpBusinessException"/> 或传输错误）。</summary>
public sealed record SimWireError(int Code, string Msg);

/// <summary>登录参数。刻意只带 brokerId/userId——仿真核心不校验凭据，密码不出客户端进程。</summary>
/// <summary>登录参数（字段对齐真实 CTP 客户端登录的核心三件：经纪商/投资者/密码——AppId/AuthCode 仍留客户端侧不消费）。</summary>
public sealed record LoginArg(string BrokerId, string UserId, string? Password = null);

/// <summary>查询参数（可选按合约过滤）。</summary>
public sealed record InstrumentArg(string? InstrumentId);

/// <summary>行情订阅参数。</summary>
public sealed record SubscribeArg(IReadOnlyList<string> Symbols);

/// <summary>请求帧（客户端→服务端）。Arg 为按运行时类型序列化的 JSON 元素。</summary>
public sealed record SimRequest(long Id, string Op, JsonElement? Arg);

/// <summary>应答帧（服务端→客户端）。Ok=false 时 Err 必非空；Ok=true 时 Result 可空（void 操作）。</summary>
public sealed record SimResponse(long Id, bool Ok, JsonElement? Result, SimWireError? Err);

/// <summary>推送帧（服务端→客户端，无 id 不回执）。</summary>
public sealed record SimPush(string Ev, JsonElement Arg);

/// <summary>
/// 读取端统一信封：应答与推送共用一个反序列化目标，靠 <c>Id</c>（应答）/<c>Ev</c>（推送）判别。
/// 写入端用具体类型（<see cref="SimResponse"/>/<see cref="SimPush"/>），读取端用本类型。
/// （public：反射式 System.Text.Json 对 non-public record 的构造绑定不可靠。）
/// </summary>
public sealed record SimFrame(
    long? Id = null,
    bool? Ok = null,
    JsonElement? Result = null,
    SimWireError? Err = null,
    string? Ev = null,
    JsonElement? Arg = null);

public static class SimJson
{
    public static readonly JsonSerializerOptions Options = new();
}
