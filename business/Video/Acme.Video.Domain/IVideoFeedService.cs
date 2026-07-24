using System.Collections.Generic;
using System.Threading.Tasks;

namespace Acme.Video.Domain;

public interface IVideoFeedService
{
    Task<List<VideoItem>> GetVideoFeedAsync(int limit = 20, int offset = 0);
    Task<List<VideoItem>> GetMoreVideosAsync(int limit = 20, int offset = 0);
    Task<VideoItem> AddVideoAsync(VideoItem videoItem);
}