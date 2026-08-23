using System.Globalization;
using Acme.Desktop.Mvvm;
using Avalonia.Data.Converters;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 判断当前选中文件树节点是否为文件夹（供右键菜单项 IsEnabled 绑定）。
/// null（未选中）→ false；FileNode → IsFolder；parameter="!" → 取反（文件时 true）。
/// 与 <see cref="NotNullConverter"/> 同构，但针对 <see cref="FileNode"/> 的 IsFolder 语义。
/// </summary>
public sealed class FileNodeIsFolderConverter : IValueConverter
{
    public static readonly FileNodeIsFolderConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isFolder = (value as FileNode)?.IsFolder ?? false;
        // parameter="!" 反转：文件节点（非文件夹）时 true。
        if (parameter is string p && p == "!") return !isFolder;
        return isFolder;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
