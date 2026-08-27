using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SB.Core
{
    [JsonSerializable(typeof(System.Boolean))]
    [JsonSerializable(typeof(DateTime))]
    [JsonSerializable(typeof(CompileCommand))]
    [JsonSerializable(typeof(DependencyRecord))]
    [JsonSerializable(typeof(SB.Core.OptimizationLevel))]
    [JsonSerializable(typeof(SB.Core.TargetType))]
    [JsonSerializable(typeof(SB.Core.SIMDArchitecture))]
    [JsonSerializable(typeof(SB.Core.FpModel))]
    [JsonSerializable(typeof(CLDependenciesData))]
    [JsonSerializable(typeof(CLDependencies))]
    [JsonSerializable(typeof(ArgumentList<string>))]
    [JsonSerializable(typeof(Dictionary<string, IReadOnlySet<string>>))]
    [JsonSerializable(typeof(IReadOnlyDictionary<string, object>))]
    [JsonSerializable(typeof(List<string>))]
    [JsonSerializable(typeof(List<KeyValuePair<string, DateTime>>))]
    internal partial class JsonContext : JsonSerializerContext
    {

    }

    public static class Json
    {
        public static TValue? Deserialize<TValue>(string json)
        {
            var sourceGenOptions = new JsonSerializerOptions
            {
                TypeInfoResolver = JsonContext.Default,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            return JsonSerializer.Deserialize<TValue>(json, sourceGenOptions);
        }

        public static string Serialize<TValue>(TValue value)
        {
            var sourceGenOptions = new JsonSerializerOptions
            {
                TypeInfoResolver = JsonContext.Default,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            return JsonSerializer.Serialize(value, typeof(TValue), sourceGenOptions);
        }
    }
}
