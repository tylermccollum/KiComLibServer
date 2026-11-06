using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace KiComLibBackend.Services.Reviews;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AssetVerificationRequest(string Action, string DefinitionDigest, int ExpectedStateVersion,
    Guid RequestUuid, string Note, List<string>? ReferenceUrls = null);

public sealed class AssetVerificationService(ReviewDatabase db, ReviewDefinitionBuilder builder)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public Task<T> Snapshot<T>(Func<Task<T>> read, CancellationToken ct) => db.Snapshot(read, ct);

    public static void Kind(string kind)
    {
        if (kind is not ("symbol" or "footprint")) throw new ReviewException(400, "kind must be symbol or footprint");
    }
    private Task<JsonObject?> State(string kind, Guid uuid, CancellationToken ct) => db.Row(
        "SELECT to_jsonb(s)::text FROM public.asset_verification_state s WHERE kind=@kind AND asset_uuid=@uuid", ct, ("kind", kind), ("uuid", uuid));
    private static int Version(JsonObject? state) => state?["version"]?.GetValue<int>() ?? 0;
    private static string Status(ReviewAssetCapture capture, JsonObject? state) => capture.Digest == "" ? "blocked"
        : state == null ? "unverified" : state["digest"]!.GetValue<string>() != capture.Digest ? "stale" : state["state"]!.GetValue<string>();

    public Task<JsonObject> Current(string kind, Guid uuid, CancellationToken ct) => db.Snapshot(async () =>
    {
        Kind(kind);
        var capture = await builder.CaptureAsset(kind, uuid, ct);
        var state = await State(kind, uuid, ct);
        string status = Status(capture, state);
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["kind"] = kind,
            ["assetUuid"] = uuid.ToString(),
            ["name"] = capture.Name,
            ["definitionDigest"] = capture.Digest,
            ["state"] = status,
            ["verified"] = status == "verified",
            ["stateVersion"] = Version(state),
            ["body"] = capture.Body,
            ["provenance"] = capture.Provenance,
            ["findings"] = JsonSerializer.SerializeToNode(capture.Findings, JsonOptions),
            ["blobs"] = new JsonArray(capture.Blobs.Order().Select(h => (JsonNode)new JsonObject { ["hash"] = h, ["url"] = "/api/blob/" + h }).ToArray())
        };
    }, ct);

    public Task<JsonObject> List(string kind, string status, int after, int limit, CancellationToken ct) => db.Snapshot(async () =>
    {
        Kind(kind);
        if (status is not ("pending" or "all" or "unverified" or "verified" or "stale" or "needs_attention" or "blocked") || after < 0 || limit is < 1 or > 100)
            throw new ReviewException(400, "Use a valid status, after >= 0, and limit between 1 and 100");
        JsonArray items = [];
        int cursor = after;
        bool more = true;
        while (items.Count < limit && more)
        {
            var rows = await db.Rows($"SELECT jsonb_build_object('id',id,'uuid',uuid)::text FROM public.{kind} WHERE deleted_at IS NULL AND id>@after ORDER BY id LIMIT 100", ct, ("after", cursor));
            more = rows.Count == 100;
            foreach (var row in rows)
            {
                cursor = row["id"]!.GetValue<int>();
                var item = await Current(kind, Guid.Parse(row["uuid"]!.GetValue<string>()), ct);
                string current = item["state"]!.GetValue<string>();
                if (status == "all" || current == status || (status == "pending" && current != "verified"))
                {
                    item.Remove("body"); item.Remove("provenance"); item.Remove("blobs");
                    item["inspectUrl"] = $"/api/verifications/{kind}/{row["uuid"]!.GetValue<string>()}";
                    items.Add(item);
                    if (items.Count == limit) { more = true; break; }
                }
            }
        }
        return new JsonObject { ["schemaVersion"] = 1, ["items"] = items, ["nextAfter"] = more ? cursor : null };
    }, ct);

    // The normal asset lists use the same snapshot for display fields and status.
    public Task<List<T>> WithStates<T>(string kind, Func<Task<List<T>>> read, Func<T, Guid?> uuid, Action<T, string> stamp, CancellationToken ct) => db.Snapshot(async () =>
    {
        var items = await read();
        foreach (var item in items)
        {
            string state = uuid(item) is Guid id ? (await Current(kind, id, ct))["state"]!.GetValue<string>() : "blocked";
            stamp(item, state);
        }
        return items;
    }, ct);

    public async Task<JsonObject> Act(string kind, Guid uuid, AssetVerificationRequest request, CallerContext caller, CancellationToken ct)
    {
        Kind(kind);
        if (!caller.IsAuthenticated) throw new ReviewException(401, "Authentication required");
        if (!caller.CanRead || !caller.CanWrite) throw new ReviewException(403, "Catalog read and write permission required");
        if (request.Action is not ("verify" or "flag") || request.RequestUuid == Guid.Empty || request.ExpectedStateVersion < 0
            || string.IsNullOrWhiteSpace(request.Note) || request.Note.Length > 4000)
            throw new ReviewException(400, "Supply verify/flag, a request UUID, expected state version, and a note of 1 through 4000 characters");
        var references = request.ReferenceUrls ?? [];
        if (references.Count > 10 || references.Any(url => url == null || url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo != ""))
            throw new ReviewException(400, "Use at most 10 HTTP(S) reference URLs without embedded credentials (2048 characters each)");
        JsonObject actor = new() { ["userId"] = caller.UserId, ["username"] = caller.Username, ["authType"] = caller.AuthType, ["tokenName"] = caller.TokenName };
        string fingerprint = ReviewJson.Digest(ReviewJson.Canonical(new JsonObject
        {
            ["kind"] = kind,
            ["assetUuid"] = uuid.ToString(),
            ["request"] = JsonSerializer.SerializeToNode(request, JsonOptions),
            ["actor"] = actor.DeepClone()
        }));
        await using var tx = await db.Context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await db.Execute("SELECT pg_advisory_xact_lock(1936418162, 1)", ct);
        var prior = await db.Row("SELECT to_jsonb(a)::text FROM public.asset_verification_action a WHERE request_uuid=@request", ct, ("request", request.RequestUuid));
        if (prior != null)
        {
            if (prior["fingerprint"]!.GetValue<string>() != fingerprint) throw new ReviewException(409, "Request UUID was used with different content or actor");
            return prior["result"]!.AsObject();
        }
        var capture = await builder.CaptureAsset(kind, uuid, ct);
        if (capture.Digest == "") throw new ReviewException(422, "Asset capture is blocked; resolve its findings before recording verification");
        if (capture.Digest != request.DefinitionDigest) throw new ReviewException(409, "Asset or dependency changed; inspect the current definition");
        var state = await State(kind, uuid, ct);
        if (Version(state) != request.ExpectedStateVersion) throw new ReviewException(409, "Verification changed; reload before acting");
        await db.Execute("INSERT INTO public.asset_verification_definition(digest,kind,asset_uuid,body,provenance) VALUES(@digest,@kind,@uuid,@body,@provenance::jsonb) ON CONFLICT(digest) DO NOTHING", ct,
            ("digest", capture.Digest), ("kind", kind), ("uuid", uuid), ("body", ReviewJson.Canonical(capture.Body)), ("provenance", capture.Provenance.ToJsonString()));
        foreach (string hash in capture.Blobs)
            await db.Execute("INSERT INTO public.asset_verification_blob(digest,hash) VALUES(@digest,@hash) ON CONFLICT DO NOTHING", ct, ("digest", capture.Digest), ("hash", hash));
        string next = request.Action == "verify" ? "verified" : "needs_attention";
        int version = Version(state) + 1;
        JsonObject result = new()
        {
            ["schemaVersion"] = 1,
            ["kind"] = kind,
            ["assetUuid"] = uuid.ToString(),
            ["definitionDigest"] = capture.Digest,
            ["state"] = next,
            ["verified"] = next == "verified",
            ["stateVersion"] = version,
            ["requestUuid"] = request.RequestUuid.ToString(),
            ["actor"] = actor.DeepClone(),
            ["recordedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["note"] = request.Note,
            ["referenceUrls"] = JsonSerializer.SerializeToNode(references, JsonOptions)
        };
        await db.Execute("INSERT INTO public.asset_verification_state(kind,asset_uuid,digest,state,version) VALUES(@kind,@uuid,@digest,@state,@version) ON CONFLICT(kind,asset_uuid) DO UPDATE SET digest=EXCLUDED.digest,state=EXCLUDED.state,version=EXCLUDED.version", ct,
            ("kind", kind), ("uuid", uuid), ("digest", capture.Digest), ("state", next), ("version", version));
        await db.Execute("INSERT INTO public.asset_verification_action(request_uuid,fingerprint,kind,asset_uuid,digest,action,actor,note,reference_urls,result) VALUES(@request,@fingerprint,@kind,@uuid,@digest,@action,@actor::jsonb,@note,@refs::jsonb,@result::jsonb)", ct,
            ("request", request.RequestUuid), ("fingerprint", fingerprint), ("kind", kind), ("uuid", uuid), ("digest", capture.Digest), ("action", request.Action), ("actor", actor.ToJsonString()), ("note", request.Note), ("refs", JsonSerializer.Serialize(references, JsonOptions)), ("result", result.ToJsonString()));
        await tx.CommitAsync(ct);
        return result;
    }

    public Task<JsonObject> History(string kind, Guid uuid, long before, int limit, CancellationToken ct) => db.Snapshot(async () =>
    {
        Kind(kind);
        if (before < 0 || limit is < 1 or > 100) throw new ReviewException(400, "Use before >= 0 and limit between 1 and 100");
        var rows = await db.Rows("SELECT jsonb_build_object('id',id,'action',action,'result',result)::text FROM public.asset_verification_action WHERE kind=@kind AND asset_uuid=@uuid AND (@before=0 OR id<@before) ORDER BY id DESC LIMIT @limit", ct,
            ("kind", kind), ("uuid", uuid), ("before", before), ("limit", limit));
        return new JsonObject { ["schemaVersion"] = 1, ["items"] = new JsonArray(rows.Select(r => (JsonNode)r).ToArray()), ["nextBefore"] = rows.LastOrDefault()?["id"]?.DeepClone() };
    }, ct);

    public Task<JsonObject> Definition(string digest, CancellationToken ct) => db.Snapshot(async () =>
    {
        var row = await db.Row("SELECT jsonb_build_object('definitionDigest',digest,'body',body::jsonb,'provenance',provenance)::text FROM public.asset_verification_definition WHERE digest=@digest", ct, ("digest", digest));
        return row ?? throw new ReviewException(404, "Retained asset definition not found");
    }, ct);

    public Task<byte[]> Blob(string digest, string hash, CancellationToken ct) => db.Snapshot(async () =>
    {
        if (await db.Row("SELECT jsonb_build_object('found',true)::text FROM public.asset_verification_blob WHERE digest=@digest AND hash=@hash", ct, ("digest", digest), ("hash", hash)) == null)
            throw new ReviewException(404, "Blob does not belong to this asset definition");
        return await db.Blob(hash, ct) ?? throw new ReviewException(404, "Retained blob is missing");
    }, ct);
}
