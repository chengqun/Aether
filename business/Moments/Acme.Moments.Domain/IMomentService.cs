using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Acme.Moments.Domain;

public interface IMomentService
{
    Task<List<Moment>> GetFriendsMomentsAsync(int limit = 50, int offset = 0, DateTime? since = null);
    Task<Moment> CreateMomentAsync(string content, string? imageUrl = null, string type = "Text", string visibility = "Friends");
    Task DeleteMomentAsync(string momentId);
}