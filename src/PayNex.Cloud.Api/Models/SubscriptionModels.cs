namespace PayNex.Cloud.Api.Models;

public sealed record SubscriptionPostRequest(
    string CompanyCode,
    int SubscriptionPlanId,
    DateTime? StartDate,
    DateTime? ExpiryDate,
    decimal Amount,
    string? Currency,
    string? PaymentStatus,
    string? PaymentMethod,
    string? ReferenceNo,
    string? Notes);

public sealed record SubscriptionRenewRequest(
    int? SubscriptionPlanId,
    int? DurationMonths,
    decimal Amount,
    string? Currency,
    string? PaymentStatus,
    string? PaymentMethod,
    string? ReferenceNo,
    string? Notes);

public sealed record SubscriptionCancelRequest(
    DateTime? CancellationDate,
    string? Reason);
