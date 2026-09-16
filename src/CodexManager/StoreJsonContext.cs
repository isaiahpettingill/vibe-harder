using System.Text.Json.Serialization;

namespace CodexManager;

internal sealed record ChatSnapshot(string? SessionId, string Title, DateTimeOffset Updated, string Draft, bool Archived, AgentProvider Provider, PendingInput? PendingInput);

[JsonSerializable(typeof(Attachment[]))]
[JsonSerializable(typeof(PendingInput))]
[JsonSerializable(typeof(PendingInput[]))]
[JsonSerializable(typeof(ChatSnapshot))]
[JsonSerializable(typeof(SubagentInfo))]
internal partial class StoreJsonContext : JsonSerializerContext;
