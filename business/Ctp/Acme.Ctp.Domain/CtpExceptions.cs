using System;

namespace Acme.Ctp.Domain;

/// <summary>CTP 异常基类（领域层，不依赖 SDK）。</summary>
public class CtpException : Exception
{
    public CtpException(string message) : base(message) { }
}

/// <summary>CTP 业务错误（含错误码 + 错误信息），由 Infrastructure 在 ICtpSession 边界从 SDK 异常包装而来。</summary>
public class CtpBusinessException : CtpException
{
    public int ErrorId { get; }
    public string ErrorMsg { get; }

    public CtpBusinessException(int errorId, string errorMsg)
        : base($"CTP 业务错误 [{errorId}]: {errorMsg}")
    {
        ErrorId = errorId;
        ErrorMsg = errorMsg;
    }
}
