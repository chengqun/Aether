using System;

namespace Acme.Desktop.Mvvm;

/// <summary>轻量右下角 toast 提示类型（驱动气泡配色/图标）。</summary>
public enum ToastKind
{
    /// <summary>中性信息（默认灰/主色）。</summary>
    Info,
    /// <summary>成功（绿）。</summary>
    Success,
    /// <summary>错误/警告（红）。</summary>
    Error,
}

/// <summary>
/// 轻量右下角 toast 提示 API（接口在 Mvvm，UI 无关；impl 在 Shared）。不占布局、自动消失——
/// 用于替代常驻状态条（如知识对象页顶部状态条）里的错误/保存反馈，视觉对齐 VSCode 式极简界面。
/// 镜像 <see cref="IAskUserDialogService"/> 的「接口 Mvvm / impl Shared」分层范式。
/// </summary>
public interface IToastService
{
    /// <summary>在右下角弹一条自动消失的提示。null/空白消息忽略（静默）。</summary>
    void Show(string? message, ToastKind kind = ToastKind.Info);
}
