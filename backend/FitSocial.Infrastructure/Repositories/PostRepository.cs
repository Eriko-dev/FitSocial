using FitSocial.Domain.Entities;
using FitSocial.Domain.Interfaces;
using FitSocial.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FitSocial.Infrastructure.Repositories;

public class PostRepository : Repository<Post>, IPostRepository
{
    private readonly IMemoryCache? _memoryCache;

    public PostRepository(FitSocialDbContext dbContext, IMemoryCache? memoryCache = null) : base(dbContext)
    {
        _memoryCache = memoryCache;
    }

    public async Task<Post?> GetByIdWithDetailsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await DbSet
            .Include(p => p.Author)
            .Include(p => p.Location)
            .Include(p => p.PostMedia)
            .Include(p => p.PostInteractions)
            .Include(p => p.Comments)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == id && (p.IsDeleted == null || p.IsDeleted == false), cancellationToken);
    }

    public async Task<Post?> GetByIdWithMediaAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await DbSet
            .Include(p => p.PostMedia)
            .Include(p => p.Author)
            .Include(p => p.Location)
            .Include(p => p.PostInteractions)
            .Include(p => p.Comments)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == id && (p.IsDeleted == null || p.IsDeleted == false), cancellationToken);
    }

    public void RemoveMediaRange(IEnumerable<PostMedium> mediaItems)
    {
        DbContext.PostMedia.RemoveRange(mediaItems);
    }

    public Task<(List<Post> Items, int TotalCount)> GetPagedPostsAsync(
        string? postType,
        Guid? locationId,
        Guid? authorId,
        string? searchTerm,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        return GetPagedPostsAsync(postType, locationId, authorId, searchTerm, pageNumber, pageSize, "hot", null, null, cancellationToken);
    }

    public Task<(List<Post> Items, int TotalCount)> GetPagedPostsAsync(
        string? postType,
        Guid? locationId,
        Guid? authorId,
        string? searchTerm,
        int pageNumber,
        int pageSize,
        string? sortBy,
        DateTime? asOf,
        CancellationToken cancellationToken = default)
    {
        return GetPagedPostsAsync(postType, locationId, authorId, searchTerm, pageNumber, pageSize, sortBy, asOf, null, cancellationToken);
    }

    public async Task<(List<Post> Items, int TotalCount)> GetPagedPostsAsync(
        string? postType,
        Guid? locationId,
        Guid? authorId,
        string? searchTerm,
        int pageNumber,
        int pageSize,
        string? sortBy,
        DateTime? asOf,
        string? sessionId,
        CancellationToken cancellationToken = default)
    {
        var query = DbSet.AsNoTracking();

        query = query.Where(p => p.IsDeleted == null || p.IsDeleted == false);

        if (!string.IsNullOrWhiteSpace(postType))
        {
            var normalizedType = postType.Trim().ToUpper();
            query = query.Where(p => p.PostType.ToUpper() == normalizedType);
        }

        if (locationId.HasValue && locationId.Value != Guid.Empty)
        {
            query = query.Where(p => p.LocationId == locationId.Value);
        }

        if (authorId.HasValue && authorId.Value != Guid.Empty)
        {
            query = query.Where(p => p.AuthorId == authorId.Value);
        }

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = $"%{searchTerm.Trim()}%";
            query = query.Where(p => p.Content != null && EF.Functions.ILike(p.Content, term));
        }

        var asOfTime = asOf ?? DateTime.UtcNow;

        IQueryable<Post> orderedQuery;

        if (string.Equals(sortBy, "newest", StringComparison.OrdinalIgnoreCase))
        {
            orderedQuery = query
                .OrderByDescending(p => p.CreatedAt)
                .ThenByDescending(p => p.Id);
        }
        else
        {
            // Trending / Hot Score algorithm:
            // EngagementScore = (ReactionCount * 1) + (CommentCount * 2)
            // HotScore = EngagementScore / (HoursElapsed + 2)^1.5
            // Tie-breaker 1: CreatedAt descending
            // Tie-breaker 2: Id descending
            orderedQuery = query
                .OrderByDescending(p =>
                    ((double)(p.PostInteractions.Count * 1 + p.Comments.Count * 2)) /
                    Math.Pow(Math.Max(0.0, (asOfTime - (p.CreatedAt ?? asOfTime)).TotalHours) + 2.0, 1.5))
                .ThenByDescending(p => p.CreatedAt)
                .ThenByDescending(p => p.Id);
        }

        // Feed Session Snapshot Ranking:
        // When sessionId is provided and cache is available, freeze the ranking for the session
        // so real-time interaction changes (likes/comments) do not shift posts between pages.
        if (!string.IsNullOrWhiteSpace(sessionId) && _memoryCache != null)
        {
            var cacheKey = $"feed_session_snapshot_{sessionId.Trim()}";
            if (!_memoryCache.TryGetValue(cacheKey, out List<Guid>? cachedIds) || cachedIds == null)
            {
                cachedIds = await orderedQuery.Select(p => p.Id).ToListAsync(cancellationToken);
                _memoryCache.Set(cacheKey, cachedIds, new MemoryCacheEntryOptions
                {
                    SlidingExpiration = TimeSpan.FromMinutes(20),
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1)
                });
            }

            var totalCount = cachedIds.Count;
            var pageIds = cachedIds.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToList();
            if (pageIds.Count == 0)
            {
                return (new List<Post>(), totalCount);
            }

            var posts = await DbSet.AsNoTracking()
                .Where(p => pageIds.Contains(p.Id) && (p.IsDeleted == null || p.IsDeleted == false))
                .Include(p => p.Author)
                .Include(p => p.Location)
                .Include(p => p.PostMedia)
                .Include(p => p.PostInteractions)
                .Include(p => p.Comments)
                .AsSplitQuery()
                .ToListAsync(cancellationToken);

            // Reorder posts strictly according to the session snapshot's frozen pageIds
            var postDict = posts.ToDictionary(p => p.Id);
            var orderedItems = new List<Post>(pageIds.Count);
            foreach (var id in pageIds)
            {
                if (postDict.TryGetValue(id, out var post))
                {
                    orderedItems.Add(post);
                }
            }

            return (orderedItems, totalCount);
        }

        // Standard non-snapshot pagination fallback (for callers without sessionId or when cache is unavailable)
        var unCachedTotalCount = await query.CountAsync(cancellationToken);

        var items = await orderedQuery
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Include(p => p.Author)
            .Include(p => p.Location)
            .Include(p => p.PostMedia)
            .Include(p => p.PostInteractions)
            .Include(p => p.Comments)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        return (items, unCachedTotalCount);
    }
}
