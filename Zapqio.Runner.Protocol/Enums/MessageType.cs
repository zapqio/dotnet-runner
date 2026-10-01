namespace Zapqio.Runner.Protocol.Enums
{
    public enum MessageType
    {
        Job,
        /// <summary>Potwierdzenie odbioru przydziału (§5.3). Dodane w v2.</summary>
        JobAccepted,
        JobReturn,
        Log,
        Info,
        Deployment,
        DeploymentApproval,
        DeploymentApprovalResult,
        DeploymentStatus,
        DeploymentStatusAck
    }
}
