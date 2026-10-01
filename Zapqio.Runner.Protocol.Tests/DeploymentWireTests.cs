using System.Text.Json;
using System.Text.Json.Nodes;
using Zapqio.Deployments;
using Zapqio.Runner.Protocol.Enums;
namespace Zapqio.Runner.Protocol.Tests;

public class DeploymentWireTests
{
    public static TheoryData<string, Type, string> Cases => new()
    {
        { "Deployment", typeof(DeploymentNotice), "deploymentNotice" },
        { "DeploymentApproval", typeof(DeploymentApproval), "deploymentApproval" },
        { "DeploymentApprovalResult", typeof(DeploymentApprovalResult), "deploymentApprovalResult" },
        { "DeploymentStatus", typeof(DeploymentReport), "deploymentReport" },
        { "DeploymentStatusAck", typeof(DeploymentStatusAck), "deploymentStatusAck" },
    };
    [Theory]
    [MemberData(nameof(Cases))]
    public void Canonical_fixture_is_consumed_and_produced_with_the_documented_shape(string name, Type type, string definition)
    {
        var source = ProtocolRepo.Fixture("sr3-" + name.ToLowerInvariant() + ".json");
        var envelope = JsonSerializer.Deserialize<Message>(source, JsonDefaults.Options)!;
        Assert.Equal(name, envelope.Type.ToString());
        var payload = JsonSerializer.Deserialize(envelope.Data, type, JsonDefaults.Options)!;
        var serialized = JsonSerializer.Serialize(payload, type, JsonDefaults.Options);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(envelope.Data), JsonNode.Parse(serialized)));
        ProtocolRepo.Validate(definition, JsonNode.Parse(serialized), name);
        ProtocolRepo.Validate("message", JsonNode.Parse(source), name);
    }
    [Fact]
    public void Info_has_no_separate_deployment_version()
    {
        var json = JsonSerializer.Serialize(new MessageInfo { Name = "old", Methods = [] }, JsonDefaults.Options);
        Assert.DoesNotContain("deploymentProtocolVersion", json);
    }
}
