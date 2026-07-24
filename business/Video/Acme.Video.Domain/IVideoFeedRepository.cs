using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.Video.Domain;

public interface IVideoFeedRepository
{
    Task<List<VideoItem>> GetPagedAsync(int limit, int offset, CancellationToken ct = default);
    Task<VideoItem> AddAsync(VideoItem item, CancellationToken ct = default);
}