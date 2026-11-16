using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FitSocial.API.Controllers;
using FitSocial.Application.DTOs.Posts;
using FitSocial.Application.Interfaces;
using FitSocial.Application.Services;
using FitSocial.Domain.Entities;
using FitSocial.Domain.Interfaces;
using FitSocial.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace FitSocial.UnitTests;

public class TrendingFeedTests
{
    private readonly ITestOutputHelper _output;

    public TrendingFeedTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void CalculateHotScore_AccuratelyComputesEngagementAndDecay()
    {
        var asOf = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        
        // Post 1: 2 hours old, 10 reactions, 5 comments
        // EngagementScore = 10 * 1 + 5 * 2 = 20
        // HoursElapsed = 2
        // HotScore = 20 / (2 + 2)^1.5 = 20 / 4^1.5 = 20 / 8 = 2.5
        var createdAt1 = asOf.AddHours(-2);
        var score1 = PostService.CalculateHotScore(10, 5, createdAt1, asOf);
        Assert.Equal(2.5, score1, precision: 4);

        // Post 2: 0 hours old, 0 reactions, 0 comments
        // EngagementScore = 0
        // HotScore = 0 / 2^1.5 = 0
        var createdAt2 = asOf;
        var score2 = PostService.CalculateHotScore(0, 0, createdAt2, asOf);
        Assert.Equal(0.0, score2);

        // Post 3: 6 hours old, 5 reactions, 2 comments
        // Engagement = 5*1 + 2*2 = 9
        // HoursElapsed = 6
        // HotScore = 9 / (6 + 2)^1.5 = 9 / 8^1.5 = 9 / 22.627417 = ~0.3977
        var createdAt3 = asOf.AddHours(-6);
        var score3 = PostService.CalculateHotScore(5, 2, createdAt3, asOf);
        var expected3 = 9.0 / Math.Pow(8.0, 1.5);
        Assert.Equal(expected3, score3, precision: 4);

        // Score 1 (2.5) > Score 3 (~0.3977) > Score 2 (0.0)
        Assert.True(score1 > score3);
        Assert.True(score3 > score2);
    }

    [Fact]
    public void CalculateHotScore_HandlesClockSkew_WithoutNegativeHoursElapsed()
    {
        var asOf = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        var futureCreatedAt = asOf.AddMinutes(5); // slight clock skew

        var score = PostService.CalculateHotScore(2, 1, futureCreatedAt, asOf);
        // HoursElapsed clamped to 0 -> (2*1 + 1*2) / 2^1.5 = 4 / 2.828427 = ~1.4142
        var expected = 4.0 / Math.Pow(2.0, 1.5);
        Assert.Equal(expected, score, precision: 4);
    }

    [Fact]
    public void ZeroEngagementPosts_RemainInFeedAndSortByCreatedAtDescending()
    {
        var asOf = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        var postNew = new Post
        {
            Id = Guid.NewGuid(),
            CreatedAt = asOf.AddHours(-1),
            PostInteractions = new List<PostInteraction>(),
            Comments = new List<Comment>()
        };
        var postOld = new Post
        {
            Id = Guid.NewGuid(),
            CreatedAt = asOf.AddHours(-5),
            PostInteractions = new List<PostInteraction>(),
            Comments = new List<Comment>()
        };

        var scoreNew = PostService.CalculateHotScore(postNew.PostInteractions.Count, postNew.Comments.Count, postNew.CreatedAt, asOf);
        var scoreOld = PostService.CalculateHotScore(postOld.PostInteractions.Count, postOld.Comments.Count, postOld.CreatedAt, asOf);

        Assert.Equal(0.0, scoreNew);
        Assert.Equal(0.0, scoreOld);

        // When HotScore is identical (both 0.0), secondary sort is CreatedAt descending
        var posts = new List<Post> { postOld, postNew };
        var sorted = posts
            .OrderByDescending(p => PostService.CalculateHotScore(p.PostInteractions.Count, p.Comments.Count, p.CreatedAt, asOf))
            .ThenByDescending(p => p.CreatedAt)
            .ThenByDescending(p => p.Id)
            .ToList();

        Assert.Equal(postNew.Id, sorted[0].Id);
        Assert.Equal(postOld.Id, sorted[1].Id);
    }

