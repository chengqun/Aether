using System;
using System.Threading.Tasks;
using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Mvvm.Abstractions;

/// <summary>
/// UI 线程调度抽象
/// MAUI: MainThread.BeginInvokeOnMainThread
/// Avalonia: Dispatcher.UIThread.Post
/// </summary>
public interface IUIThreadDispatcher
{
    void Post(Action action);
}