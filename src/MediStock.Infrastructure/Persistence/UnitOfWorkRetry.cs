using MediStock.Application.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MediStock.Infrastructure.Persistence;

/// <summary>
/// Runs a unit of work as a single transaction and repeats the whole of it when a competing writer
/// changed a row it read.
/// </summary>
/// <remarks>
/// <para>
/// A repeated attempt has committed nothing, so the unit of work is re-run from a cleared change tracker
/// rather than resumed half-built: the caller's lambda does that itself. Only
/// <see cref="DbUpdateConcurrencyException"/> is repeated, and only a bounded number of times, because it
/// means the row changed underneath a write that expected the value it read. Every other failure is
/// returned to the caller, whose own <c>catch</c> clauses turn it into an operator-visible message.
/// </para>
/// <para>
/// The attempt is wrapped in the connection's execution strategy, which is what lets a transient
/// connection fault be retried without abandoning the transaction, and also what makes a user
/// transaction legal to start at all when <c>EnableRetryOnFailure</c> is configured. The pause between
/// attempts grows with the attempt number so contending writers drift apart instead of colliding again
/// in lockstep. See <c>docs/decisions/0006-context-lifetime-and-execution-strategy.md</c>.
/// </para>
/// </remarks>
internal static class UnitOfWorkRetry
{
    /// <summary>How many times a unit of work may be repeated when it is overtaken by another writer.</summary>
    internal const int Attempts = 8;

    /// <summary>
    /// Runs <paramref name="unitOfWork"/> until it succeeds, fails on its own terms, or is overtaken
    /// <see cref="Attempts"/> times.
    /// </summary>
    /// <param name="exhaustedMessage">
    /// What the operator is told when every attempt was overtaken. Worded by the caller, because losing a
    /// checkout race and losing a return race need different advice.
    /// </param>
    internal static async Task<OperationResult<T>> ExecuteAsync<T>(
        MediStockDbContext db,
        Func<Task<OperationResult<T>>> unitOfWork,
        string exhaustedMessage,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(logger);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await db.Database
                    .CreateExecutionStrategy()
                    .ExecuteAsync(unitOfWork)
                    .ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < Attempts)
            {
                logger.LogInformation(
                    ex,
                    "Attempt {Attempt} of {Attempts} was overtaken by another writer and will be repeated.",
                    attempt,
                    Attempts);

                await Task.Delay(
                    TimeSpan.FromMilliseconds(Random.Shared.Next(5, 20) * attempt),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                logger.LogWarning(
                    ex,
                    "Another writer changed the records this operation read {Attempts} times in a row.",
                    Attempts);

                return OperationResult<T>.Fail(exhaustedMessage);
            }
        }
    }
}
