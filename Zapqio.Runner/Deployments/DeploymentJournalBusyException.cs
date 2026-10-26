namespace Zapqio.Runner.Deployments;

// Only failure to acquire the lock is retryable by the reporting loop. A corrupt
// journal or an error after acquiring the lock must not be mistaken for contention.
public sealed class DeploymentJournalBusyException(string lockPath, IOException innerException)
    : IOException($"Deployment journal lock could not be acquired within 10 seconds: {lockPath}", innerException);
