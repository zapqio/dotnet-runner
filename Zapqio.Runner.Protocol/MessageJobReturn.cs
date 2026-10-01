using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner.Protocol
{
    public class MessageJobReturn
    {
        public Guid Id { get; set; }

        /// <summary>Próba, której dotyczy ten wynik - <c>attemptId</c> z przydziału (§5.2).</summary>
        public Guid AttemptId { get; set; }

        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public Zapqio.Deployments.ExecutionVersion? ExecutionVersion { get; set; }

        public MessageResponseStatus Status { get; set; }
        public string Data { get; set; }
    }
}
