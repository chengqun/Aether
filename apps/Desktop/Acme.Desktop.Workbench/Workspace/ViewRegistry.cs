using System;
using System.Collections.Generic;
using Acme.Desktop.Workbench.Abstractions;
using Avalonia.Controls;

namespace Acme.Desktop.Workbench.Workspace;

/// <summary>IViewRegistry 默认实现：view-key → 工厂字典。</summary>
public class ViewRegistry : IViewRegistry
{
    private readonly Dictionary<string, (Func<Control> View, Func<object?>? Vm)> _entries = new();

    public void Register(string key, Func<Control> viewFactory, Func<object?>? vmFactory = null)
        => _entries[key] = (viewFactory, vmFactory);

    public Control? CreateView(string key)
        => _entries.TryGetValue(key, out var e) ? e.View() : null;

    public object? CreateViewModel(string key)
        => _entries.TryGetValue(key, out var e) ? e.Vm?.Invoke() : null;
}
