using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.Video.Domain;

public interface IVideoFeedService
{
    Task<List<VideoItem>> GetVideoFeedAsync(int limit = 20, int offset = 0, CancellationToken ct = default);
    Task<List<VideoItem>> GetMoreVideosAsync(int limit = 20, int offset = 0, CancellationToken ct = default);
    Task<VideoItem> AddVideoAsync(VideoItem videoItem, CancellationToken ct = default);
}
