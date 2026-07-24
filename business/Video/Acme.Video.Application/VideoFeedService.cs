using Acme.Video.Domain;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Acme.Video.Application;

public class VideoFeedService : IVideoFeedService
{
    private readonly IVideoFeedRepository _repository;

    public VideoFeedService(IVideoFeedRepository repository)
    {
        _repository = repository;
    }

    public async Task<List<VideoItem>> GetVideoFeedAsync(int limit = 20, int offset = 0)
    {
        return await _repository.GetPagedAsync(limit, offset);
    }

    public async Task<List<VideoItem>> GetMoreVideosAsync(int limit = 20, int offset = 0)
    {
        return await _repository.GetPagedAsync(limit, offset);
    }

    public async Task<VideoItem> AddVideoAsync(VideoItem videoItem)
    {
        return await _repository.AddAsync(videoItem);
    }
}