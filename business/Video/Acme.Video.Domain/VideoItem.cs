using System;

namespace Acme.Video.Domain;

public class VideoItem
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string VideoUrl { get; set; } = string.Empty;
    public string? CoverUrl { get; set; }
    public string Author { get; set; } = string.Empty;
    public int Duration { get; set; }
    public DateTime CreatedAt { get; set; }
}