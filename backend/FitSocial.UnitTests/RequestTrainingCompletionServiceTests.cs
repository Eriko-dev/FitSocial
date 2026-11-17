using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FitSocial.Application.DTOs.TrainingPlan;
using FitSocial.Application.Exceptions;
using FitSocial.Application.Services;
using FitSocial.Domain.Entities;
using FitSocial.Domain.Interfaces;
using Moq;
using Xunit;

namespace FitSocial.UnitTests;

/// <summary>
/// Unit test suite for Request Training Completion & Coach Review feature:
/// - RequestWorkoutCompletionAsync (Trainee submits workout completion request with proof photo/video and notes)
/// - ReviewWorkoutCompletionAsync (Coach reviews and approves or rejects with feedback)
/// </summary>
public class RequestTrainingCompletionServiceTests
{
    private readonly Mock<ITrainingPlanRepository> _mockPlanRepo;
    private readonly Mock<IUnitOfWork> _mockUow;
    private readonly Mock<INotificationRepository> _mockNotificationRepo;
    private readonly TrainingPlanService _service;

    public RequestTrainingCompletionServiceTests()
    {
        _mockPlanRepo = new Mock<ITrainingPlanRepository>();
        _mockUow = new Mock<IUnitOfWork>();
        _mockNotificationRepo = new Mock<INotificationRepository>();

        _service = new TrainingPlanService(
            _mockPlanRepo.Object,
            _mockUow.Object,
            _mockNotificationRepo.Object);
    }

    #region Helper Factories

    private static (TrainingPlan Plan, TrainingPlanExercise Exercise) CreateExerciseHierarchy(
        Guid traineeId,
        Guid? coachId = null,
        DateTime? planStartDate = null,
        DateTime? planEndDate = null,
        short weekNumber = 1,
        short dayNumber = 1,
        string orderStatus = "PAID",
        string planStatus = "IN_PROGRESS",
        Guid? exerciseId = null)
    {
        var exId = exerciseId ?? Guid.NewGuid();
        var cId = coachId ?? Guid.NewGuid();
        var orderDetailsId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        var buyer = new User
        {
            UserId = traineeId,
            FullName = "Trainee Nguyen",
            Email = "trainee@fitsocial.com"
        };

        var coachUser = new User
        {
            UserId = cId,
            FullName = "Coach Tran",
            Email = "coach@fitsocial.com"
        };

        var order = new Order
        {
            OrderId = orderId,
            BuyerId = traineeId,
            Buyer = buyer,
            CoachId = cId,
            OrderStatus = orderStatus
        };

        var orderDetail = new OrderDetail
        {
            OrderDetailsId = orderDetailsId,
            OrderId = orderId,
            Order = order,
            PackageTitle = "VIP Bodybuilding 30 Days"
        };

        var coachProfile = new CoachProfile
        {
            CoachId = cId,
            Coach = coachUser,
            Status = "ACTIVE"
        };

        var plan = new TrainingPlan
        {
            TrainingPlanId = Guid.NewGuid(),
            CoachId = cId,
            Coach = coachProfile,
            OrderDetailsId = orderDetailsId,
            OrderDetails = orderDetail,
            StartDate = planStartDate,
            EndDate = planEndDate,
            Status = planStatus,
            Title = "30-Day Strength Plan",
            TrainingPlanExercises = new List<TrainingPlanExercise>()
        };

        var exercise = new TrainingPlanExercise
        {
            TrainingPlanExerciseId = exId,
            TrainingPlanId = plan.TrainingPlanId,
            TrainingPlan = plan,
            ExerciseName = "Barbell Bench Press",
            WeekNumber = weekNumber,
            DayNumber = dayNumber,
            WorkoutCompletionLogs = new List<WorkoutCompletionLog>()
        };

        plan.TrainingPlanExercises.Add(exercise);

        return (plan, exercise);
    }

    #endregion

    #region Trainee: RequestWorkoutCompletionAsync Tests

