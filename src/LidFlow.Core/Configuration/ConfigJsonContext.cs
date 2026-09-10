using System.Text.Json;
using System.Text.Json.Serialization;

namespace LidFlow.Core.Configuration;

/// <summary>
/// Source-generated JSON contract for the configuration tree.
/// <para>
/// Generated rather than reflection-based so the app stays trim- and AOT-compatible and so
/// serialization never has to JIT on a code path that could run during a transition.
/// </para>
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(LidFlowConfig))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext
{
}
