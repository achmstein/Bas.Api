using Bas.Api.Contracts.Bas;
using Bas.Api.Data;
using Bas.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bas.Api.Statements;

/// <summary>
/// Reads and writes the worker's own identity — the details Practice Manager needs before it will
/// create a client.
/// </summary>
public sealed class WorkerIdentityService(
    BasDbContext db,
    TimeProvider timeProvider,
    ILogger<WorkerIdentityService> logger)
{

    public async Task<WorkerIdentityResponse?> GetAsync(
        Guid workerId, string partnerId, CancellationToken cancellationToken)
    {
        var worker = await db.Workers
            .AsNoTracking()
            .SingleOrDefaultAsync(w => w.Id == workerId, cancellationToken);

        return worker is null ? null : ToResponse(worker, partnerId);
    }

    public async Task<(WorkerIdentityResponse? Identity, BasError? Error)> SaveAsync(
        Guid workerId, string partnerId, WorkerIdentityRequest request, CancellationToken cancellationToken)
    {
        // Validated here rather than at the push. Practice Manager creates a client in two calls
        // and only the second validates the TFN, so a bad one leaves a fully-created client behind
        // - and the reconciler retrying means every attempt orphans another in the live practice.
        if (!TfnValidator.IsValid(request.Tfn, out var tfnReason))
        {
            return (null, new BasError(
                StatusCodes.Status400BadRequest,
                "Invalid TFN",
                // tfnReason never contains the TFN itself.
                $"The tax file number is not valid: {tfnReason}."));
        }

        var abn = AbnValidator.Normalise(request.Abn);
        if (!string.IsNullOrEmpty(abn) && !AbnValidator.IsValid(abn, out var abnReason))
            return (null, new BasError(StatusCodes.Status400BadRequest, "Invalid ABN", $"The ABN is not valid: {abnReason}."));

        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        if (request.DateOfBirth >= today)
            return (null, new BasError(StatusCodes.Status400BadRequest, "Invalid date of birth", "Date of birth must be in the past."));

        if (request.DateOfBirth < today.AddYears(-120))
            return (null, new BasError(StatusCodes.Status400BadRequest, "Invalid date of birth", "Date of birth is implausibly early."));

        var worker = await db.Workers.SingleOrDefaultAsync(w => w.Id == workerId, cancellationToken);
        if (worker is null)
            return (null, new BasError(StatusCodes.Status404NotFound, "Unknown worker", "No such worker."));

        var tfn = TfnValidator.Normalise(request.Tfn);

        worker.Tfn = tfn;
        worker.TfnLast3 = tfn[^3..];
        worker.Abn = string.IsNullOrEmpty(abn) ? null : abn;
        worker.FirstName = request.FirstName.Trim();
        worker.FamilyName = request.FamilyName.Trim();
        worker.DateOfBirth = request.DateOfBirth;
        worker.Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
        worker.Phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim();
        worker.UpdatedAt = timeProvider.GetUtcNow();

        await db.SaveChangesAsync(cancellationToken);

        // No identifying detail in the log line - that a worker's identity changed is worth
        // recording, what it changed to is not.
        logger.LogInformation(
            "Worker {WorkerId} identity updated by partner {PartnerId}.", workerId, partnerId);

        return (ToResponse(worker, partnerId), null);
    }

    /// <summary>
    /// The TFN in full, for the Practice Manager push and nothing else. Never call this on a path
    /// that produces a response or a log line - those get <c>TfnMasked</c>.
    /// </summary>
    public static string? RevealTfn(Worker worker) => worker.Tfn;

    private static WorkerIdentityResponse ToResponse(Worker worker, string partnerId) => new()
    {
        WorkerId = worker.Id,
        PartnerId = partnerId,
        // Built from the stored last three digits, so a response is never assembled from the TFN.
        TfnMasked = worker.TfnLast3 is null ? null : $"******{worker.TfnLast3}",
        Abn = worker.Abn,
        FirstName = worker.FirstName,
        FamilyName = worker.FamilyName,
        DateOfBirth = worker.DateOfBirth,
        Email = worker.Email,
        Phone = worker.Phone,
        IsCompleteForLodgement = worker.IsCompleteForLodgement
    };
}
