using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FitSocial.Application.DTOs.Posts;
using FitSocial.Application.Interfaces;
using FitSocial.Application.Services;
using FitSocial.Domain.Entities;
using FitSocial.Domain.Interfaces;
using FitSocial.Infrastructure.Data;
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
}
