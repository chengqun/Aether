using System.Threading.Tasks;

namespace Acme.Mobile.Mvvm.Abstractions;

/// <summary>
/// 剪贴板服务抽象
/// MAUI: Clipboard.Default.SetTextAsync
/// Avalonia: TopLevel.Clipboard.SetTextAsync
/// </summary>
public interface IClipboardService
{
    Task SetTextAsync(string text);
}