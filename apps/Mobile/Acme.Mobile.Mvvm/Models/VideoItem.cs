using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Mobile.Mvvm.Models;

public partial class VideoItem : ObservableObject
{
    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _videoUrl = string.Empty;

    [ObservableProperty]
    private string? _coverUrl;

    [ObservableProperty]
    private string _author = string.Empty;

    [ObservableProperty]
    private int _duration;
}