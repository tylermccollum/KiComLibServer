using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace KiComLibBackend.Services.Reviews;

public interface IReviewValidator
{
    string PolicyId { get; }
    Task<IReadOnlyList<ReviewFinding>> Validate(JsonObject body, string digest, IReadOnlyCollection<string> blobs, CancellationToken ct);
}

public sealed class ReviewValidator(ReviewDatabase db) : IReviewValidator
{
    public string PolicyId => "kicomlib-review/1";
    public async Task<IReadOnlyList<ReviewFinding>> Validate(JsonObject body, string digest, IReadOnlyCollection<string> blobs, CancellationToken ct)
    {
        List<ReviewFinding> findings = [];
        if (body["schemaVersion"]?.GetValue<int>() != 1 || ReviewJson.Digest(ReviewJson.Canonical(body)) != digest)
            findings.Add(new("definition.integrity", "error", "definition", "Unsupported schema or mismatched definition digest"));
        var referenced = new HashSet<string>();
        void Visit(JsonNode? node)
        {
            if (node is JsonObject obj)
                foreach (var (key, value) in obj)
                {
                    if (key == "blobHash" && value != null) referenced.Add(value.GetValue<string>());
                    Visit(value);
                }
            else if (node is JsonArray array) foreach (var value in array) Visit(value);
        }
        Visit(body);
        if (!referenced.SetEquals(blobs)) findings.Add(new("definition.retention", "error", "blobs", "Retained blob references do not match the definition"));
        foreach (string hash in referenced)
        {
            byte[]? data = await db.Blob(hash, ct);
            if (data == null || Convert.ToHexStringLower(SHA256.HashData(data)) != hash)
                findings.Add(new("blob.integrity", "error", "blobs/" + hash, "Missing blob or SHA-256 mismatch"));
        }
        if (body["primarySource"] == null) findings.Add(new("part.mpn", "warning", "primarySource", "No manufacturer part number is selected"));
        if (body["symbol"] == null) findings.Add(new("part.symbol", "warning", "symbol", "No symbol is selected"));
        if (body["footprint"] == null) findings.Add(new("part.footprint", "warning", "footprint", "No footprint is selected"));
        return findings;
    }
}
