using FitSocial.Application.Exceptions;
using FitSocial.Domain.Entities;
using FitSocial.Domain.Interfaces;
using FitSocial.Domain.Models;
using FitSocial.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FitSocial.Infrastructure.Repositories;

public class PostRepository : Repository<Post>, IPostRepository
{
    private readonly IMemoryCache _memoryCache;

    public PostRepository(FitSocialDbContext dbContext, IMemoryCache memoryCache) : base(dbContext)
    {
        _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
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

    public void SetUserRecentCreatedPost(Guid userId, Guid postId)
    {
        if (userId == Guid.Empty || postId == Guid.Empty) return;
        var recentKey = $"user_recent_created_post_{userId}";
        _memoryCache?.Set(recentKey, postId, TimeSpan.FromMinutes(15));
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
        return GetPagedPostsAsync(postType, locationId, authorId, searchTerm, pageNumber, pageSize, "hot", null, null, null, null, cancellationToken);
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
        return GetPagedPostsAsync(postType, locationId, authorId, searchTerm, pageNumber, pageSize, sortBy, asOf, null, null, null, cancellationToken);
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
        string? sessionId,
        Guid? currentUserId = null,
        CancellationToken cancellationToken = default)
    {
        return GetPagedPostsAsync(postType, locationId, authorId, searchTerm, pageNumber, pageSize, sortBy, asOf, sessionId, currentUserId, null, cancellationToken);
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
        Guid? currentUserId,
        Guid? prioritizedPostId,
        CancellationToken cancellationToken = default)
    {
        var query = DbSet.AsNoTracking();

        query = query.Where(p => p.IsDeleted == null || p.IsDeleted == false);

        string? normalizedType = null;
        if (!string.IsNullOrWhiteSpace(postType))
        {
            normalizedType = postType.Trim().ToUpper();
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
        // When sessionId is provided, freeze the ranking for the session
        // so real-time interaction changes (likes/comments) do not shift posts between pages.
        Guid? effectivePrioritizedPostId = null;
        if (currentUserId.HasValue && currentUserId.Value != Guid.Empty && _memoryCache != null)
        {
            var recentKey = $"user_recent_created_post_{currentUserId.Value}";
            if (_memoryCache.TryGetValue(recentKey, out Guid cachedRecentPostId) && cachedRecentPostId != Guid.Empty)
            {
                // If caller supplied a prioritizedPostId, it must match the backend's recent-created-post cache.
                // Do not trust the frontend-provided ID as the sole authorization or prioritization mechanism.
                if (!prioritizedPostId.HasValue || prioritizedPostId.Value == cachedRecentPostId)
                {
                    effectivePrioritizedPostId = cachedRecentPostId;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            if (_memoryCache == null)
            {
                throw new InvalidOperationException("IMemoryCache is required to process feed session snapshot requests.");
            }

            if (!currentUserId.HasValue || currentUserId.Value == Guid.Empty)
            {
                throw new ForbiddenException("Authentication is required to use feed session snapshot.");
            }

            var validUserId = currentUserId.Value;
            var cacheKey = $"feed_session_snapshot_{sessionId.Trim()}";
            var normalizedSortBy = !string.IsNullOrWhiteSpace(sortBy) ? sortBy.Trim().ToLowerInvariant() : "hot";
            var normalizedSearch = !string.IsNullOrWhiteSpace(searchTerm) ? searchTerm.Trim() : null;

            if (_memoryCache.TryGetValue(cacheKey, out FeedSessionSnapshot? snapshot) && snapshot != null)
            {
                // Validate Session Ownership
                if (snapshot.UserId != validUserId)
                {
                    throw new ForbiddenException(
                        "Feed session ownership mismatch. You cannot access a feed session established by another user.");
                }

                // Validate Query Parameter Consistency
                var sameType = string.Equals(snapshot.PostType, normalizedType, StringComparison.OrdinalIgnoreCase);
                var sameLoc = snapshot.LocationId == locationId;
                var sameAuthor = snapshot.AuthorId == authorId;
                var sameSearch = string.Equals(snapshot.SearchTerm, normalizedSearch, StringComparison.OrdinalIgnoreCase);
                var sameSort = string.Equals(snapshot.SortBy, normalizedSortBy, StringComparison.OrdinalIgnoreCase);
                var samePageSize = snapshot.PageSize == pageSize;

                if (!sameType || !sameLoc || !sameAuthor || !sameSearch || !sameSort || !samePageSize)
                {
                    throw new ValidationException(
                        "Feed SessionId cannot be reused with different search or filter criteria. A new session must be established.");
                }
            }
            else
            {
                // Cache Entry Missing / Expired
                if (pageNumber > 1)
                {
                    // CRITICAL: Do NOT silently recalculate a new snapshot for page > 1!
                    throw new SessionExpiredException("Feed session has expired. Please refresh your feed.");
                }

                // pageNumber == 1: Create a brand new session snapshot
                var postIds = await orderedQuery
                    .Select(p => p.Id)
                    .Take(1000) // Cache safety cap
                    .ToListAsync(cancellationToken);

                if (effectivePrioritizedPostId.HasValue && effectivePrioritizedPostId.Value != Guid.Empty)
                {
                    var targetId = effectivePrioritizedPostId.Value;
                    var fifteenMinutesAgo = (asOf ?? DateTime.UtcNow).AddMinutes(-15);
                    var candidate = await DbSet.AsNoTracking()
                        .Where(p => p.Id == targetId &&
                                    (p.IsDeleted == null || p.IsDeleted == false) &&
                                    p.AuthorId == validUserId &&
                                    p.CreatedAt != null &&
                                    p.CreatedAt >= fifteenMinutesAgo)
                        .Select(p => new
                        {
                            p.Id,
                            p.PostType,
                            p.LocationId,
                            p.AuthorId,
                            p.Content
                        })
                        .FirstOrDefaultAsync(cancellationToken);

                    if (candidate != null)
                    {
                        bool matchesType = string.IsNullOrWhiteSpace(normalizedType) ||
                            string.Equals(candidate.PostType?.Trim(), normalizedType, StringComparison.OrdinalIgnoreCase);

                        bool matchesLoc = !locationId.HasValue || locationId.Value == Guid.Empty ||
                            candidate.LocationId == locationId.Value;

                        bool matchesAuthor = !authorId.HasValue || authorId.Value == Guid.Empty ||
                            candidate.AuthorId == authorId.Value;

                        bool matchesSearch = string.IsNullOrWhiteSpace(searchTerm) ||
                            (candidate.Content != null && candidate.Content.Contains(searchTerm.Trim(), StringComparison.OrdinalIgnoreCase));

                        if (matchesType && matchesLoc && matchesAuthor && matchesSearch)
                        {
                            postIds.Remove(candidate.Id);
                            postIds.Insert(0, candidate.Id);
                        }
                    }
                }

                snapshot = new FeedSessionSnapshot
                {
                    SessionId = sessionId.Trim(),
                    UserId = validUserId,
                    PostType = normalizedType,
                    LocationId = locationId,
                    AuthorId = authorId,
                    SearchTerm = normalizedSearch,
                    SortBy = normalizedSortBy,
                    PageSize = pageSize,
                    CreatedAt = DateTime.UtcNow,
                    PostIds = postIds
                };

                _memoryCache.Set(cacheKey, snapshot, new MemoryCacheEntryOptions
                {
                    SlidingExpiration = TimeSpan.FromMinutes(20),
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1)
                });
            }

            var cachedIds = snapshot.PostIds;
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

        if (pageNumber == 1 && currentUserId.HasValue && effectivePrioritizedPostId.HasValue && effectivePrioritizedPostId.Value != Guid.Empty)
        {
            var targetId = effectivePrioritizedPostId.Value;
            var fifteenMinutesAgo = (asOf ?? DateTime.UtcNow).AddMinutes(-15);
            var existingIndex = items.FindIndex(p => p.Id == targetId);
            if (existingIndex > 0)
            {
                var targetPost = items[existingIndex];
                if (targetPost.AuthorId == currentUserId.Value &&
                    targetPost.CreatedAt != null &&
                    targetPost.CreatedAt >= fifteenMinutesAgo &&
                    (targetPost.IsDeleted == null || targetPost.IsDeleted == false))
                {
                    items.RemoveAt(existingIndex);
                    items.Insert(0, targetPost);
                }
            }
            else if (existingIndex < 0)
            {
                var candidate = await DbSet.AsNoTracking()
                    .Where(p => p.Id == targetId &&
                                (p.IsDeleted == null || p.IsDeleted == false) &&
                                p.AuthorId == currentUserId.Value &&
                                p.CreatedAt != null &&
                                p.CreatedAt >= fifteenMinutesAgo)
                    .Include(p => p.Author)
                    .Include(p => p.Location)
                    .Include(p => p.PostMedia)
                    .Include(p => p.PostInteractions)
                    .Include(p => p.Comments)
                    .AsSplitQuery()
                    .FirstOrDefaultAsync(cancellationToken);

                if (candidate != null)
                {
                    bool matchesType = string.IsNullOrWhiteSpace(normalizedType) ||
                        string.Equals(candidate.PostType?.Trim(), normalizedType, StringComparison.OrdinalIgnoreCase);

                    bool matchesLoc = !locationId.HasValue || locationId.Value == Guid.Empty ||
                        candidate.LocationId == locationId.Value;

                    bool matchesAuthor = !authorId.HasValue || authorId.Value == Guid.Empty ||
                        candidate.AuthorId == authorId.Value;

                    bool matchesSearch = string.IsNullOrWhiteSpace(searchTerm) ||
                        (candidate.Content != null && candidate.Content.Contains(searchTerm.Trim(), StringComparison.OrdinalIgnoreCase));

                    if (matchesType && matchesLoc && matchesAuthor && matchesSearch)
                    {
                        items.Insert(0, candidate);
                        if (items.Count > pageSize)
                        {
                            items.RemoveAt(items.Count - 1);
                        }
                    }
                }
            }
        }

        return (items, unCachedTotalCount);
    }
}
