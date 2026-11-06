using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace KiComLibBackend.Services.Reviews;

public sealed record ReviewActionRequest(string Action, string DefinitionDigest, int ExpectedStateVersion, Guid RequestUuid, string? Comment);
public sealed record ReviewPair(Guid PartUuid, string DefinitionDigest);
public sealed record ReviewActionResult(int Status, JsonObject Body);

public sealed class ReviewService(ReviewDatabase db, ReviewDefinitionBuilder builder, IReviewValidator validator)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static JsonNode Json<T>(T value) => JsonSerializer.SerializeToNode(value, JsonOptions)!;
    private static string Str(JsonObject? row, string key, string fallback = "") => row?[key]?.GetValue<string>() ?? fallback;
    private static int Version(JsonObject? state) => state?["version"]?.GetValue<int>() ?? 0;
    private Task<JsonObject?> State(string digest, CancellationToken ct) => db.Row("SELECT to_jsonb(s)::text FROM public.review_state s WHERE digest=@digest", ct, ("digest", digest));

    public Task<JsonObject> Current(Guid uuid, CancellationToken ct) => db.Snapshot(async () =>
    {
        var capture = await builder.Capture(uuid, ct);
        var state = capture.Digest == "" ? null : await State(capture.Digest, ct);
        return new JsonObject
        {
            ["partUuid"] = uuid.ToString(),
            ["definitionDigest"] = capture.Digest,
            ["state"] = Str(state, "state", "unreviewed"),
            ["stateVersion"] = Version(state),
            ["body"] = capture.Body,
            ["provenance"] = capture.Provenance,
            ["findings"] = Json(capture.Findings),
            ["policyId"] = validator.PolicyId,
            ["approvalValid"] = Valid(state),
            ["historyUrl"] = capture.Digest == "" ? null : "/api/review-definitions/" + capture.Digest + "/history"
        };
    }, ct);

    private bool Valid(JsonObject? state) => Str(state, "state") == "approved" && Str(state, "evidence_policy") == validator.PolicyId && state?["evidence_valid"]?.GetValue<bool>() == true;

    public Task<JsonObject> Definition(string digest, CancellationToken ct) => db.Snapshot(async () =>
    {
        var row = await db.Row("SELECT to_jsonb(d)::text FROM public.review_definition d WHERE digest=@digest", ct, ("digest", digest)) ?? throw new ReviewException(404, "Definition has not been submitted");
        var state = await State(digest, ct);
        var uuid = Guid.Parse(Str(row, "part_uuid"));
        var part = await db.Row("SELECT jsonb_build_object('exists',true)::text FROM public.part WHERE uuid=@uuid", ct, ("uuid", uuid));
        string? current = part == null ? null : (await builder.Capture(uuid, ct)).Digest;
        var reports = await db.Rows("SELECT jsonb_build_object('id',id,'createdAt',created_at,'report',report)::text FROM public.review_validation WHERE digest=@digest ORDER BY id DESC LIMIT 1", ct, ("digest", digest));
        return new JsonObject
        {
            ["definitionDigest"] = digest,
            ["body"] = JsonNode.Parse(Str(row, "body")),
            ["provenance"] = row["provenance"]?.DeepClone(),
            ["createdAt"] = row["created_at"]?.DeepClone(),
            ["state"] = Str(state, "state", "unreviewed"),
            ["stateVersion"] = Version(state),
            ["isCurrent"] = current == digest,
            ["partExists"] = part != null,
            ["approvalValid"] = part != null && Valid(state),
            ["policyId"] = validator.PolicyId,
            ["validation"] = reports.FirstOrDefault()
        };
    }, ct);

    public Task<JsonObject> History(string digest, long before, int limit, CancellationToken ct) => db.Snapshot(async () =>
    {
        var rows = await db.Rows("SELECT jsonb_build_object('id',id,'requestUuid',request_uuid,'action',action,'actor',actor,'comment',comment,'stateVersion',state_version,'result',result,'createdAt',created_at)::text FROM public.review_action WHERE digest=@digest AND (@before=0 OR id<@before) ORDER BY id DESC LIMIT @limit", ct, ("digest", digest), ("before", before), ("limit", Math.Clamp(limit, 1, 100)));
        return new JsonObject { ["items"] = new JsonArray(rows.Select(r => (JsonNode)r).ToArray()), ["nextBefore"] = rows.LastOrDefault()?["id"]?.DeepClone() };
    }, ct);

    public Task<JsonArray> Definitions(Guid uuid, CancellationToken ct) => db.Snapshot(async () =>
    {
        var rows = await db.Rows("SELECT jsonb_build_object('definitionDigest',d.digest,'state',s.state,'stateVersion',s.version,'createdAt',d.created_at)::text FROM public.review_definition d JOIN public.review_state s USING(digest) WHERE part_uuid=@uuid ORDER BY created_at DESC LIMIT 200", ct, ("uuid", uuid));
        return new JsonArray(rows.Select(r => (JsonNode)r).ToArray());
    }, ct);

    public Task<JsonArray> CurrentStates(CancellationToken ct) => db.Snapshot(async () =>
    {
        var rows = await db.Rows("SELECT jsonb_build_object('uuid',uuid)::text FROM public.part ORDER BY id", ct);
        JsonArray result = [];
        foreach (var row in rows)
        {
            var capture = await builder.Capture(Guid.Parse(Str(row, "uuid")), ct);
            var state = capture.Digest == "" ? null : await State(capture.Digest, ct);
            result.Add(new JsonObject { ["partUuid"] = capture.PartUuid.ToString(), ["definitionDigest"] = capture.Digest, ["state"] = Str(state, "state", "unreviewed") });
        }
        return result;
    }, ct);

    public Task<JsonArray> Affected(string hash, CancellationToken ct) => db.Snapshot(async () =>
    {
        var rows = await db.Rows("SELECT jsonb_build_object('partUuid',d.part_uuid,'definitionDigest',d.digest,'stateVersion',s.version,'name',d.body::jsonb->'part'->>'name')::text FROM public.review_definition_blob b JOIN public.review_definition d USING(digest) JOIN public.review_state s USING(digest) WHERE b.hash=@hash AND s.state='approved' ORDER BY d.created_at DESC", ct, ("hash", hash));
        return new JsonArray(rows.Select(r => (JsonNode)r).ToArray());
    }, ct);

    public Task<JsonObject> Resolve(IReadOnlyList<ReviewPair> pairs, CancellationToken ct) => db.Snapshot(async () =>
    {
        if (pairs.Count is < 1 or > 200) throw new ReviewException(400, "Supply between 1 and 200 exact part/digest pairs");
        var server = await db.Row("SELECT to_jsonb(s)::text FROM public.review_server s", ct);
        JsonArray results = [];
        var current = new Dictionary<Guid, string?>();
        foreach (var pair in pairs)
        {
            if (!current.ContainsKey(pair.PartUuid))
            {
                try { current[pair.PartUuid] = (await builder.Capture(pair.PartUuid, ct)).Digest; }
                catch (ReviewException ex) when (ex.Status == 404) { current[pair.PartUuid] = null; }
            }
            var definition = await db.Row("SELECT jsonb_build_object('schema',schema_version)::text FROM public.review_definition WHERE part_uuid=@uuid AND digest=@digest", ct, ("uuid", pair.PartUuid), ("digest", pair.DefinitionDigest));
            var state = definition == null ? null : await State(pair.DefinitionDigest, ct);
            JsonArray reasons = [];
            if (definition == null) reasons.Add("unknown_definition");
            if (current[pair.PartUuid] == null) reasons.Add("part_deleted");
            if (definition != null && definition["schema"]?.GetValue<int>() != 1) reasons.Add("unsupported_schema");
            if (Str(state, "state") != "approved") reasons.Add(Str(state, "state", "unreviewed"));
            else if (!Valid(state)) reasons.Add("validation_required");
            results.Add(new JsonObject
            {
                ["partUuid"] = pair.PartUuid.ToString(),
                ["definitionDigest"] = pair.DefinitionDigest,
                ["state"] = Str(state, "state", "unreviewed"),
                ["stateVersion"] = Version(state),
                ["isCurrent"] = current[pair.PartUuid] == pair.DefinitionDigest,
                ["approvalValid"] = reasons.Count == 0,
                ["reasons"] = reasons,
                ["definitionUrl"] = definition == null ? null : "/api/review-definitions/" + pair.DefinitionDigest
            });
        }
        return new JsonObject { ["schemaVersion"] = 1, ["serverInstanceUuid"] = server?["instance_id"]?.DeepClone(), ["responseUuid"] = Guid.NewGuid().ToString(), ["observedAt"] = DateTimeOffset.UtcNow.ToString("O"), ["policyId"] = validator.PolicyId, ["results"] = results };
    }, ct);

    public async Task<ReviewActionResult> Act(Guid uuid, ReviewActionRequest request, CallerContext caller, CancellationToken ct)
    {
        if (!caller.IsAuthenticated) throw new ReviewException(401, "Authentication required");
        if (!caller.CanWrite || (request.Action != "submit" && !caller.IsAdmin)) throw new ReviewException(403, "This action requires reviewer permission and a writable credential");
        if (request.RequestUuid == Guid.Empty || request.ExpectedStateVersion < 0 || request.DefinitionDigest?.Length != 71) throw new ReviewException(400, "A digest, expected state version, and request UUID are required");
        if (request.Comment?.Length > 4000) throw new ReviewException(400, "Comment must be at most 4000 characters");
        if (request.Action is not ("submit" or "approve" or "reject" or "revoke" or "reopen" or "revalidate")) throw new ReviewException(400, "Unknown review action");
        if (request.Action is "reject" or "revoke" or "reopen" && string.IsNullOrWhiteSpace(request.Comment)) throw new ReviewException(400, "A reason is required");
        JsonObject actor = new() { ["userId"] = caller.UserId, ["username"] = caller.Username, ["authType"] = caller.AuthType, ["tokenName"] = caller.TokenName };
        string fingerprint = ReviewJson.Digest(ReviewJson.Canonical(new JsonObject { ["partUuid"] = uuid.ToString(), ["request"] = Json(request), ["actor"] = actor.DeepClone() }));
        await using var tx = await db.Context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        // Do not use REPEATABLE READ/SERIALIZABLE here: a snapshot established while
        // waiting for the lock could approve an obsolete head.
        await db.Execute("SELECT pg_advisory_xact_lock(1936418162, 1)", ct);
        var prior = await db.Row("SELECT to_jsonb(r)::text FROM public.review_receipt r WHERE request_uuid=@id", ct, ("id", request.RequestUuid));
        if (prior != null)
        {
            if (Str(prior, "fingerprint") != fingerprint) throw new ReviewException(409, "Request UUID was already used with different content or actor");
            return new(prior["status"]!.GetValue<int>(), prior["result"]!.AsObject());
        }
        string digest = request.DefinitionDigest!;
        var state = await State(digest, ct);
        int version = Version(state);
        if (version != request.ExpectedStateVersion) throw new ReviewException(409, "Review state changed; reload before acting");
        string from = Str(state, "state", "unreviewed");
        ReviewCapture? capture = null;
        if (request.Action is "submit" or "approve" or "reject" or "reopen")
        {
            capture = await builder.Capture(uuid, ct);
            if (capture.Digest == "") throw new ReviewException(422, "Definition capture failed: " + string.Join("; ", capture.Findings.Where(f => f.Severity == "error").Select(f => f.EntityPath + ": " + f.Message)));
            if (capture.Digest != digest) throw new ReviewException(409, "The part or a dependency changed; review the current definition");
        }
        var existing = await db.Row("SELECT to_jsonb(d)::text FROM public.review_definition d WHERE digest=@digest AND part_uuid=@uuid", ct, ("digest", digest), ("uuid", uuid));
        if (existing == null && request.Action != "submit") throw new ReviewException(404, "Submitted definition not found for this part");
        if (request.Action == "submit" && from == "approved")
        {
            var noOp = new JsonObject { ["definitionDigest"] = digest, ["state"] = from, ["stateVersion"] = version, ["accepted"] = true, ["noOp"] = true };
            await Receipt(request.RequestUuid, fingerprint, 200, noOp, ct); await tx.CommitAsync(ct); return new(200, noOp);
        }
        string to = request.Action switch
        {
            "submit" when from is "unreviewed" or "rejected" => "in_review",
            "approve" when from == "in_review" => "approved",
            "reject" when from == "in_review" => "rejected",
            "revoke" when from == "approved" => "revoked",
            "reopen" when from == "revoked" => "in_review",
            "revalidate" when from == "approved" => "approved",
            _ => throw new ReviewException(409, $"Cannot {request.Action} a {from} definition")
        };
        if (existing == null)
        {
            await db.Execute("INSERT INTO public.review_definition(digest,part_uuid,schema_version,body,provenance) VALUES(@digest,@uuid,1,@body,@provenance::jsonb)", ct, ("digest", digest), ("uuid", uuid), ("body", ReviewJson.Canonical(capture!.Body)), ("provenance", capture.Provenance.ToJsonString()));
            foreach (string hash in capture.Blobs) await db.Execute("INSERT INTO public.review_definition_blob(digest,hash) VALUES(@digest,@hash)", ct, ("digest", digest), ("hash", hash));
        }
        IReadOnlyList<ReviewFinding> findings = [];
        bool validate = request.Action is "submit" or "approve" or "revalidate" or "reopen";
        if (validate)
        {
            var body = capture?.Body ?? JsonNode.Parse(Str(existing, "body"))!.AsObject();
            var rows = await db.Rows("SELECT jsonb_build_object('hash',hash)::text FROM public.review_definition_blob WHERE digest=@digest", ct, ("digest", digest));
            findings = await validator.Validate(body, digest, rows.Select(r => Str(r, "hash")).ToArray(), ct);
        }
        bool passed = !findings.Any(f => f.Severity == "error");
        bool accepted = !validate || passed;
        if (!accepted) to = from == "unreviewed" ? "in_review" : from;
        version++;
        bool evidence = request.Action is "approve" or "revalidate" && passed;
        var response = new JsonObject { ["definitionDigest"] = digest, ["fromState"] = from, ["state"] = to, ["stateVersion"] = version, ["accepted"] = accepted, ["findings"] = Json(findings), ["approvalValid"] = to == "approved" && evidence, ["policyId"] = validator.PolicyId };
        await db.Execute("INSERT INTO public.review_state(digest,state,version,evidence_policy,evidence_valid) VALUES(@digest,@state,@version,@policy,@valid) ON CONFLICT(digest) DO UPDATE SET state=EXCLUDED.state,version=EXCLUDED.version,evidence_policy=EXCLUDED.evidence_policy,evidence_valid=EXCLUDED.evidence_valid", ct, ("digest", digest), ("state", to), ("version", version), ("policy", validator.PolicyId), ("valid", evidence));
        await db.Execute("INSERT INTO public.review_action(request_uuid,request_fingerprint,digest,action,actor,comment,state_version,result) VALUES(@id,@fingerprint,@digest,@action,@actor::jsonb,@comment,@version,@result::jsonb)", ct, ("id", request.RequestUuid), ("fingerprint", fingerprint), ("digest", digest), ("action", request.Action), ("actor", actor.ToJsonString()), ("comment", request.Comment), ("version", version), ("result", response.ToJsonString()));
        if (validate)
        {
            JsonObject report = new() { ["definitionDigest"] = digest, ["policyId"] = validator.PolicyId, ["validator"] = "KiComLib fast validator v1", ["passed"] = passed, ["findings"] = Json(findings) };
            await db.Execute("INSERT INTO public.review_validation(digest,request_uuid,policy,report) VALUES(@digest,@id,@policy,@report::jsonb)", ct, ("digest", digest), ("id", request.RequestUuid), ("policy", validator.PolicyId), ("report", report.ToJsonString()));
        }
        int status = accepted ? 200 : 422;
        await Receipt(request.RequestUuid, fingerprint, status, response, ct);
        await tx.CommitAsync(ct);
        return new(status, response);
    }

    private Task Receipt(Guid id, string fingerprint, int status, JsonObject response, CancellationToken ct) =>
        db.Execute("INSERT INTO public.review_receipt(request_uuid,fingerprint,status,result) VALUES(@id,@fingerprint,@status,@result::jsonb)", ct, ("id", id), ("fingerprint", fingerprint), ("status", status), ("result", response.ToJsonString()));

    public async Task<byte[]> RetainedBlob(string digest, string hash, CancellationToken ct)
    {
        if (await db.Row("SELECT jsonb_build_object('found',true)::text FROM public.review_definition_blob WHERE digest=@digest AND hash=@hash", ct, ("digest", digest), ("hash", hash)) == null) throw new ReviewException(404, "Blob does not belong to this definition");
        return await db.Blob(hash, ct) ?? throw new ReviewException(404, "Retained blob is missing");
    }
}
