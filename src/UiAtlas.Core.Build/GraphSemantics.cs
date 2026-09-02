using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Build;

public static class GraphSemantics
{
    public static string ComputeHash(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges)
    {
        var canonical = JsonSerializer.Serialize(new { nodes, edges }, JsonDefaults.Options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
