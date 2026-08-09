using System.Threading.Tasks;

namespace Acme.Mobile.Mvvm.Abstractions;

/// <summary>
/// 轻提示服务抽象（短暂通知，如"已复制"）
/// MAUI: CommunityToolkit.Maui.Alerts.Toast
/// Avalonia: 自定义叠加层通知
/// </summary>
public interface IToastService
{
    Task ShowAsync(string message, int durationMs = 1500);
}