using System.Buffers.Binary;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SandboxMcp.FullBroker;

internal sealed record Request(int Protocol, string Operation, string? Id = null, string? AgentName = null,
    string? AgentSid = null, string? ContainerId = null, string? Variant = null, GridMember[]? Members = null,
    string? StartupPacing = null);
internal sealed record GridMember(string Id, string AgentName, string AgentSid, string ContainerId, string Variant)
{
    public Request AsRequest() => new(1, "run-fixture", Id, AgentName, AgentSid, ContainerId, Variant);
}
internal sealed record Configuration(int Schema, string OwnerSid, string Release);
internal sealed record Lease(Request Request, string OutputKey, int[] Baseline,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? SessionId, string Phase,
    DateTimeOffset Created, JsonElement? Result = null, int? CleanupSessionId = null, DateTimeOffset? CleanupStartedAt = null);

internal static class Protocol
{
    public const int RequestLimit = 131072, ResponseLimit = 262144;
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 16,
        WriteIndented = true
    };

    public static Request Parse(byte[] bytes)
    {
        if (bytes.Length is 0 or > RequestLimit) throw new InvalidDataException("Invalid request length");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a request object");
        RejectDuplicates(document.RootElement);
        var properties = document.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        var request = JsonSerializer.Deserialize<Request>(bytes, Json) ?? throw new InvalidDataException("Empty request");
        if (request.Protocol != 1) throw new InvalidDataException("Unsupported protocol");
        string[] allowed = request.Operation switch
        {
            "status" or "recover" => ["protocol", "operation"],
            "result" or "ack" => ["protocol", "operation", "id"],
            "run-fixture" => ["protocol", "operation", "id", "agentName", "agentSid", "containerId", "variant"],
            "run-grid" => ["protocol", "operation", "members", "startupPacing"],
            _ => throw new InvalidDataException("Unsupported operation")
        };
        if (properties.Any(property => !allowed.Contains(property, StringComparer.Ordinal)))
            throw new InvalidDataException("Unexpected operation field");
        if (request.Operation == "run-grid")
        {
            StartupPacing.DelayMs(request.StartupPacing);
            if (request.Members is not { Length: >= 1 and <= VisualWorkloads.MaxSessions })
                throw new InvalidDataException("Grid requires one to sixteen fixed workloads");
            foreach (var member in request.Members)
            {
                if (member is null) throw new InvalidDataException("Missing grid member");
                Parse(JsonSerializer.SerializeToUtf8Bytes(member.AsRequest(), Json));
            }
            int count = request.Members.Length;
            bool calculatorGrid = request.Members.All(member => VisualWorkloads.CalculatorNumber(member.Variant) > 0);
            if (request.Members.Select(member => member.Id).Distinct().Count() != count
                || request.Members.Select(member => member.AgentSid).Distinct().Count() != count
                || request.Members.Select(member => member.AgentName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != count
                || request.Members.Select(member => member.Variant).Distinct().Count() != count
                || !calculatorGrid && !request.Members.Select(member => member.Variant).ToHashSet().SetEquals(["light", "dark", "dark-large"]))
                throw new InvalidDataException("Grid member identities and variants must be unique");
        }
        if (request.Operation is "run-fixture" or "result" or "ack") RequireId(request.Id);
        if (request.Operation == "run-fixture")
        {
            if (!VisualWorkloads.IsAllowed(request.Variant))
                throw new InvalidDataException("Unsupported fixed fixture variant");
            if (request.AgentName is null || !Regex.IsMatch(request.AgentName, "^[A-Za-z0-9][A-Za-z0-9-]{0,31}$"))
                throw new InvalidDataException("Invalid agent name");
            if (request.AgentSid is null || request.AgentSid.Length > 184)
                throw new InvalidDataException("Missing agent SID");
            var sid = new SecurityIdentifier(request.AgentSid);
            if (sid.AccountDomainSid is null) throw new InvalidDataException("Expected an account SID");
            if (string.IsNullOrWhiteSpace(request.ContainerId) || request.ContainerId.Length > 4096)
                throw new InvalidDataException("Invalid opaque container ID");
        }
        return request;
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate request property");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicates(child);
    }

    public static void RequireId(string? id)
    {
        if (id is null || !Guid.TryParseExact(id, "N", out _) || id != id.ToLowerInvariant())
            throw new InvalidDataException("Expected a lowercase request GUID without separators");
    }

    public static async Task<byte[]> Read(Stream stream, int limit, CancellationToken token)
    {
        byte[] prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, token);
        int size = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (size <= 0 || size > limit) throw new InvalidDataException("Frame exceeds the protocol limit");
        byte[] bytes = new byte[size];
        await stream.ReadExactlyAsync(bytes, token);
        return bytes;
    }

    public static async Task Write(Stream stream, object message, int limit, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (bytes.Length > limit) throw new InvalidDataException("Response exceeds the protocol limit");
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }
}
