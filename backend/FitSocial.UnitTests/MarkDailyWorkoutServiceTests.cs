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
/// Unit test suite for Mark Daily Workout feature:
/// TrainingPlanService.ToggleWorkoutCompletionAsync(Guid traineeId, ToggleWorkoutCompletionDto dto, CancellationToken ct)
/// Covers:
/// - Normal test cases (UTCID01 - UTCID10): Marking completed/incomplete, notes, transitions, flexible plans, notifications
/// - Boundary test cases (UTCID11 - UTCID18): 300/301 chars notes, empty notes, today/past/future schedule boundaries, Guid.Empty boundaries, null dto
/// - Abnormal test cases (UTCID19 - UTCID24): Non-existent exercise, unauthorized trainee, refunded package, invalid order status, uncomplete completed exercise, cancellation token
/// </summary>
public class MarkDailyWorkoutServiceTests
{
    private readonly Mock<ITrainingPlanRepository> _mockPlanRepo;
    private readonly Mock<IUnitOfWork> _mockUow;
    private readonly Mock<INotificationRepository> _mockNotificationRepo;
    private readonly TrainingPlanService _service;

    public MarkDailyWorkoutServiceTests()
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

        var plan = new TrainingPlan
        {
            TrainingPlanId = Guid.NewGuid(),
            CoachId = cId,
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

    #region Normal Test Cases (UTCID01 - UTCID10)

    [Fact]
    public async Task UTCID01_ToggleWorkoutCompletionAsync_FirstTimeCompletion_CreatesCompletedLogSuccessfully()
    {
        // Arrange
        var traineeId = Guid.NewGuid();
        var businessToday = TrainingPlanService.GetBusinessToday();
        var (plan, exercise) = CreateExerciseHierarchy(traineeId, planStartDate: businessToday);

        var dto = new ToggleWorkoutCompletionDto
        {
            TrainingPlanExerciseId = exercise.TrainingPlanExerciseId,
            Status = "COMPLETED",
            Notes = "Felt great, completed all 4 sets."
        };

        _mockPlanRepo.Setup(r => r.GetExerciseByIdAsync(exercise.TrainingPlanExerciseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(exercise);
        _mockPlanRepo.Setup(r => r.GetWorkoutLogAsync(exercise.TrainingPlanExerciseId, traineeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkoutCompletionLog?)null);
        _mockPlanRepo.Setup(r => r.GetTraineePlanWithLogsAsync(plan.OrderDetailsId ?? Guid.Empty, traineeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(plan);

        // Act
        var result = await _service.ToggleWorkoutCompletionAsync(traineeId, dto);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsCompleted);
        Assert.Equal("COMPLETED", result.Status);
        Assert.Equal(exercise.TrainingPlanExerciseId, result.TrainingPlanExerciseId);
        Assert.Equal("Felt great, completed all 4 sets.", result.Notes);
        Assert.NotNull(result.WorkoutCompletionLogId);

        _mockPlanRepo.Verify(r => r.AddWorkoutLogAsync(It.Is<WorkoutCompletionLog>(l =>
            l.TrainingPlanExerciseId == exercise.TrainingPlanExerciseId &&
            l.TraineeId == traineeId &&
            l.Status == "COMPLETED" &&
            l.Notes == "Felt great, completed all 4 sets."), It.IsAny<CancellationToken>()), Times.Once);
        _mockUow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task UTCID02_ToggleWorkoutCompletionAsync_DefaultStatusNull_MarksAsCompleted()
    {
        // Arrange
        var traineeId = Guid.NewGuid();
        var businessToday = TrainingPlanService.GetBusinessToday();
        var (plan, exercise) = CreateExerciseHierarchy(traineeId, planStartDate: businessToday);

        var dto = new ToggleWorkoutCompletionDto
        {
            TrainingPlanExerciseId = exercise.TrainingPlanExerciseId,
            Status = null, // Default behavior when status is null is COMPLETED
            Notes = "Standard workout completion"
        };

        _mockPlanRepo.Setup(r => r.GetExerciseByIdAsync(exercise.TrainingPlanExerciseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(exercise);
        _mockPlanRepo.Setup(r => r.GetWorkoutLogAsync(exercise.TrainingPlanExerciseId, traineeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkoutCompletionLog?)null);
        _mockPlanRepo.Setup(r => r.GetTraineePlanWithLogsAsync(plan.OrderDetailsId ?? Guid.Empty, traineeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(plan);

        // Act
        var result = await _service.ToggleWorkoutCompletionAsync(traineeId, dto);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsCompleted);
        Assert.Equal("COMPLETED", result.Status);
    }

    [Fact]
    public async Task UTCID03_ToggleWorkoutCompletionAsync_FirstTimeIncomplete_CreatesIncompleteLog()
    {
        // Arrange
        var traineeId = Guid.NewGuid();
        var businessToday = TrainingPlanService.GetBusinessToday();
        var (plan, exercise) = CreateExerciseHierarchy(traineeId, planStartDate: businessToday);

        var dto = new ToggleWorkoutCompletionDto
        {
            TrainingPlanExerciseId = exercise.TrainingPlanExerciseId,
            Status = "INCOMPLETE",
            Notes = "Could not finish due to muscle soreness"
        };

        _mockPlanRepo.Setup(r => r.GetExerciseByIdAsync(exercise.TrainingPlanExerciseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(exercise);
        _mockPlanRepo.Setup(r => r.GetWorkoutLogAsync(exercise.TrainingPlanExerciseId, traineeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkoutCompletionLog?)null);
        _mockPlanRepo.Setup(r => r.GetTraineePlanWithLogsAsync(plan.OrderDetailsId ?? Guid.Empty, traineeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(plan);

        // Act
        var result = await _service.ToggleWorkoutCompletionAsync(traineeId, dto);

        // Assert
        Assert.NotNull(result);
        Assert.False(result.IsCompleted);
        Assert.Equal("INCOMPLETE", result.Status);
        Assert.Equal("Could not finish due to muscle soreness", result.Notes);

        _mockPlanRepo.Verify(r => r.AddWorkoutLogAsync(It.Is<WorkoutCompletionLog>(l =>
            l.Status == "INCOMPLETE"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UTCID04_ToggleWorkoutCompletionAsync_ExistingLogIsIncomplete_TransitionsToCompleted()
    {
        // Arrange
        var traineeId = Guid.NewGuid();
        var businessToday = TrainingPlanService.GetBusinessToday();
        var (plan, exercise) = CreateExerciseHierarchy(traineeId, planStartDate: businessToday);

        var existingLog = new WorkoutCompletionLog
        {
            WorkoutCompletionLogId = Guid.NewGuid(),
            TrainingPlanExerciseId = exercise.TrainingPlanExerciseId,
            TraineeId = traineeId,
            Status = "INCOMPLETE",
            Notes = "Initial soreness",
            CreatedAt = DateTime.UtcNow.AddHours(-2)
        };

        var dto = new ToggleWorkoutCompletionDto
        {
            TrainingPlanExerciseId = exercise.TrainingPlanExerciseId,
            Status = "COMPLETED",
            Notes = "Rested and finished in evening"
        };

        _mockPlanRepo.Setup(r => r.GetExerciseByIdAsync(exercise.TrainingPlanExerciseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(exercise);
        _mockPlanRepo.Setup(r => r.GetWorkoutLogAsync(exercise.TrainingPlanExerciseId, traineeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingLog);
        _mockPlanRepo.Setup(r => r.GetTraineePlanWithLogsAsync(plan.OrderDetailsId ?? Guid.Empty, traineeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(plan);

        // Act
        var result = await _service.ToggleWorkoutCompletionAsync(traineeId, dto);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsCompleted);
        Assert.Equal("COMPLETED", result.Status);
        Assert.Equal("Rested and finished in evening", existingLog.Notes);
        Assert.Equal("COMPLETED", existingLog.Status);
    }

    [Fact]
    public async Task UTCID05_ToggleWorkoutCompletionAsync_ExistingCompletedLog_UpdatesNotesOnly()
    {
        // Arrange
        var traineeId = Guid.NewGuid();
        var businessToday = TrainingPlanService.GetBusinessToday();
        var (plan, exercise) = CreateExerciseHierarchy(traineeId, planStartDate: businessToday);

        var existingLog = new WorkoutCompletionLog
        {
            WorkoutCompletionLogId = Guid.NewGuid(),
            TrainingPlanExerciseId = exercise.TrainingPlanExerciseId,
            TraineeId = traineeId,
            Status = "COMPLETED",
            Notes = "Original note",
            CreatedAt = DateTime.UtcNow.AddHours(-1)
        };

        var dto = new ToggleWorkoutCompletionDto
        {
            TrainingPlanExerciseId = exercise.TrainingPlanExerciseId,
            Status = "COMPLETED",
            Notes = "Updated feedback for coach"
        };

        _mockPlanRepo.Setup(r => r.GetExerciseByIdAsync(exercise.TrainingPlanExerciseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(exercise);
        _mockPlanRepo.Setup(r => r.GetWorkoutLogAsync(exercise.TrainingPlanExerciseId, traineeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingLog);
        _mockPlanRepo.Setup(r => r.GetTraineePlanWithLogsAsync(plan.OrderDetailsId ?? Guid.Empty, traineeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(plan);

        // Act
        var result = await _service.ToggleWorkoutCompletionAsync(traineeId, dto);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsCompleted);
        Assert.Equal("COMPLETED", result.Status);
        Assert.Equal("Updated feedback for coach", existingLog.Notes);
    }

    [Fact]
    public async Task UTCID06_ToggleWorkoutCompletionAsync_PlanWithoutStartDate_AllowsMarkingExercise()
    {
        // Arrange (Flexible plan where StartDate is null)
        var traineeId = Guid.NewGuid();
        var (plan, exercise) = CreateExerciseHierarchy(traineeId, planStartDate: null);

        var dto = new ToggleWorkoutCompletionDto
        {
            TrainingPlanExerciseId = exercise.TrainingPlanExerciseId,
            Status = "COMPLETED"
        };

        _mockPlanRepo.Setup(r => r.GetExerciseByIdAsync(exercise.TrainingPlanExerciseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(exercise);
        _mockPlanRepo.Setup(r => r.GetWorkoutLogAsync(exercise.TrainingPlanExerciseId, traineeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkoutCompletionLog?)null);
        _mockPlanRepo.Setup(r => r.GetTraineePlanWithLogsAsync(plan.OrderDetailsId ?? Guid.Empty, traineeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(plan);

        // Act
        var result = await _service.ToggleWorkoutCompletionAsync(traineeId, dto);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsCompleted);
    }

    [Fact]
    public async Task UTCID07_ToggleWorkoutCompletionAsync_DailyWorkoutFullyCompleted_SendsDailyWorkoutCompletedNotification()
    {
        // Arrange (2 exercises for Day 1, exercise 1 already completed, marking exercise 2 completes the day)
        var traineeId = Guid.NewGuid();
        var coachId = Guid.NewGuid();
        var businessToday = TrainingPlanService.GetBusinessToday();
        var (plan, exercise2) = CreateExerciseHierarchy(traineeId, coachId, planStartDate: businessToday, dayNumber: 1);

        var exercise1 = new TrainingPlanExercise
        {
            TrainingPlanExerciseId = Guid.NewGuid(),
            TrainingPlanId = plan.TrainingPlanId,
            WeekNumber = 1,
            DayNumber = 1,
            WorkoutCompletionLogs = new List<WorkoutCompletionLog>
            {
                new WorkoutCompletionLog { TraineeId = traineeId, Status = "COMPLETED" }
            }
        };

        // Extra exercise for Day 2 so whole plan is not 100% yet
        var exerciseDay2 = new TrainingPlanExercise
        {
            TrainingPlanExerciseId = Guid.NewGuid(),
            TrainingPlanId = plan.TrainingPlanId,
            WeekNumber = 1,
            DayNumber = 2
        };

        plan.TrainingPlanExercises.Add(exercise1);
        plan.TrainingPlanExercises.Add(exerciseDay2);

        var dto = new ToggleWorkoutCompletionDto
        {
            TrainingPlanExerciseId = exercise2.TrainingPlanExerciseId,
            Status = "COMPLETED"
        };

        _mockPlanRepo.Setup(r => r.GetExerciseByIdAsync(exercise2.TrainingPlanExerciseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(exercise2);
        _mockPlanRepo.Setup(r => r.GetWorkoutLogAsync(exercise2.TrainingPlanExerciseId, traineeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkoutCompletionLog?)null);

        // When refreshed, exercise2 also has COMPLETED log
        _mockPlanRepo.Setup(r => r.GetTraineePlanWithLogsAsync(plan.OrderDetailsId ?? Guid.Empty, traineeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                exercise2.WorkoutCompletionLogs.Add(new WorkoutCompletionLog { TraineeId = traineeId, Status = "COMPLETED" });
                return plan;
            });

        // Act
        var result = await _service.ToggleWorkoutCompletionAsync(traineeId, dto);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsCompleted);
        _mockNotificationRepo.Verify(n => n.AddAsync(It.Is<Notification>(notif =>
            notif.UserId == coachId &&
            notif.Type == "WORKOUT_DAY_COMPLETED"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UTCID08_ToggleWorkoutCompletionAsync_PlanFullyCompleted_UpdatesPlanStatusAndSendsPlanCompletedNotification()
    {
        // Arrange (1 exercise in total, marking it completes 100% of plan)
        var traineeId = Guid.NewGuid();
        var coachId = Guid.NewGuid();
        var businessToday = TrainingPlanService.GetBusinessToday();
        var (plan, exercise) = CreateExerciseHierarchy(traineeId, coachId, planStartDate: businessToday);

        var dto = new ToggleWorkoutCompletionDto
        {
            TrainingPlanExerciseId = exercise.TrainingPlanExerciseId,
            Status = "COMPLETED"
        };

        _mockPlanRepo.Setup(r => r.GetExerciseByIdAsync(exercise.TrainingPlanExerciseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(exercise);
        _mockPlanRepo.Setup(r => r.GetWorkoutLogAsync(exercise.TrainingPlanExerciseId, traineeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkoutCompletionLog?)null);

        _mockPlanRepo.Setup(r => r.GetTraineePlanWithLogsAsync(plan.OrderDetailsId ?? Guid.Empty, traineeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                exercise.WorkoutCompletionLogs.Add(new WorkoutCompletionLog { TraineeId = traineeId, Status = "COMPLETED" });
                return plan;
            });

        // Act
        var result = await _service.ToggleWorkoutCompletionAsync(traineeId, dto);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsCompleted);
        Assert.Equal("COMPLETED", plan.Status);

        _mockNotificationRepo.Verify(n => n.AddAsync(It.Is<Notification>(notif =>
            notif.UserId == coachId &&
            notif.Type == "TRAINING_PLAN_COMPLETED"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UTCID09_ToggleWorkoutCompletionAsync_OrderStatusIsCompleted_AllowsMarking()
    {
        // Arrange
        var traineeId = Guid.NewGuid();
        var businessToday = TrainingPlanService.GetBusinessToday();
        var (plan, exercise) = CreateExerciseHierarchy(traineeId, planStartDate: businessToday, orderStatus: "COMPLETED");

        var dto = new ToggleWorkoutCompletionDto
        {
            TrainingPlanExerciseId = exercise.TrainingPlanExerciseId,
            Status = "COMPLETED"
        };

        _mockPlanRepo.Setup(r => r.GetExerciseByIdAsync(exercise.TrainingPlanExerciseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(exercise);
        _mockPlanRepo.Setup(r => r.GetWorkoutLogAsync(exercise.TrainingPlanExerciseId, traineeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkoutCompletionLog?)null);
        _mockPlanRepo.Setup(r => r.GetTraineePlanWithLogsAsync(plan.OrderDetailsId ?? Guid.Empty, traineeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(plan);

        // Act
        var result = await _service.ToggleWorkoutCompletionAsync(traineeId, dto);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsCompleted);
    }

    [Fact]
    public async Task UTCID10_ToggleWorkoutCompletionAsync_OrderStatusIsRefundPending_AllowsMarking()
    {
        // Arrange
        var traineeId = Guid.NewGuid();
        var businessToday = TrainingPlanService.GetBusinessToday();
        var (plan, exercise) = CreateExerciseHierarchy(traineeId, planStartDate: businessToday, orderStatus: "REFUND_PENDING");

        var dto = new ToggleWorkoutCompletionDto
        {
            TrainingPlanExerciseId = exercise.TrainingPlanExerciseId,
            Status = "COMPLETED"
        };

        _mockPlanRepo.Setup(r => r.GetExerciseByIdAsync(exercise.TrainingPlanExerciseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(exercise);
        _mockPlanRepo.Setup(r => r.GetWorkoutLogAsync(exercise.TrainingPlanExerciseId, traineeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkoutCompletionLog?)null);
        _mockPlanRepo.Setup(r => r.GetTraineePlanWithLogsAsync(plan.OrderDetailsId ?? Guid.Empty, traineeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(plan);

        // Act
        var result = await _service.ToggleWorkoutCompletionAsync(traineeId, dto);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsCompleted);
    }

    #endregion

    #region Boundary Test Cases (UTCID11 - UTCID18)

    [Fact]
    public async Task UTCID11_ToggleWorkoutCompletionAsync_NotesLengthExactly300Chars_Succeeds()
    {
        // Arrange
        var traineeId = Guid.NewGuid();
        var businessToday = TrainingPlanService.GetBusinessToday();
        var (plan, exercise) = CreateExerciseHierarchy(traineeId, planStartDate: businessToday);
        var exact300Note = new string('A', 300);

        var dto = new ToggleWorkoutCompletionDto
        {
            TrainingPlanExerciseId = exercise.TrainingPlanExerciseId,
            Notes = exact300Note
        };

        _mockPlanRepo.Setup(r => r.GetExerciseByIdAsync(exercise.TrainingPlanExerciseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(exercise);
        _mockPlanRepo.Setup(r => r.GetWorkoutLogAsync(exercise.TrainingPlanExerciseId, traineeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkoutCompletionLog?)null);
        _mockPlanRepo.Setup(r => r.GetTraineePlanWithLogsAsync(plan.OrderDetailsId ?? Guid.Empty, traineeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(plan);

        // Act
        var result = await _service.ToggleWorkoutCompletionAsync(traineeId, dto);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(300, result.Notes?.Length);
    }

    [Fact]
    public async Task UTCID12_ToggleWorkoutCompletionAsync_NotesLength301Chars_ThrowsValidationException()
    {
        // Arrange
        var traineeId = Guid.NewGuid();
        var exerciseId = Guid.NewGuid();
        var exceed300Note = new string('B', 301);

        var dto = new ToggleWorkoutCompletionDto
        {
            TrainingPlanExerciseId = exerciseId,
            Notes = exceed300Note
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            _service.ToggleWorkoutCompletionAsync(traineeId, dto));

        Assert.Equal("Notes cannot exceed 300 characters.", ex.Message);
    }

    [Fact]
    public async Task UTCID13_ToggleWorkoutCompletionAsync_NotesEmptyStringOrNull_Succeeds()
    {
        // Arrange
        var traineeId = Guid.NewGuid();
        var businessToday = TrainingPlanService.GetBusinessToday();
        var (plan, exercise) = CreateExerciseHierarchy(traineeId, planStartDate: businessToday);

        var dto = new ToggleWorkoutCompletionDto
        {
            TrainingPlanExerciseId = exercise.TrainingPlanExerciseId,
            Notes = "" // Empty string boundary
        };

        _mockPlanRepo.Setup(r => r.GetExerciseByIdAsync(exercise.TrainingPlanExerciseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(exercise);
        _mockPlanRepo.Setup(r => r.GetWorkoutLogAsync(exercise.TrainingPlanExerciseId, traineeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkoutCompletionLog?)null);
        _mockPlanRepo.Setup(r => r.GetTraineePlanWithLogsAsync(plan.OrderDetailsId ?? Guid.Empty, traineeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(plan);

        // Act
        var result = await _service.ToggleWorkoutCompletionAsync(traineeId, dto);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsCompleted);
    }

    [Fact]
    public async Task UTCID14_ToggleWorkoutCompletionAsync_ExerciseScheduledForTomorrow_ThrowsValidationException()
    {
        // Arrange (DayNumber 2 makes scheduledDate = businessToday + 1 day)
        var traineeId = Guid.NewGuid();
        var businessToday = TrainingPlanService.GetBusinessToday();
        var (plan, exercise) = CreateExerciseHierarchy(traineeId, planStartDate: businessToday, dayNumber: 2);

        var dto = new ToggleWorkoutCompletionDto
        {
            TrainingPlanExerciseId = exercise.TrainingPlanExerciseId
        };

        _mockPlanRepo.Setup(r => r.GetExerciseByIdAsync(exercise.TrainingPlanExerciseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(exercise);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            _service.ToggleWorkoutCompletionAsync(traineeId, dto));

        Assert.Contains("You can only mark exercises scheduled for today", ex.Message);
    }

    [Fact]
    public async Task UTCID15_ToggleWorkoutCompletionAsync_ExerciseScheduledForYesterday_ThrowsValidationException()
    {
        // Arrange (Plan started 2 days ago, DayNumber 1 makes scheduledDate = 2 days ago)
        var traineeId = Guid.NewGuid();
        var businessToday = TrainingPlanService.GetBusinessToday();
        var (plan, exercise) = CreateExerciseHierarchy(traineeId, planStartDate: businessToday.AddDays(-2), dayNumber: 1);

        var dto = new ToggleWorkoutCompletionDto
        {
            TrainingPlanExerciseId = exercise.TrainingPlanExerciseId
        };

        _mockPlanRepo.Setup(r => r.GetExerciseByIdAsync(exercise.TrainingPlanExerciseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(exercise);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            _service.ToggleWorkoutCompletionAsync(traineeId, dto));

        Assert.Contains("You can only mark exercises scheduled for today", ex.Message);
    }

    [Fact]
    public async Task UTCID16_ToggleWorkoutCompletionAsync_EmptyTraineeId_ThrowsForbiddenException()
    {
        // Arrange
        var emptyTraineeId = Guid.Empty;
        var dto = new ToggleWorkoutCompletionDto
        {
            TrainingPlanExerciseId = Guid.NewGuid()
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ForbiddenException>(() =>
            _service.ToggleWorkoutCompletionAsync(emptyTraineeId, dto));

        Assert.Equal("Invalid trainee identity.", ex.Message);
    }

    [Fact]
    public async Task UTCID17_ToggleWorkoutCompletionAsync_EmptyTrainingPlanExerciseId_ThrowsValidationException()
    {
        // Arrange
        var traineeId = Guid.NewGuid();
        var dto = new ToggleWorkoutCompletionDto
        {
            TrainingPlanExerciseId = Guid.Empty
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            _service.ToggleWorkoutCompletionAsync(traineeId, dto));

        Assert.Equal("TrainingPlanExerciseId is required.", ex.Message);
    }

    [Fact]
    public async Task UTCID18_ToggleWorkoutCompletionAsync_NullDto_ThrowsValidationException()
    {
        // Arrange
        var traineeId = Guid.NewGuid();

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            _service.ToggleWorkoutCompletionAsync(traineeId, null!));

        Assert.Equal("TrainingPlanExerciseId is required.", ex.Message);
    }

    #endregion

    #region Abnormal Test Cases (UTCID19 - UTCID24)

    [Fact]
    public async Task UTCID19_ToggleWorkoutCompletionAsync_ExerciseNotFound_ThrowsNotFoundException()
    {
        // Arrange
        var traineeId = Guid.NewGuid();
        var nonExistentExerciseId = Guid.NewGuid();
        var dto = new ToggleWorkoutCompletionDto
        {
            TrainingPlanExerciseId = nonExistentExerciseId
        };

        _mockPlanRepo.Setup(r => r.GetExerciseByIdAsync(nonExistentExerciseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TrainingPlanExercise?)null);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.ToggleWorkoutCompletionAsync(traineeId, dto));

        Assert.Equal("Exercise not found.", ex.Message);
    }

    [Fact]
    public async Task UTCID20_ToggleWorkoutCompletionAsync_UnauthorizedTrainee_ThrowsForbiddenException()
    {
        // Arrange (Order belongs to another trainee)
        var actualTraineeId = Guid.NewGuid();
        var maliciousRequesterId = Guid.NewGuid();
        var businessToday = TrainingPlanService.GetBusinessToday();
        var (plan, exercise) = CreateExerciseHierarchy(actualTraineeId, planStartDate: businessToday);

        var dto = new ToggleWorkoutCompletionDto
        {
            TrainingPlanExerciseId = exercise.TrainingPlanExerciseId
        };

        _mockPlanRepo.Setup(r => r.GetExerciseByIdAsync(exercise.TrainingPlanExerciseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(exercise);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ForbiddenException>(() =>
            _service.ToggleWorkoutCompletionAsync(maliciousRequesterId, dto));

        Assert.Equal("You are not authorized to mark this exercise.", ex.Message);
    }

    [Fact]
    public async Task UTCID21_ToggleWorkoutCompletionAsync_RefundedPackage_ThrowsForbiddenException()
    {
        // Arrange
        var traineeId = Guid.NewGuid();
        var businessToday = TrainingPlanService.GetBusinessToday();
        var (plan, exercise) = CreateExerciseHierarchy(traineeId, planStartDate: businessToday, orderStatus: "REFUNDED");

        var dto = new ToggleWorkoutCompletionDto
        {
            TrainingPlanExerciseId = exercise.TrainingPlanExerciseId
        };

        _mockPlanRepo.Setup(r => r.GetExerciseByIdAsync(exercise.TrainingPlanExerciseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(exercise);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ForbiddenException>(() =>
            _service.ToggleWorkoutCompletionAsync(traineeId, dto));

        Assert.Equal("This package has been refunded and its workout plan is no longer accessible.", ex.Message);
    }

    [Fact]
    public async Task UTCID22_ToggleWorkoutCompletionAsync_DisallowedOrderStatus_ThrowsForbiddenException()
    {
        // Arrange (Order status PENDING is not allowed for trainee workout access)
        var traineeId = Guid.NewGuid();
        var businessToday = TrainingPlanService.GetBusinessToday();
        var (plan, exercise) = CreateExerciseHierarchy(traineeId, planStartDate: businessToday, orderStatus: "PENDING");

        var dto = new ToggleWorkoutCompletionDto
        {
            TrainingPlanExerciseId = exercise.TrainingPlanExerciseId
        };

        _mockPlanRepo.Setup(r => r.GetExerciseByIdAsync(exercise.TrainingPlanExerciseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(exercise);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ForbiddenException>(() =>
            _service.ToggleWorkoutCompletionAsync(traineeId, dto));

        Assert.Contains("Workout plan is not accessible for order with status 'PENDING'", ex.Message);
    }

    [Fact]
    public async Task UTCID23_ToggleWorkoutCompletionAsync_AlreadyCompletedExerciseMarkedIncomplete_ThrowsValidationException()
    {
        // Arrange
        var traineeId = Guid.NewGuid();
        var businessToday = TrainingPlanService.GetBusinessToday();
        var (plan, exercise) = CreateExerciseHierarchy(traineeId, planStartDate: businessToday);

        var existingLog = new WorkoutCompletionLog
        {
            WorkoutCompletionLogId = Guid.NewGuid(),
            TrainingPlanExerciseId = exercise.TrainingPlanExerciseId,
            TraineeId = traineeId,
            Status = "COMPLETED",
            CreatedAt = DateTime.UtcNow.AddHours(-1)
        };

        var dto = new ToggleWorkoutCompletionDto
        {
            TrainingPlanExerciseId = exercise.TrainingPlanExerciseId,
            Status = "INCOMPLETE" // Not allowed to uncomplete a completed exercise
        };

        _mockPlanRepo.Setup(r => r.GetExerciseByIdAsync(exercise.TrainingPlanExerciseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(exercise);
        _mockPlanRepo.Setup(r => r.GetWorkoutLogAsync(exercise.TrainingPlanExerciseId, traineeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingLog);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            _service.ToggleWorkoutCompletionAsync(traineeId, dto));

        Assert.Equal("Completed exercises cannot be uncompleted or marked incomplete.", ex.Message);
    }

    [Fact]
    public async Task UTCID24_ToggleWorkoutCompletionAsync_OperationCanceled_ThrowsOperationCanceledException()
    {
        // Arrange
        var traineeId = Guid.NewGuid();
        var exerciseId = Guid.NewGuid();
        var dto = new ToggleWorkoutCompletionDto
        {
            TrainingPlanExerciseId = exerciseId
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _mockPlanRepo.Setup(r => r.GetExerciseByIdAsync(exerciseId, cts.Token))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _service.ToggleWorkoutCompletionAsync(traineeId, dto, cts.Token));
    }

    #endregion
}
