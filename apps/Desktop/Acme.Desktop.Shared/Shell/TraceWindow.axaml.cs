using System;
using System.IO;
using Acme.Desktop.Mvvm;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Shared.Shell;

public partial class TraceWindow : Window
{
    private IThemeApplier? _themeApplier;

    public TraceWindow()
    {
        InitializeComponent();
        // 同 SettingsWindow——经 IThemeApplier 叠加 overlay，dark 下 Trace 窗跟随主题。
        _themeApplier = DesktopApp.Services.GetService<IThemeApplier>();
        if (_themeApplier is not null)
            _themeApplier.ThemeChanged += OnThemeChanged;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _themeApplier?.ApplyTheme(this);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => _themeApplier?.ApplyTheme(this);

    protected override void OnClosed(EventArgs e)
    {
        if (_themeApplier is not null)
            _themeApplier.ThemeChanged -= OnThemeChanged;
        base.OnClosed(e);
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TraceViewModel vm) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出 Trace JSON",
            DefaultExtension = "json",
            FileTypeChoices = new[] { new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } } },
        });
        if (file is not null)
        {
            await using var stream = await file.OpenWriteAsync();
            using var writer = new StreamWriter(stream);
            await writer.WriteAsync(vm.GetJson());
        }
    }
}