    [Fact]
    public async Task UTCID01_RequestWorkoutCompletion_Success_SetsPendingReview_SendsNotificationToCoach()
    {
        // Arrange
        var traineeId = Guid.NewGuid();
        var coachId = Guid.NewGuid();
        var (plan, exercise) = CreateExerciseHierarchy(traineeId, coachId);

        _mockPlanRepo.Setup(r => r.GetExerciseByIdAsync(exercise.TrainingPlanExerciseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(exercise);

        _mockPlanRepo.Setup(r => r.GetWorkoutLogAsync(exercise.TrainingPlanExerciseId, traineeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkoutCompletionLog?)null);

        _mockPlanRepo.Setup(r => r.GetTraineePlanWithLogsAsync(plan.OrderDetailsId.Value, traineeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(plan);

        var dto = new RequestWorkoutCompletionDto
        {
            TrainingPlanExerciseId = exercise.TrainingPlanExerciseId,
            MediaUrl = "https://res.cloudinary.com/demo/video/upload/workout_proof.mp4",
            Notes = "Completed 4 sets of 50kg, form felt solid."
        };

        // Act
        var result = await _service.RequestWorkoutCompletionAsync(traineeId, dto);

        // Assert
        Assert.NotNull(result);
        Assert.False(result.IsCompleted);
        Assert.Equal("PENDING_REVIEW", result.Status);
        Assert.Equal(dto.MediaUrl, result.MediaUrl);
        Assert.Equal(dto.Notes, result.Notes);

        _mockPlanRepo.Verify(r => r.AddWorkoutLogAsync(It.Is<WorkoutCompletionLog>(l =>
            l.Status == "PENDING_REVIEW" &&
            l.MediaUrl == dto.MediaUrl &&
            l.Notes == dto.Notes &&
            l.TraineeId == traineeId), It.IsAny<CancellationToken>()), Times.Once);

        _mockNotificationRepo.Verify(n => n.AddAsync(It.Is<Notification>(notif =>
            notif.UserId == coachId &&
            notif.ActorId == traineeId &&
            notif.Type == "WORKOUT_COMPLETION_REQUESTED"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UTCID02_RequestWorkoutCompletion_AlreadyCompleted_ThrowsBusinessException()
    {
        // Arrange
        var traineeId = Guid.NewGuid();
        var (plan, exercise) = CreateExerciseHierarchy(traineeId);

        var existingCompletedLog = new WorkoutCompletionLog
        {
            WorkoutCompletionLogId = Guid.NewGuid(),
            TrainingPlanExerciseId = exercise.TrainingPlanExerciseId,
            TraineeId = traineeId,
            Status = "COMPLETED"
        };

        _mockPlanRepo.Setup(r => r.GetExerciseByIdAsync(exercise.TrainingPlanExerciseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(exercise);

        _mockPlanRepo.Setup(r => r.GetWorkoutLogAsync(exercise.TrainingPlanExerciseId, traineeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingCompletedLog);

        var dto = new RequestWorkoutCompletionDto
        {
            TrainingPlanExerciseId = exercise.TrainingPlanExerciseId,
            MediaUrl = "https://res.cloudinary.com/demo/image/upload/proof.jpg"
        };

        // Act & Assert
        await Assert.ThrowsAsync<BusinessException>(() => _service.RequestWorkoutCompletionAsync(traineeId, dto));
    }

    [Fact]
    public async Task UTCID03_RequestWorkoutCompletion_UnauthorizedTrainee_ThrowsForbiddenException()
    {
        // Arrange
        var actualBuyerId = Guid.NewGuid();
        var attackerTraineeId = Guid.NewGuid();
        var (plan, exercise) = CreateExerciseHierarchy(actualBuyerId);

        _mockPlanRepo.Setup(r => r.GetExerciseByIdAsync(exercise.TrainingPlanExerciseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(exercise);

        var dto = new RequestWorkoutCompletionDto
        {
            TrainingPlanExerciseId = exercise.TrainingPlanExerciseId,
            MediaUrl = "https://example.com/proof.jpg"
        };

        // Act & Assert
        await Assert.ThrowsAsync<ForbiddenException>(() => _service.RequestWorkoutCompletionAsync(attackerTraineeId, dto));
    }

    [Fact]
    public async Task UTCID04_RequestWorkoutCompletion_NotesExceeds300Chars_ThrowsValidationException()
    {
        // Arrange
        var traineeId = Guid.NewGuid();
        var dto = new RequestWorkoutCompletionDto
        {
            TrainingPlanExerciseId = Guid.NewGuid(),
            Notes = new string('A', 301)
        };

        // Act & Assert
        await Assert.ThrowsAsync<ValidationException>(() => _service.RequestWorkoutCompletionAsync(traineeId, dto));
    }

    [Fact]
    public async Task UTCID05_RequestWorkoutCompletion_MediaUrlExceeds500Chars_ThrowsValidationException()
    {
        // Arrange
        var traineeId = Guid.NewGuid();
        var dto = new RequestWorkoutCompletionDto
        {
            TrainingPlanExerciseId = Guid.NewGuid(),
            MediaUrl = "https://example.com/" + new string('b', 500)
        };

        // Act & Assert
        await Assert.ThrowsAsync<ValidationException>(() => _service.RequestWorkoutCompletionAsync(traineeId, dto));
    }

    #endregion

    #region Coach: ReviewWorkoutCompletionAsync Tests

    [Fact]
    public async Task UTCID06_ReviewWorkoutCompletion_Approve_SetsCompleted_SendsNotificationToTrainee()
    {
        // Arrange
        var traineeId = Guid.NewGuid();
        var coachId = Guid.NewGuid();
        var (plan, exercise) = CreateExerciseHierarchy(traineeId, coachId);

        var existingPendingLog = new WorkoutCompletionLog
        {
            WorkoutCompletionLogId = Guid.NewGuid(),
            TrainingPlanExerciseId = exercise.TrainingPlanExerciseId,
            TraineeId = traineeId,
            Status = "PENDING_REVIEW",
            MediaUrl = "https://res.cloudinary.com/demo/video/upload/proof.mp4",
            Notes = "Felt great!",
            CreatedAt = DateTime.UtcNow
        };
        exercise.WorkoutCompletionLogs.Add(existingPendingLog);

        _mockPlanRepo.Setup(r => r.GetExerciseByIdAsync(exercise.TrainingPlanExerciseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(exercise);

        _mockPlanRepo.Setup(r => r.GetWorkoutLogAsync(exercise.TrainingPlanExerciseId, traineeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingPendingLog);

        _mockPlanRepo.Setup(r => r.GetTraineePlanWithLogsAsync(plan.OrderDetailsId.Value, traineeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(plan);

        var dto = new CoachReviewWorkoutDto
        {
            TrainingPlanExerciseId = exercise.TrainingPlanExerciseId,
            TraineeId = traineeId,
            Decision = "COMPLETED",
            Feedback = "Động tác rất chuẩn, form rất đẹp em nhé!"
        };

        // Act
        var result = await _service.ReviewWorkoutCompletionAsync(coachId, dto);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsCompleted);
        Assert.Equal("COMPLETED", result.Status);
        Assert.Equal(dto.Feedback, result.CoachFeedback);
        Assert.NotNull(result.ReviewedAt);
        Assert.Equal(coachId, existingPendingLog.ReviewedBy);

        _mockNotificationRepo.Verify(n => n.AddAsync(It.Is<Notification>(notif =>
            notif.UserId == traineeId &&
            notif.ActorId == coachId &&
            notif.Type == "WORKOUT_APPROVED" &&
            notif.Description.Contains("Động tác rất chuẩn")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UTCID07_ReviewWorkoutCompletion_Reject_RequiresFeedback_SetsUncomplete_SendsNotificationToTrainee()
    {
        // Arrange
        var traineeId = Guid.NewGuid();
        var coachId = Guid.NewGuid();
        var (plan, exercise) = CreateExerciseHierarchy(traineeId, coachId);

        var existingPendingLog = new WorkoutCompletionLog
        {
            WorkoutCompletionLogId = Guid.NewGuid(),
            TrainingPlanExerciseId = exercise.TrainingPlanExerciseId,
            TraineeId = traineeId,
            Status = "PENDING_REVIEW",
            MediaUrl = "https://res.cloudinary.com/demo/video/upload/proof.mp4",
            CreatedAt = DateTime.UtcNow
        };
        exercise.WorkoutCompletionLogs.Add(existingPendingLog);

        _mockPlanRepo.Setup(r => r.GetExerciseByIdAsync(exercise.TrainingPlanExerciseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(exercise);

        _mockPlanRepo.Setup(r => r.GetWorkoutLogAsync(exercise.TrainingPlanExerciseId, traineeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingPendingLog);

        _mockPlanRepo.Setup(r => r.GetTraineePlanWithLogsAsync(plan.OrderDetailsId.Value, traineeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(plan);

        var dto = new CoachReviewWorkoutDto
        {
            TrainingPlanExerciseId = exercise.TrainingPlanExerciseId,
            TraineeId = traineeId,
            Decision = "UNCOMPLETE",
            Feedback = "Lưng bị cong ở 2 rep cuối, em chỉnh lại tư thế rồi nộp lại video nhé."
        };

        // Act
        var result = await _service.ReviewWorkoutCompletionAsync(coachId, dto);

        // Assert
        Assert.NotNull(result);
        Assert.False(result.IsCompleted);
        Assert.Equal("UNCOMPLETE", result.Status);
        Assert.Equal(dto.Feedback, result.CoachFeedback);
        Assert.Equal(coachId, existingPendingLog.ReviewedBy);

        _mockNotificationRepo.Verify(n => n.AddAsync(It.Is<Notification>(notif =>
            notif.UserId == traineeId &&
            notif.ActorId == coachId &&
            notif.Type == "WORKOUT_REJECTED" &&
            notif.Description.Contains("Lưng bị cong")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UTCID08_ReviewWorkoutCompletion_Reject_MissingFeedback_ThrowsValidationException()
    {
        // Arrange
        var coachId = Guid.NewGuid();
        var dto = new CoachReviewWorkoutDto
        {
            TrainingPlanExerciseId = Guid.NewGuid(),
            TraineeId = Guid.NewGuid(),
            Decision = "UNCOMPLETE",
            Feedback = "" // Empty feedback on rejection!
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ValidationException>(() => _service.ReviewWorkoutCompletionAsync(coachId, dto));
        Assert.Contains("Feedback is required", ex.Message);
    }

    [Fact]
    public async Task UTCID09_ReviewWorkoutCompletion_UnauthorizedCoach_ThrowsForbiddenException()
    {
        // Arrange
        var traineeId = Guid.NewGuid();
        var actualCoachId = Guid.NewGuid();
        var strangerCoachId = Guid.NewGuid();
        var (plan, exercise) = CreateExerciseHierarchy(traineeId, actualCoachId);

        _mockPlanRepo.Setup(r => r.GetExerciseByIdAsync(exercise.TrainingPlanExerciseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(exercise);

        var dto = new CoachReviewWorkoutDto
        {
            TrainingPlanExerciseId = exercise.TrainingPlanExerciseId,
            TraineeId = traineeId,
            Decision = "COMPLETED",
            Feedback = "Great job!"
        };

        // Act & Assert
        await Assert.ThrowsAsync<ForbiddenException>(() => _service.ReviewWorkoutCompletionAsync(strangerCoachId, dto));
    }

    [Fact]
    public async Task UTCID10_ReviewWorkoutCompletion_InvalidDecision_ThrowsValidationException()
    {
        // Arrange
        var coachId = Guid.NewGuid();
        var dto = new CoachReviewWorkoutDto
        {
            TrainingPlanExerciseId = Guid.NewGuid(),
            TraineeId = Guid.NewGuid(),
            Decision = "INVALID_STATUS"
        };

        // Act & Assert
        await Assert.ThrowsAsync<ValidationException>(() => _service.ReviewWorkoutCompletionAsync(coachId, dto));
    }

    [Fact]
    public async Task UTCID11_ReviewWorkoutCompletion_100PercentDone_AutoCompletesTrainingPlan()
    {
        // Arrange
        var traineeId = Guid.NewGuid();
        var coachId = Guid.NewGuid();
        var (plan, exercise) = CreateExerciseHierarchy(traineeId, coachId);
        plan.Status = "IN_PROGRESS";

        var log = new WorkoutCompletionLog
        {
            WorkoutCompletionLogId = Guid.NewGuid(),
            TrainingPlanExerciseId = exercise.TrainingPlanExerciseId,
            TraineeId = traineeId,
            Status = "PENDING_REVIEW"
        };
        exercise.WorkoutCompletionLogs.Add(log);

        _mockPlanRepo.Setup(r => r.GetExerciseByIdAsync(exercise.TrainingPlanExerciseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(exercise);

        _mockPlanRepo.Setup(r => r.GetWorkoutLogAsync(exercise.TrainingPlanExerciseId, traineeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(log);

        _mockPlanRepo.Setup(r => r.GetTraineePlanWithLogsAsync(plan.OrderDetailsId.Value, traineeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(plan);

        var dto = new CoachReviewWorkoutDto
        {
            TrainingPlanExerciseId = exercise.TrainingPlanExerciseId,
            TraineeId = traineeId,
            Decision = "COMPLETED",
            Feedback = "Chúc mừng em đã hoàn thành bài tập cuối cùng!"
        };

        // Act
        var result = await _service.ReviewWorkoutCompletionAsync(coachId, dto);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsCompleted);
        Assert.Equal("COMPLETED", plan.Status); // TrainingPlan automatically set to COMPLETED!
    }

    #endregion
}
