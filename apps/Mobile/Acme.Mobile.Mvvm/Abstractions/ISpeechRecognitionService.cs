using System;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.Mobile.Mvvm.Abstractions;

/// <summary>
/// 语音识别服务：将语音转为文字
/// </summary>
public interface ISpeechRecognitionService
{
    /// <summary>
    /// 当前是否正在监听语音
    /// </summary>
    bool IsListening { get; }

    /// <summary>
    /// 开始语音识别，返回最终识别文本。
    /// onPartialResult 回调实时返回中间识别结果。
    /// 通过 CancellationToken 取消可停止识别。
    /// </summary>
    Task<string?> ListenAsync(Action<string>? onPartialResult = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 停止当前识别
    /// </summary>
    void StopListening();

    /// <summary>
    /// 请求麦克风权限，返回是否已授权
    /// </summary>
    Task<bool> RequestPermissionAsync();
}