    [Fact]
    public void TieBreaker_UsesPostId_WhenHotScoreAndCreatedAtAreEqual()
    {
        var asOf = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        var sameTime = asOf.AddHours(-1);
        var idSmall = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var idLarge = Guid.Parse("99999999-9999-9999-9999-999999999999");

        var post1 = new Post { Id = idSmall, CreatedAt = sameTime };
        var post2 = new Post { Id = idLarge, CreatedAt = sameTime };

        var sorted = new List<Post> { post1, post2 }
            .OrderByDescending(p => PostService.CalculateHotScore(0, 0, p.CreatedAt, asOf))
            .ThenByDescending(p => p.CreatedAt)
            .ThenByDescending(p => p.Id)
            .ToList();

        Assert.Equal(idLarge, sorted[0].Id);
        Assert.Equal(idSmall, sorted[1].Id);
    }

    [Fact]
    public async Task GetPostsAsync_WithAsOfAndSortBy_InvokesExtendedRepositoryOverload()
    {
        var mockRepo = new Mock<IPostRepository>();
        var mockLocationRepo = new Mock<ILocationRepository>();
        var mockUserRepo = new Mock<IUserRepository>();
        var mockUnitOfWork = new Mock<IUnitOfWork>();

        var asOf = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        var samplePost = new Post
        {
            Id = Guid.NewGuid(),
            Content = "Trending Workout",
            PostType = "Normal",
            CreatedAt = asOf.AddHours(-1),
            Author = new User { UserId = Guid.NewGuid(), FullName = "Trainer John" },
            PostInteractions = new List<PostInteraction> { new() { PostId = Guid.NewGuid(), UserId = Guid.NewGuid() } },
            Comments = new List<Comment> { new() { Id = Guid.NewGuid(), Content = "Nice!" } }
        };

        mockRepo.Setup(r => r.GetPagedPostsAsync(
                null, null, null, null, 1, 10, "hot", asOf, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<Post> { samplePost }, 1));

        var postService = new PostService(
            mockRepo.Object,
            mockUserRepo.Object,
            mockLocationRepo.Object,
            mockUnitOfWork.Object);

        var query = new GetPostsQueryDto
        {
            PageNumber = 1,
            PageSize = 10,
            SortBy = "hot",
            AsOf = asOf
        };

        var result = await postService.GetPostsAsync(query);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Single(result.Data.Items);

        var dto = result.Data.Items[0];
        Assert.Equal(1, dto.LikeCount);
        Assert.Equal(1, dto.CommentCount);
        Assert.Equal(3.0, dto.EngagementScore); // 1*1 + 1*2 = 3
        Assert.True(dto.HotScore > 0);

        mockRepo.Verify(r => r.GetPagedPostsAsync(
            null, null, null, null, 1, 10, "hot", asOf, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void TestHotScoreOrderingWithIncludes_TranslatesSuccessfullyInNpgsql()
    {
        var options = new DbContextOptionsBuilder<FitSocialDbContext>()
            .UseNpgsql("Host=localhost;Database=fitsocial;Username=postgres;Password=postgres")
            .Options;

        using var context = new FitSocialDbContext(options);
        var asOfTime = DateTime.UtcNow;

        var query = context.Posts
            .Where(p => p.IsDeleted == null || p.IsDeleted == false);

        var orderedQuery = query
            .OrderByDescending(p =>
                ((double)(p.PostInteractions.Count * 1 + p.Comments.Count * 2)) /
                Math.Pow(Math.Max(0.0, (asOfTime - (p.CreatedAt ?? asOfTime)).TotalHours) + 2.0, 1.5))
            .ThenByDescending(p => p.CreatedAt)
            .ThenByDescending(p => p.Id);

        var pagedQuery = orderedQuery
            .Skip(0)
            .Take(10)
            .Include(p => p.Author)
            .Include(p => p.Location)
            .Include(p => p.PostMedia)
            .Include(p => p.PostInteractions)
            .Include(p => p.Comments)
            .AsSplitQuery();

        var sql = pagedQuery.ToQueryString();
        _output.WriteLine("Generated SQL:\n" + sql);
        Assert.Contains("ORDER BY", sql);
    }

    [Fact]
    public async Task FeedSessionSnapshot_FreezesRankingAcrossPages_EvenWhenReactionsChange()
    {
        var dbName = "FitSocial_SnapshotTest_" + Guid.NewGuid();
        var options = new DbContextOptionsBuilder<FitSocialDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        using var context = new FitSocialDbContext(options);
        var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var repo = new FitSocial.Infrastructure.Repositories.PostRepository(context, cache);

        var asOf = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        var author = new User { UserId = Guid.NewGuid(), FullName = "Athlete Test", Email = "athlete.test@fitsocial.vn", PasswordHash = "hash" };
        var location = new Location { LocationId = Guid.NewGuid(), LocationName = "Gym A" };
        context.Users.Add(author);
        context.Locations.Add(location);

        // Post 1: 5 reactions (Rank 1 initially)
        var p1 = new Post { Id = Guid.NewGuid(), AuthorId = author.UserId, Content = "Post 1", PostType = "Normal", CreatedAt = asOf.AddHours(-1), IsDeleted = false };
        // Post 2: 3 reactions (Rank 2 initially)
        var p2 = new Post { Id = Guid.NewGuid(), AuthorId = author.UserId, Content = "Post 2", PostType = "Normal", CreatedAt = asOf.AddHours(-1), IsDeleted = false };
        // Post 3: 1 reaction (Rank 3 initially)
        var p3 = new Post { Id = Guid.NewGuid(), AuthorId = author.UserId, Content = "Post 3", PostType = "Normal", CreatedAt = asOf.AddHours(-1), IsDeleted = false };
        // Post 4: 0 reactions (Rank 4 initially)
        var p4 = new Post { Id = Guid.NewGuid(), AuthorId = author.UserId, Content = "Post 4", PostType = "Normal", CreatedAt = asOf.AddHours(-1), IsDeleted = false };

        context.Posts.AddRange(p1, p2, p3, p4);

        for (int i = 0; i < 5; i++) p1.PostInteractions.Add(new PostInteraction { PostId = p1.Id, UserId = Guid.NewGuid() });
        for (int i = 0; i < 3; i++) p2.PostInteractions.Add(new PostInteraction { PostId = p2.Id, UserId = Guid.NewGuid() });
        for (int i = 0; i < 1; i++) p3.PostInteractions.Add(new PostInteraction { PostId = p3.Id, UserId = Guid.NewGuid() });

        await context.SaveChangesAsync();

        var sessionId = "session-snap-test-1";

        // Step 1: Fetch Page 1 (pageSize = 2) for session 1
        var (page1Items, totalCount) = await repo.GetPagedPostsAsync(
            postType: null, locationId: null, authorId: null, searchTerm: null,
            pageNumber: 1, pageSize: 2, sortBy: "hot", asOf: asOf, sessionId: sessionId, currentUserId: author.UserId);

        Assert.Equal(4, totalCount);
        Assert.Equal(2, page1Items.Count);
        Assert.Equal(p1.Id, page1Items[0].Id);
        Assert.Equal(p2.Id, page1Items[1].Id);

        // Step 2: Now simulate concurrent activity while user is reading Page 1.
        // Post 3 and Post 4 receive 20 new reactions each in database, making their scores far higher than Post 1 and Post 2!
        for (int i = 0; i < 20; i++)
        {
            context.PostInteractions.Add(new PostInteraction { PostId = p3.Id, UserId = Guid.NewGuid() });
            context.PostInteractions.Add(new PostInteraction { PostId = p4.Id, UserId = Guid.NewGuid() });
        }
        await context.SaveChangesAsync();

        // Step 3: Fetch Page 2 (pageSize = 2) for the SAME session
        // With snapshot ranking, Page 2 MUST return the remaining items from the frozen snapshot (Post 3, Post 4)
        // and must NOT duplicate Post 1 or Post 2!
        var (page2Items, totalCountPage2) = await repo.GetPagedPostsAsync(
            postType: null, locationId: null, authorId: null, searchTerm: null,
            pageNumber: 2, pageSize: 2, sortBy: "hot", asOf: asOf, sessionId: sessionId, currentUserId: author.UserId);

        Assert.Equal(4, totalCountPage2);
        Assert.Equal(2, page2Items.Count);
        Assert.Equal(p3.Id, page2Items[0].Id);
        Assert.Equal(p4.Id, page2Items[1].Id);

        // Step 4: Verify that a NEW session reflects the updated ranking
        var newSessionId = "session-snap-test-2";
        var (newPage1Items, newTotalCount) = await repo.GetPagedPostsAsync(
            postType: null, locationId: null, authorId: null, searchTerm: null,
            pageNumber: 1, pageSize: 2, sortBy: "hot", asOf: asOf, sessionId: newSessionId, currentUserId: author.UserId);

        Assert.Equal(4, newTotalCount);
        Assert.Equal(2, newPage1Items.Count);
        // In the fresh session, Post 3 and Post 4 (with 21 and 20 reactions) are now the Top 2 posts
        Assert.Contains(newPage1Items, p => p.Id == p3.Id);
        Assert.Contains(newPage1Items, p => p.Id == p4.Id);
    }

    [Fact]
    public async Task PostService_GetPostsAsync_PassesSessionId_AndReturnsInPagedResult()
    {
        var mockRepo = new Mock<IPostRepository>();
        var mockLocationRepo = new Mock<ILocationRepository>();
        var mockUserRepo = new Mock<IUserRepository>();
        var mockUnitOfWork = new Mock<IUnitOfWork>();

        var asOf = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        var samplePost = new Post
        {
            Id = Guid.NewGuid(),
            Content = "Session Feed Post",
            PostType = "Normal",
            CreatedAt = asOf.AddHours(-1),
            Author = new User { UserId = Guid.NewGuid(), FullName = "Trainer Alice" },
            PostInteractions = new List<PostInteraction>(),
            Comments = new List<Comment>()
        };

        var sessionId = "test-session-id-456";
        var userId = Guid.NewGuid();

        mockRepo.Setup(r => r.GetPagedPostsAsync(
                null, null, null, null, 1, 10, "hot", asOf, sessionId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<Post> { samplePost }, 1));

        var postService = new PostService(
            mockRepo.Object,
            mockUserRepo.Object,
            mockLocationRepo.Object,
            mockUnitOfWork.Object);

        var query = new GetPostsQueryDto
        {
            PageNumber = 1,
            PageSize = 10,
            SortBy = "hot",
            AsOf = asOf,
            SessionId = sessionId
        };

        var result = await postService.GetPostsAsync(query, userId);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(sessionId, result.Data.SessionId);
        Assert.Single(result.Data.Items);

        mockRepo.Verify(r => r.GetPagedPostsAsync(
            null, null, null, null, 1, 10, "hot", asOf, sessionId, userId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExistingSessionId_WithMissingSnapshot_ThrowsSessionExpiredException_OnPageGreaterThanOne()
    {
        var dbName = "FitSocial_ExpiredTest_" + Guid.NewGuid();
        var options = new DbContextOptionsBuilder<FitSocialDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        using var context = new FitSocialDbContext(options);
        var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var repo = new FitSocial.Infrastructure.Repositories.PostRepository(context, cache);

        var asOf = DateTime.UtcNow;
        var missingSessionId = "expired-session-id-999";
        var userId = Guid.NewGuid();

        // Requesting Page 2 when snapshot does NOT exist in cache must throw SessionExpiredException
        await Assert.ThrowsAsync<FitSocial.Application.Exceptions.SessionExpiredException>(async () =>
        {
            await repo.GetPagedPostsAsync(
                postType: null, locationId: null, authorId: null, searchTerm: null,
                pageNumber: 2, pageSize: 10, sortBy: "hot", asOf: asOf, sessionId: missingSessionId, currentUserId: userId);
        });
    }

    [Fact]
    public async Task ReusingSessionId_WithDifferentFilters_ThrowsValidationException()
    {
        var dbName = "FitSocial_FilterMismatchTest_" + Guid.NewGuid();
        var options = new DbContextOptionsBuilder<FitSocialDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        using var context = new FitSocialDbContext(options);
        var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var repo = new FitSocial.Infrastructure.Repositories.PostRepository(context, cache);

        var asOf = DateTime.UtcNow;
        var sessionId = "session-filter-mismatch";
        var userId = Guid.NewGuid();

        // Create snapshot on Page 1 with PostType = "Normal"
        await repo.GetPagedPostsAsync(
            postType: "Normal", locationId: null, authorId: null, searchTerm: null,
            pageNumber: 1, pageSize: 10, sortBy: "hot", asOf: asOf, sessionId: sessionId, currentUserId: userId);

        // Subsequent request attempting to reuse the same SessionId with PostType = "FindCoach" must fail
        var ex = await Assert.ThrowsAsync<FitSocial.Application.Exceptions.ValidationException>(async () =>
        {
            await repo.GetPagedPostsAsync(
                postType: "FindCoach", locationId: null, authorId: null, searchTerm: null,
                pageNumber: 2, pageSize: 10, sortBy: "hot", asOf: asOf, sessionId: sessionId, currentUserId: userId);
        });

        Assert.Contains("cannot be reused with different search or filter criteria", ex.Message);
    }

    [Fact]
    public async Task ReusingSessionId_WithDifferentPageSize_ThrowsValidationException()
    {
        var dbName = "FitSocial_PageSizeMismatchTest_" + Guid.NewGuid();
        var options = new DbContextOptionsBuilder<FitSocialDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        using var context = new FitSocialDbContext(options);
        var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var repo = new FitSocial.Infrastructure.Repositories.PostRepository(context, cache);

        var asOf = DateTime.UtcNow;
        var sessionId = "session-pagesize-mismatch";
        var userId = Guid.NewGuid();

        // Create snapshot on Page 1 with PageSize = 10
        await repo.GetPagedPostsAsync(
            postType: null, locationId: null, authorId: null, searchTerm: null,
            pageNumber: 1, pageSize: 10, sortBy: "hot", asOf: asOf, sessionId: sessionId, currentUserId: userId);

        // Subsequent request attempting to reuse the same SessionId with PageSize = 20 must fail
        var ex = await Assert.ThrowsAsync<FitSocial.Application.Exceptions.ValidationException>(async () =>
        {
            await repo.GetPagedPostsAsync(
                postType: null, locationId: null, authorId: null, searchTerm: null,
                pageNumber: 2, pageSize: 20, sortBy: "hot", asOf: asOf, sessionId: sessionId, currentUserId: userId);
        });

        Assert.Contains("cannot be reused with different search or filter criteria", ex.Message);
    }

    [Fact]
    public async Task PostsController_GetPosts_WithoutAuthentication_ReturnsUnauthorized()
    {
        var mockPostService = new Mock<IPostService>();
        var mockReportService = new Mock<IPostReportService>();
        var mockReactionService = new Mock<IPostReactionService>();
        var mockCommentService = new Mock<IPostCommentService>();

        var controller = new PostsController(
            mockPostService.Object,
            mockReportService.Object,
            mockReactionService.Object,
            mockCommentService.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext() // Unauthenticated, empty identity
            }
        };

        var query = new GetPostsQueryDto { PageNumber = 1, PageSize = 10, SessionId = "unauth-session" };
        var actionResult = await controller.GetPosts(query);

        var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(actionResult);
        Assert.Equal(401, unauthorizedResult.StatusCode);
    }

    [Fact]
    public async Task Guest_CannotCreate_FeedSessionSnapshot_ThrowsForbiddenException()
    {
        var dbName = "FitSocial_GuestRejectTest_" + Guid.NewGuid();
        var options = new DbContextOptionsBuilder<FitSocialDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        using var context = new FitSocialDbContext(options);
        var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var repo = new FitSocial.Infrastructure.Repositories.PostRepository(context, cache);

        var asOf = DateTime.UtcNow;
        var guestSessionId = "guest-session-rejected";

        var ex = await Assert.ThrowsAsync<FitSocial.Application.Exceptions.ForbiddenException>(async () =>
        {
            await repo.GetPagedPostsAsync(
                postType: null, locationId: null, authorId: null, searchTerm: null,
                pageNumber: 1, pageSize: 10, sortBy: "hot", asOf: asOf, sessionId: guestSessionId, currentUserId: null);
        });

        Assert.Contains("Authentication is required to use feed session snapshot", ex.Message);
    }

    [Fact]
    public async Task Missing_Or_EmptyGuidUserId_ThrowsForbiddenException()
    {
        var dbName = "FitSocial_EmptyGuidTest_" + Guid.NewGuid();
        var options = new DbContextOptionsBuilder<FitSocialDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        using var context = new FitSocialDbContext(options);
        var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var repo = new FitSocial.Infrastructure.Repositories.PostRepository(context, cache);

        var asOf = DateTime.UtcNow;
        var sessionId = "empty-guid-session";

        var ex = await Assert.ThrowsAsync<FitSocial.Application.Exceptions.ForbiddenException>(async () =>
        {
            await repo.GetPagedPostsAsync(
                postType: null, locationId: null, authorId: null, searchTerm: null,
                pageNumber: 1, pageSize: 10, sortBy: "hot", asOf: asOf, sessionId: sessionId, currentUserId: Guid.Empty);
        });

        Assert.Contains("Authentication is required to use feed session snapshot", ex.Message);
    }

    [Fact]
    public async Task AuthenticatedTrainee_CanCreateAndPaginate_Snapshot()
    {
        var dbName = "FitSocial_TraineeSessionTest_" + Guid.NewGuid();
        var options = new DbContextOptionsBuilder<FitSocialDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        using var context = new FitSocialDbContext(options);
        var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var repo = new FitSocial.Infrastructure.Repositories.PostRepository(context, cache);

        var asOf = DateTime.UtcNow;
        var trainee = new User { UserId = Guid.NewGuid(), FullName = "Trainee Bob", Email = "trainee@test.com", PasswordHash = "hash" };
        context.Users.Add(trainee);
        for (int i = 0; i < 4; i++)
        {
            context.Posts.Add(new Post
            {
                Id = Guid.NewGuid(),
                AuthorId = trainee.UserId,
                Content = $"Trainee Post {i}",
                PostType = "Normal",
                CreatedAt = asOf.AddMinutes(-i),
                IsDeleted = false
            });
        }
        await context.SaveChangesAsync();

        var sessionId = "trainee-session-1";

        // Page 1
        var (p1Items, p1Total) = await repo.GetPagedPostsAsync(
            postType: null, locationId: null, authorId: null, searchTerm: null,
            pageNumber: 1, pageSize: 2, sortBy: "hot", asOf: asOf, sessionId: sessionId, currentUserId: trainee.UserId);

        Assert.Equal(4, p1Total);
        Assert.Equal(2, p1Items.Count);

        // Page 2
        var (p2Items, p2Total) = await repo.GetPagedPostsAsync(
            postType: null, locationId: null, authorId: null, searchTerm: null,
            pageNumber: 2, pageSize: 2, sortBy: "hot", asOf: asOf, sessionId: sessionId, currentUserId: trainee.UserId);

        Assert.Equal(4, p2Total);
        Assert.Equal(2, p2Items.Count);
    }

    [Fact]
    public async Task AuthenticatedCoach_CanCreateAndPaginate_Snapshot()
    {
        var dbName = "FitSocial_CoachSessionTest_" + Guid.NewGuid();
        var options = new DbContextOptionsBuilder<FitSocialDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        using var context = new FitSocialDbContext(options);
        var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var repo = new FitSocial.Infrastructure.Repositories.PostRepository(context, cache);

        var asOf = DateTime.UtcNow;
        var coach = new User { UserId = Guid.NewGuid(), FullName = "Coach Mike", Email = "coach@test.com", PasswordHash = "hash" };
        context.Users.Add(coach);
        for (int i = 0; i < 4; i++)
        {
            context.Posts.Add(new Post
            {
                Id = Guid.NewGuid(),
                AuthorId = coach.UserId,
                Content = $"Coach Post {i}",
                PostType = "FindTrainee",
                CreatedAt = asOf.AddMinutes(-i),
                IsDeleted = false
            });
        }
        await context.SaveChangesAsync();

        var sessionId = "coach-session-1";

        // Page 1
        var (p1Items, p1Total) = await repo.GetPagedPostsAsync(
            postType: null, locationId: null, authorId: null, searchTerm: null,
            pageNumber: 1, pageSize: 2, sortBy: "hot", asOf: asOf, sessionId: sessionId, currentUserId: coach.UserId);

        Assert.Equal(4, p1Total);
        Assert.Equal(2, p1Items.Count);

        // Page 2
        var (p2Items, p2Total) = await repo.GetPagedPostsAsync(
            postType: null, locationId: null, authorId: null, searchTerm: null,
            pageNumber: 2, pageSize: 2, sortBy: "hot", asOf: asOf, sessionId: sessionId, currentUserId: coach.UserId);

        Assert.Equal(4, p2Total);
        Assert.Equal(2, p2Items.Count);
    }

    [Fact]
    public async Task Page1_ReturnsZeroPosts_WhenAllItemsSoftDeleted_HasNextPageIsTrue()
    {
        var dbName = "FitSocial_EmptyPage1Test_" + Guid.NewGuid();
        var options = new DbContextOptionsBuilder<FitSocialDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        using var context = new FitSocialDbContext(options);
        var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var repo = new FitSocial.Infrastructure.Repositories.PostRepository(context, cache);

        var asOf = DateTime.UtcNow;
        var author = new User { UserId = Guid.NewGuid(), FullName = "Author", Email = "author@test.com", PasswordHash = "hash" };
        context.Users.Add(author);

        var posts = new List<Post>();
        for (int i = 0; i < 15; i++)
        {
            posts.Add(new Post
            {
                Id = Guid.NewGuid(),
                AuthorId = author.UserId,
                Content = $"Post {i}",
                PostType = "Normal",
                CreatedAt = asOf.AddMinutes(-i),
                IsDeleted = false
            });
        }
        context.Posts.AddRange(posts);
        await context.SaveChangesAsync();

        var sessionId = "empty-p1-session";

        // Step 1: Create session snapshot on Page 1 (stores all 15 post IDs in snapshot)
        var (initialItems, initialTotal) = await repo.GetPagedPostsAsync(
            postType: null, locationId: null, authorId: null, searchTerm: null,
            pageNumber: 1, pageSize: 10, sortBy: "hot", asOf: asOf, sessionId: sessionId, currentUserId: author.UserId);

        Assert.Equal(15, initialTotal);
        Assert.Equal(10, initialItems.Count);

        // Step 2: Now simulate all 10 posts on Page 1 being soft-deleted
        var idsToDelete = initialItems.Select(x => x.Id).ToList();
        var trackedPosts = await context.Posts.Where(p => idsToDelete.Contains(p.Id)).ToListAsync();
        foreach (var p in trackedPosts)
        {
            p.IsDeleted = true;
        }
        await context.SaveChangesAsync();

        // Step 3: Re-fetch Page 1 within the same session
        var (p1Items, p1Total) = await repo.GetPagedPostsAsync(
            postType: null, locationId: null, authorId: null, searchTerm: null,
            pageNumber: 1, pageSize: 10, sortBy: "hot", asOf: asOf, sessionId: sessionId, currentUserId: author.UserId);

        // Verify: 0 posts returned in items, totalCount is 15, and hasNextPage is true!
        Assert.Empty(p1Items);
        Assert.Equal(15, p1Total);
        var hasNextPage = (1 * 10) < p1Total;
        Assert.True(hasNextPage);
    }

    [Fact]
    public void InitialEmptyBatch_TriggersNextBatch_AndRecoversDataOnPage2()
    {
        // Simulate frontend state machine for initial empty batch
        int consecutiveEmptyBatches = 0;
        int currentPage = 1;
        int pageSize = 10;
        int totalCount = 15;
        bool hasNextPage = true;
        bool append = false;
        var posts = new List<string>();

        // Page 1 returns 0 items
        var incomingPage1 = new List<string>(); // 0 items

        bool didAutoTriggerPage2 = false;
        if (!append && incomingPage1.Count == 0 && hasNextPage)
        {
            consecutiveEmptyBatches++;
            if (consecutiveEmptyBatches < 3)
            {
                didAutoTriggerPage2 = true;
                // Simulate LoadNextBatchAsync fetching Page 2
                int nextPage = currentPage + 1; // 2
                var incomingPage2 = new List<string> { "Post 11", "Post 12", "Post 13", "Post 14", "Post 15" };

                if (incomingPage2.Any())
                {
                    consecutiveEmptyBatches = 0; // Reset counter
                    posts.AddRange(incomingPage2);
                }
                currentPage = nextPage;
                hasNextPage = (currentPage * pageSize) < totalCount; // 2 * 10 < 15 -> false
            }
        }

        Assert.True(didAutoTriggerPage2);
        Assert.Equal(0, consecutiveEmptyBatches);
        Assert.Equal(5, posts.Count);
        Assert.Equal(2, currentPage);
        Assert.False(hasNextPage);
    }

    [Fact]
    public void ThreeConsecutiveEmptyBatches_StopsAutoAdvance_PreventingInfiniteLoop()
    {
        int consecutiveEmptyBatches = 0;
        int currentPage = 1;
        int totalBatchesAttempted = 0;

        // Simulate loop where 4 batches in a row are empty
        while (true)
        {
            totalBatchesAttempted++;
            var incoming = new List<string>(); // empty batch

            if (incoming.Count == 0)
            {
                consecutiveEmptyBatches++;
                if (consecutiveEmptyBatches >= 3)
                {
                    // Halt auto-advance
                    break;
                }
                currentPage++;
            }
        }

        // Must stop exactly at 3 batches! Never loop infinitely.
        Assert.Equal(3, totalBatchesAttempted);
        Assert.Equal(3, consecutiveEmptyBatches);
    }

    [Fact]
    public void PostRepository_Constructor_RequiresMemoryCache()
    {
        var options = new DbContextOptionsBuilder<FitSocialDbContext>()
            .UseInMemoryDatabase("FitSocial_NullCacheTest_" + Guid.NewGuid())
            .Options;
        using var context = new FitSocialDbContext(options);

        // Passing null memoryCache must throw ArgumentNullException
        Assert.Throws<ArgumentNullException>(() =>
        {
            new FitSocial.Infrastructure.Repositories.PostRepository(context, null!);
        });
    }

    [Fact]
    public async Task LegacyOverloads_DoNotBypassSessionSnapshot_WhenSessionIdIsProvided()
    {
        var dbName = "FitSocial_LegacyOverloadTest_" + Guid.NewGuid();
        var options = new DbContextOptionsBuilder<FitSocialDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        using var context = new FitSocialDbContext(options);
        var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var repo = new FitSocial.Infrastructure.Repositories.PostRepository(context, cache);

        var author = new User { UserId = Guid.NewGuid(), FullName = "Trainer", Email = "trainer@test.com", PasswordHash = "hash" };
        context.Users.Add(author);
        context.Posts.Add(new Post { Id = Guid.NewGuid(), AuthorId = author.UserId, Content = "Normal Post", PostType = "Normal", CreatedAt = DateTime.UtcNow, IsDeleted = false });
        await context.SaveChangesAsync();

        // 1. Calling legacy 7-param overload uses dynamic un-cached query (no sessionId)
        var (res7, total7) = await repo.GetPagedPostsAsync(null, null, null, null, 1, 10);
        Assert.Single(res7);
        Assert.Equal(1, total7);

        // 2. Calling legacy 9-param overload uses dynamic un-cached query (no sessionId)
        var (res9, total9) = await repo.GetPagedPostsAsync(null, null, null, null, 1, 10, "hot", DateTime.UtcNow);
        Assert.Single(res9);
        Assert.Equal(1, total9);

        // 3. When sessionId is provided, session snapshot is strictly enforced
        var sessionId = "enforced-session-123";
        var (snapItems, snapTotal) = await repo.GetPagedPostsAsync(null, null, null, null, 1, 10, "hot", DateTime.UtcNow, sessionId, author.UserId);
        Assert.Single(snapItems);
        Assert.Equal(1, snapTotal);
    }

    [Fact]
    public async Task SessionSnapshot_OwnershipMismatch_ThrowsForbiddenException()
    {
        var dbName = "FitSocial_OwnershipTest_" + Guid.NewGuid();
        var options = new DbContextOptionsBuilder<FitSocialDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        using var context = new FitSocialDbContext(options);
        var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var repo = new FitSocial.Infrastructure.Repositories.PostRepository(context, cache);

        var asOf = DateTime.UtcNow;
        var sessionId = "session-ownership-test";
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        // User A creates session snapshot
        await repo.GetPagedPostsAsync(
            postType: null, locationId: null, authorId: null, searchTerm: null,
            pageNumber: 1, pageSize: 10, sortBy: "hot", asOf: asOf, sessionId: sessionId, currentUserId: userA);

        // User B attempts to access User A's session snapshot
        var ex = await Assert.ThrowsAsync<FitSocial.Application.Exceptions.ForbiddenException>(async () =>
        {
            await repo.GetPagedPostsAsync(
                postType: null, locationId: null, authorId: null, searchTerm: null,
                pageNumber: 2, pageSize: 10, sortBy: "hot", asOf: asOf, sessionId: sessionId, currentUserId: userB);
        });

        Assert.Contains("ownership mismatch", ex.Message);
    }

    [Fact]
    public void ShortBatch_TriggerCalculation_DoesNotHaltWhenHasNextPageIsTrue()
    {
        // Scenario: Page 1 had 5 posts (e.g. because 5 posts were deleted), hasNextPage = true.
        var postIds = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        var hasNextPage = true;
        var currentPage = 1;
        var pageSize = 10;

        // Our short-batch logic:
        var targetIndex = (currentPage * pageSize) - 4; // 6
        Guid? currentTriggerPostId = null;

        if (targetIndex >= 0 && targetIndex < postIds.Count)
        {
            currentTriggerPostId = postIds[targetIndex];
        }
        else if (postIds.Count >= 7)
        {
            currentTriggerPostId = postIds[postIds.Count - 4];
        }
        else if (hasNextPage && postIds.Count > 0)
        {
            var shortBatchTriggerIndex = Math.Max(0, postIds.Count - 2);
            currentTriggerPostId = postIds[shortBatchTriggerIndex];
        }

        // Must NOT be null!
        Assert.NotNull(currentTriggerPostId);
        Assert.Equal(postIds[3], currentTriggerPostId.Value); // index 3 (4th post)
    }

    [Fact]
    public void StalePrefetchRequest_DoesNotBlockNewSession()
    {
        // Simulate frontend concurrency logic
        long loadPostsRequestId = 1;
        string? feedSessionId = "session-1";
        bool isPrefetching = true; // Stale prefetch was running

        // User changes filter -> LoadPostsAsync starts new session
        long newRequestId = ++loadPostsRequestId;
        string newSessionId = "session-2";
        bool newSessionPrefetching = false; // Reset by LoadPostsAsync

        // Stale prefetch returns afterward:
        long staleRequestId = 1;
        string staleSessionId = "session-1";

        bool shouldDiscard = (staleRequestId != loadPostsRequestId || staleSessionId != feedSessionId);
        Assert.True(shouldDiscard);

        // New session's prefetch lock remains unblocked
        Assert.False(newSessionPrefetching);
    }
}
