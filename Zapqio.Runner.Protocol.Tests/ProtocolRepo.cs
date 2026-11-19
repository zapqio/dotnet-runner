using System.Text;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Zapqio.Runner.Protocol.Tests;

/// <summary>
/// Access to the language-neutral protocol repository (github.com/zapqio/protocol) that this
/// assembly is tested against. <c>schemas.json</c> and <c>fixtures/</c> are copied next to the test
/// binaries by the csproj; see the <c>ZapqioProtocolDir</c> property there.
/// </summary>
internal static class ProtocolRepo
{
    public const string SchemaId = "https://zapqio.dev/protocol/v4/schemas.json";

    public static string Dir { get; } = Path.Combine(AppContext.BaseDirectory, "protocol");

    public static string FixturesDir { get; } = Path.Combine(Dir, "fixtures");

    /// <summary>
    /// Every fixture file name, i.e. every canonical frame the protocol documents.
    ///
    /// Read from the directory rather than listed here: a hardcoded list would mean a fixture added
    /// to the protocol repository is validated by nothing while looking as if it were covered.
    /// Sorted so the test cases do not depend on the order the file system happens to return.
    /// </summary>
    public static readonly string[] AllFixtures = Directory.Exists(FixturesDir)
        ? Directory.EnumerateFiles(FixturesDir, "*.json")
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray()
        : [];

    /// <summary>The exact bytes of one canonical WebSocket text frame.</summary>
    public static string Fixture(string name)
    {
        var path = Path.Combine(FixturesDir, name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Protocol fixture '{name}' not found at '{path}'. The protocol repository must be " +
                "checked out next to this one (or pointed at with -p:ZapqioProtocolDir=<path>).", path);
        }

        return File.ReadAllText(path);
    }

    private static readonly Lazy<JsonSchema> Root = new(() =>
    {
        var path = Path.Combine(Dir, "schemas.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Protocol schemas.json not found at '{path}'. The protocol repository must be " +
                "checked out next to this one (or pointed at with -p:ZapqioProtocolDir=<path>).", path);
        }

        return JsonSchema.FromFile(path);
    });

    private static readonly Lazy<EvaluationOptions> Options = new(() =>
    {
        var options = new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
            RequireFormatValidation = true,
        };
        options.SchemaRegistry.Register(new Uri(SchemaId), Root.Value);
        return options;
    });

    /// <summary>The raw <c>$defs</c> object of schemas.json, for tests that read the spec's enums.</summary>
    public static JsonObject Defs { get; } =
        JsonNode.Parse(File.ReadAllText(Path.Combine(Dir, "schemas.json")))!.AsObject()["$defs"]!.AsObject();

    /// <summary>
    /// Validates <paramref name="instance"/> against <c>schemas.json#/$defs/<paramref name="def"/></c>
    /// and throws with the schema's own error locations when it does not conform.
    /// </summary>
    public static void Validate(string def, JsonNode? instance, string what)
    {
        var reference = JsonSchema.FromText($$"""{"$ref": "{{SchemaId}}#/$defs/{{def}}"}""");
        var result = reference.Evaluate(instance, Options.Value);
        if (result.IsValid)
        {
            return;
        }

        var message = new StringBuilder()
            .AppendLine($"{what} does not conform to schemas.json#/$defs/{def}:")
            .AppendLine(instance?.ToJsonString() ?? "null");
        foreach (var node in Flatten(result).Where(n => n.HasErrors))
        {
            foreach (var error in node.Errors!)
            {
                message.AppendLine($"  at {node.InstanceLocation} ({node.EvaluationPath}): {error.Key} {error.Value}");
            }
        }

        Assert.Fail(message.ToString());
    }

    private static IEnumerable<EvaluationResults> Flatten(EvaluationResults results)
    {
        yield return results;
        foreach (var child in results.Details.SelectMany(Flatten))
        {
            yield return child;
        }
    }
}
