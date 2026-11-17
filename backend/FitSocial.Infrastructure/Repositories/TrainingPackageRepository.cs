using FitSocial.Domain.Entities;
using FitSocial.Domain.Interfaces;
using FitSocial.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FitSocial.Infrastructure.Repositories
{
    public class TrainingPackageRepository : Repository<TrainingPackage>, ITrainingPackageRepository
    {
        public TrainingPackageRepository(FitSocialDbContext dbContext) : base(dbContext)
        {
        }

        public async Task<IEnumerable<TrainingPackage>> GetPackagesByCoachIdAsync(Guid coachId, CancellationToken cancellationToken = default)
        {
            return await DbSet
                .Include(x => x.Coach)
                    .ThenInclude(c => c.Coach)
                .Include(x => x.Media)
                .Where(x => x.CoachId == coachId)
                .AsNoTracking()
                .ToListAsync(cancellationToken);
        }

        public override async Task<TrainingPackage?> GetByIdAsync(object id, CancellationToken cancellationToken = default)
        {
            return await DbSet
                .Include(x => x.Coach)
                    .ThenInclude(c => c.Coach)
                .Include(x => x.Coach)
                    .ThenInclude(c => c.CoachCertificates)
                .Include(x => x.Media)
                .FirstOrDefaultAsync(x => x.PackageId == (Guid)id, cancellationToken);
        }

        public async Task<IEnumerable<TrainingPackage>> GetAllActivePackagesAsync(
            string? searchKeyword = null,
            decimal? maxPrice = null,
            Guid? coachId = null,
            CancellationToken cancellationToken = default)
        {
            var query = DbSet
                .Include(x => x.Coach)
                    .ThenInclude(c => c.Coach)
                .Include(x => x.Media)
                .Where(x => x.IsActive == true);

            if (coachId.HasValue && coachId.Value != Guid.Empty)
            {
                query = query.Where(x => x.CoachId == coachId.Value);
            }

            if (maxPrice.HasValue && maxPrice.Value > 0)
            {
                query = query.Where(x => x.Price <= maxPrice.Value);
            }

            if (!string.IsNullOrWhiteSpace(searchKeyword))
            {
                var term = searchKeyword.Trim().ToLower();
                query = query.Where(x => (x.Title != null && x.Title.ToLower().Contains(term)) ||
                                         (x.Description != null && x.Description.ToLower().Contains(term)) ||
                                         (x.TargetAudience != null && x.TargetAudience.ToLower().Contains(term)) ||
                                         (x.Coach != null && x.Coach.Coach != null && x.Coach.Coach.FullName != null && x.Coach.Coach.FullName.ToLower().Contains(term)));
            }

            return await query
                .OrderByDescending(x => x.CreatedAt)
                .AsNoTracking()
                .ToListAsync(cancellationToken);
        }

        public async Task<List<TrainingPackage>> GetPurchasedPackagesAsync(Guid traineeId, CancellationToken cancellationToken = default)
        {
            var paidStatuses = new[]
            {
                FitSocial.Domain.Constants.PaymentConstants.OrderStatusPaid,
                "PAID",
                "ACTIVE",
                "COMPLETED",
                FitSocial.Domain.Constants.PaymentConstants.OrderStatusRefundPending,
                "REFUND_PENDING",
                FitSocial.Domain.Constants.PaymentConstants.OrderStatusRefunded,
                "REFUNDED"
            };

            return await DbSet
                .Include(x => x.Coach)
                    .ThenInclude(c => c.Coach)
                .Include(x => x.Media)
                .Include(x => x.Reviews.Where(r => r.TraineeId == traineeId))
                .Include(x => x.OrderDetails.Where(od => od.Order.BuyerId == traineeId))
                    .ThenInclude(od => od.Order)
                        .ThenInclude(o => o.RefundRequests)
                .Include(x => x.OrderDetails.Where(od => od.Order.BuyerId == traineeId))
                    .ThenInclude(od => od.TrainingPlans)
                .Include(x => x.OrderDetails.Where(od => od.Order.BuyerId == traineeId))
                    .ThenInclude(od => od.MealPlans)
                .Where(x => x.OrderDetails.Any(od =>
                    od.Order.BuyerId == traineeId &&
                    od.Order.OrderStatus != null &&
                    paidStatuses.Contains(od.Order.OrderStatus.ToUpper())))
                .AsNoTracking()
                .AsSplitQuery()
                .ToListAsync(cancellationToken);
        }

        public Task<(List<OrderDetail> Items, int TotalCount)> GetPurchasedOrderDetailsAsync(
            Guid traineeId,
            int pageNumber,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            return GetPurchasedOrderDetailsAsync(
                traineeId,
                pageNumber,
                pageSize,
                searchKeyword: null,
                packageType: null,
                status: null,
                sortBy: null,
                cancellationToken: cancellationToken);
        }

        public async Task<(List<OrderDetail> Items, int TotalCount)> GetPurchasedOrderDetailsAsync(
            Guid traineeId,
            int pageNumber,
            int pageSize,
            string? searchKeyword,
            string? packageType = null,
            string? status = null,
            string? sortBy = null,
            CancellationToken cancellationToken = default)
        {
            if (pageNumber < 1) pageNumber = 1;
            if (pageSize < 1) pageSize = 10;

            var paidStatuses = new[]
            {
                FitSocial.Domain.Constants.PaymentConstants.OrderStatusPaid,
                "PAID",
                "ACTIVE",
                "COMPLETED",
                FitSocial.Domain.Constants.PaymentConstants.OrderStatusRefundPending,
                "REFUND_PENDING",
                FitSocial.Domain.Constants.PaymentConstants.OrderStatusRefunded,
                "REFUNDED"
            };

            var query = DbContext.Set<OrderDetail>()
                .Include(od => od.Order)
                    .ThenInclude(o => o.RefundRequests)
                .Include(od => od.Package)
                    .ThenInclude(p => p!.Coach)
                        .ThenInclude(c => c.Coach)
                .Include(od => od.Package)
                    .ThenInclude(p => p!.Coach)
                        .ThenInclude(c => c.CoachCertificates)
                .Include(od => od.Package)
                    .ThenInclude(p => p!.Media)
                .Include(od => od.Package)
                    .ThenInclude(p => p!.Reviews.Where(r => r.TraineeId == traineeId))
                        .ThenInclude(r => r.ReviewMedia)
                .Include(od => od.TrainingPlans)
                .Include(od => od.MealPlans)
                .Where(od => od.PackageId != null
                             && od.Package != null
                             && od.Order.BuyerId == traineeId
                             && (od.Order.OrderType == null || od.Order.OrderType == FitSocial.Domain.Constants.PaymentConstants.OrderTypePackage)
                             && od.Order.OrderStatus != null
                             && paidStatuses.Contains(od.Order.OrderStatus.ToUpper()));

            if (!string.IsNullOrWhiteSpace(searchKeyword))
            {
                var term = searchKeyword.Trim().ToLower();
                query = query.Where(od =>
                    (od.PackageTitle != null && od.PackageTitle.ToLower().Contains(term)) ||
                    (od.Package != null && od.Package.Title != null && od.Package.Title.ToLower().Contains(term)) ||
                    (od.CoachName != null && od.CoachName.ToLower().Contains(term)) ||
                    (od.Package != null && od.Package.Coach != null && od.Package.Coach.Coach != null &&
                     od.Package.Coach.Coach.FullName != null && od.Package.Coach.Coach.FullName.ToLower().Contains(term)));
            }

            if (!string.IsNullOrWhiteSpace(packageType) && !string.Equals(packageType.Trim(), "ALL", StringComparison.OrdinalIgnoreCase))
            {
                var normalizedType = packageType.Trim().ToUpperInvariant();
                query = query.Where(od =>
                    ((od.PackageType ?? (od.Package != null ? od.Package.PackageType : null) ?? "BOTH").ToUpper()) == normalizedType);
            }

            var allMatching = await query
                .OrderByDescending(od => od.CreatedAt)
                .ThenByDescending(od => od.OrderDetailsId)
                .AsNoTracking()
                .AsSplitQuery()
                .ToListAsync(cancellationToken);

            IEnumerable<OrderDetail> filtered = allMatching;

            if (!string.IsNullOrWhiteSpace(status) && !string.Equals(status.Trim(), "ALL", StringComparison.OrdinalIgnoreCase))
            {
                var normalizedStatus = status.Trim().ToUpperInvariant();
                filtered = filtered.Where(od => string.Equals(ResolveEnrollmentStatus(od), normalizedStatus, StringComparison.OrdinalIgnoreCase));
            }

            var normalizedSort = string.IsNullOrWhiteSpace(sortBy) ? "NEWEST" : sortBy.Trim().ToUpperInvariant();
            filtered = normalizedSort switch
            {
                "OLDEST" => filtered
                    .OrderBy(od => od.Order?.CreatedAt ?? od.CreatedAt ?? DateTime.MinValue)
                    .ThenBy(od => od.OrderDetailsId),
                "EXPIRING_SOON" => filtered
                    .OrderBy(od => ResolveEnrollmentStatus(od) == "ACTIVE" ? 0 : 1)
                    .ThenBy(od => ResolveExpiresAt(od) ?? DateTime.MaxValue)
                    .ThenByDescending(od => od.OrderDetailsId),
                "PRICE_DESC" => filtered
                    .OrderByDescending(od => od.PackagePrice ?? od.Package?.Price ?? 0m)
                    .ThenByDescending(od => od.CreatedAt),
                "PRICE_ASC" => filtered
                    .OrderBy(od => od.PackagePrice ?? od.Package?.Price ?? 0m)
                    .ThenByDescending(od => od.CreatedAt),
                _ => filtered
                    .OrderByDescending(od => od.Order?.CreatedAt ?? od.CreatedAt ?? DateTime.MinValue)
                    .ThenByDescending(od => od.OrderDetailsId)
            };

            var materialized = filtered.ToList();
            var totalCount = materialized.Count;
            var items = materialized
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            return (items, totalCount);
        }

        private static DateTime? ResolveExpiresAt(OrderDetail od)
        {
            var startDate = od.Order?.CreatedAt ?? od.CreatedAt;
            var durationDays = od.PackageDurationDays ?? od.Package?.DurationDays ?? 30;
            if (!startDate.HasValue || durationDays <= 0) return null;
            return DateTime.SpecifyKind(startDate.Value, DateTimeKind.Utc).AddDays(durationDays);
        }

        private static string ResolveEnrollmentStatus(OrderDetail od)
        {
            var orderStatus = od.Order?.OrderStatus?.Trim().ToUpperInvariant();
            var latestRefund = od.Order?.RefundRequests?
                .OrderByDescending(r => r.RequestedAt ?? r.CreatedAt)
                .FirstOrDefault();
            var refundStatus = latestRefund?.Status?.Trim().ToUpperInvariant();

            if (refundStatus == "APPROVED" || orderStatus == "REFUNDED")
                return "REFUNDED";

            if (refundStatus == "PENDING" || orderStatus == "REFUND_PENDING")
                return "REFUND_PENDING";

            var pkgType = (od.PackageType ?? od.Package?.PackageType ?? "BOTH").Trim().ToUpperInvariant();
            var latestPlan = od.TrainingPlans?.OrderByDescending(p => p.CreatedAt).FirstOrDefault();
            var latestMeal = od.MealPlans?.OrderByDescending(m => m.CreatedAt).FirstOrDefault();
            var planCompleted = string.Equals(latestPlan?.Status?.Trim(), "COMPLETED", StringComparison.OrdinalIgnoreCase);
            var mealCompleted = string.Equals(latestMeal?.Status?.Trim(), "COMPLETED", StringComparison.OrdinalIgnoreCase);

            if ((pkgType == "WORKOUT" && planCompleted) ||
                (pkgType == "MEAL" && mealCompleted) ||
                (pkgType == "BOTH" && planCompleted && (latestMeal == null || mealCompleted)))
            {
                return "COMPLETED";
            }

            var expiresAt = ResolveExpiresAt(od);
            if (expiresAt.HasValue && DateTime.UtcNow >= expiresAt.Value)
                return "EXPIRED";

            return "ACTIVE";
        }
    }
}
