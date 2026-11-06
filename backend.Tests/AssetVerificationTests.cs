using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KiComLibBackend.Services.Reviews;
using Npgsql;

namespace KiComLibBackend.Tests;

[Collection("kicomlib-db")]
public class AssetVerificationTests(CustomWebApplicationFactory factory)
{
    private sealed record Asset(string Kind, int Id, Guid Uuid, int Library, int Revision, string Hash);
    private async Task<HttpClient> Admin()
    {
        Assert.True(factory.DatabaseAvailable, "Asset verification integration tests require real PostgreSQL; refusing to skip.");
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "kicomlib" });
        login.EnsureSuccessStatusCode(); TestSessions.UseBearer(client, login); return client;
    }
    private static async Task<JsonObject> Read(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");
        return JsonNode.Parse(body)!.AsObject();
    }
    private static string Url(Asset asset) => $"/api/verifications/{asset.Kind}/{asset.Uuid}";
    private static string Digest(JsonObject current) => current["definitionDigest"]!.GetValue<string>();
    private async Task<JsonObject> Current(HttpClient client, Asset asset) => await Read(await client.GetAsync(Url(asset)));
    private Task<HttpResponseMessage> Act(HttpClient client, Asset asset, JsonObject current, string action = "verify", Guid? id = null, string note = "Checked synthetic geometry against its fixture.") =>
        client.PostAsJsonAsync(Url(asset) + "/actions", new AssetVerificationRequest(action, Digest(current), current["stateVersion"]!.GetValue<int>(), id ?? Guid.NewGuid(), note, []));
    private async Task<object?> Sql(string sql, params (string, object)[] parameters)
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value);
        return await command.ExecuteScalarAsync();
    }
    private async Task<string> Blob(string source)
    {
        byte[] data = Encoding.UTF8.GetBytes(source); string hash = Convert.ToHexStringLower(SHA256.HashData(data));
        await Sql("INSERT INTO blob(hash,size,media_type,data) VALUES(@hash,@size,'text/plain',@data) ON CONFLICT DO NOTHING", ("hash", hash), ("size", data.Length), ("data", data));
        return hash;
    }
    private async Task<Asset> Create(string kind, string source, string? name = null, int? library = null)
    {
        name ??= "V-" + Guid.NewGuid();
        int lib = library ?? (int)(await Sql("INSERT INTO library_file(type,name,filename) VALUES(@type,@name,@name) RETURNING id", ("type", kind == "symbol" ? 0 : 1), ("name", "Verify-" + Guid.NewGuid())))!;
        int id = (int)(await Sql($"INSERT INTO {kind}(library_file_id,name) VALUES(@library,@name) RETURNING id", ("library", lib), ("name", name)))!;
        Guid uuid = (Guid)(await Sql($"SELECT uuid FROM {kind} WHERE id=@id", ("id", id)))!;
        return await Revise(new(kind, id, uuid, lib, 0, ""), source);
    }
    private async Task<Asset> Revise(Asset asset, string source)
    {
        string hash = await Blob(source);
        int commit = (int)(await Sql("INSERT INTO public.commit(message,author) VALUES('Verification test','test') RETURNING id"))!;
        string extra = asset.Kind == "symbol" ? ",reference" : ",type,layer";
        string values = asset.Kind == "symbol" ? ",'U'" : ",0,'F.Cu'";
        int revision = (int)(await Sql($"INSERT INTO {asset.Kind}_revision({asset.Kind}_id,commit_id,revision_no,library_file_id,name,blob_hash{extra}) SELECT id,@commit,COALESCE((SELECT max(revision_no) FROM {asset.Kind}_revision WHERE {asset.Kind}_id=@id),0)+1,library_file_id,name,@hash{values} FROM {asset.Kind} WHERE id=@id RETURNING id", ("commit", commit), ("id", asset.Id), ("hash", hash)))!;
        await Sql($"UPDATE {asset.Kind} SET head_revision_id=@revision WHERE id=@id", ("revision", revision), ("id", asset.Id));
        return asset with { Hash = hash, Revision = revision };
    }

    [Theory]
    [InlineData("symbol", "(symbol \"Test\")")]
    [InlineData("footprint", "(footprint \"Test\")")]
    public async Task Verify_Flag_Retry_AndCatalogFields(string kind, string source)
    {
        var client = await Admin(); var asset = await Create(kind, source); var current = await Current(client, asset);
        Assert.Equal("unverified", current["state"]!.GetValue<string>()); Assert.False(current["verified"]!.GetValue<bool>());
        object? partApprovals = await Sql("SELECT count(*) FROM review_action");
        var request = Guid.NewGuid(); var verified = await Read(await Act(client, asset, current, id: request));
        Assert.True(verified["verified"]!.GetValue<bool>());
        Assert.Equal("admin", verified["actor"]!["username"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(verified, await Read(await Act(client, asset, current, id: request))));
        Assert.Equal(HttpStatusCode.Conflict, (await Act(client, asset, current, id: request, note: "Different note")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Act(client, asset, current, "flag")).StatusCode);
        var catalog = JsonNode.Parse(await client.GetStringAsync("/api/" + kind))!.AsArray();
        Assert.True(catalog.Single(n => n!["uuid"]!.GetValue<string>() == asset.Uuid.ToString())!["verified"]!.GetValue<bool>());
        var detail = await Read(await client.GetAsync($"/api/{kind}/{asset.Id}"));
        Assert.Equal("verified", detail["verificationState"]!.GetValue<string>());
        Assert.True(detail["verified"]!.GetValue<bool>());
        var flagged = await Read(await Act(client, asset, verified, "flag", note: "Pad/pin definition needs correction."));
        Assert.False(flagged["verified"]!.GetValue<bool>());
        Assert.Equal("needs_attention", (await Current(client, asset))["state"]!.GetValue<string>());
        var history = await Read(await client.GetAsync(Url(asset) + "/history?limit=1"));
        Assert.Single(history["items"]!.AsArray());
        Assert.Equal("flag", history["items"]![0]!["action"]!.GetValue<string>());
        Assert.Single((await Read(await client.GetAsync(Url(asset) + "/history?limit=1&before=" + history["nextBefore"]!.GetValue<long>())))["items"]!.AsArray());
        Assert.Equal(partApprovals, await Sql("SELECT count(*) FROM review_action"));
        var retained = await Read(await client.GetAsync("/api/verifications/definitions/" + Digest(verified)));
        Assert.Equal(Digest(verified), ReviewJson.Digest(ReviewJson.Canonical(retained["body"])));
        Assert.Equal(Encoding.UTF8.GetBytes(source), await client.GetByteArrayAsync($"/api/verifications/definitions/{Digest(verified)}/blobs/{asset.Hash}"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/verifications/definitions/{Digest(verified)}/blobs/{new string('0', 64)}")).StatusCode);
        Assert.Equal(1, (await Read(await client.GetAsync("/api/info")))["assetVerificationApiVersion"]!.GetValue<int>());
    }

    [Fact]
    public async Task EditsAndInheritedDependencies_BecomeStale_AndHistoryRetainsOldBytes()
    {
        var client = await Admin(); var parent = await Create("symbol", "(symbol \"Parent\")", "Parent");
        var child = await Create("symbol", "(symbol \"Child\" (extends \"Parent\"))", "Child", parent.Library);
        var inspected = await Current(client, child); var verified = await Read(await Act(client, child, inspected));
        Assert.Equal(parent.Hash, inspected["body"]!["asset"]!["parent"]!["blobHash"]!.GetValue<string>());
        string oldHash = parent.Hash;
        parent = await Revise(parent, "(symbol \"Parent\" (property \"Value\" \"Changed\"))");
        var changed = await Current(client, child);
        Assert.Equal("stale", changed["state"]!.GetValue<string>()); Assert.NotEqual(Digest(verified), Digest(changed));
        Assert.Equal(HttpStatusCode.Conflict, (await Act(client, child, verified)).StatusCode);
        Assert.Equal(Encoding.UTF8.GetBytes("(symbol \"Parent\")"), await client.GetByteArrayAsync($"/api/verifications/definitions/{Digest(verified)}/blobs/{oldHash}"));
        await Assert.ThrowsAsync<PostgresException>(() => Sql("UPDATE blob SET data=decode('00','hex') WHERE hash=@hash", ("hash", oldHash)));
        await Assert.ThrowsAsync<PostgresException>(() => Sql("DELETE FROM asset_verification_action WHERE asset_uuid=@uuid", ("uuid", child.Uuid)));
        await Assert.ThrowsAsync<PostgresException>(() => Sql("UPDATE asset_verification_definition SET body='{}' WHERE digest=@digest", ("digest", Digest(verified))));
        await Sql("UPDATE library_file SET filename=filename || '-renamed' WHERE id=@id", ("id", child.Library));
        Assert.NotEqual(Digest(changed), Digest(await Current(client, child)));
        await Sql("UPDATE symbol SET deleted_at=now() WHERE id=@id", ("id", child.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Url(child))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Act(client, child, changed)).StatusCode);
        (await client.GetAsync("/api/verifications/definitions/" + Digest(verified))).EnsureSuccessStatusCode();
        Assert.Single((await Read(await client.GetAsync(Url(child) + "/history")))["items"]!.AsArray());
    }

    [Fact]
    public async Task ModelChangesInvalidateFootprints_AndInconsistentTransformsAreBlocked()
    {
        var client = await Admin(); string filename = Guid.NewGuid() + ".step";
        string hash = await Blob("synthetic model v1 " + filename);
        int model = (int)(await Sql("INSERT INTO model3d(directory,filename) VALUES('',@filename) RETURNING id", ("filename", filename)))!;
        int commit = (int)(await Sql("INSERT INTO public.commit(message,author) VALUES('model test','test') RETURNING id"))!;
        async Task ModelRevision(string blob, int number)
        {
            int revision = (int)(await Sql("INSERT INTO model3d_revision(model3d_id,commit_id,revision_no,filename,blob_hash) VALUES(@id,@commit,@number,@filename,@hash) RETURNING id", ("id", model), ("commit", commit), ("number", number), ("filename", filename), ("hash", blob)))!;
            await Sql("UPDATE model3d SET head_revision_id=@revision WHERE id=@id", ("revision", revision), ("id", model));
        }
        await ModelRevision(hash, 1);
        var footprint = await Create("footprint", "(footprint \"Model\" (model \"" + filename + "\" (offset (xyz 0 0 0)) (scale (xyz 1 1 1)) (rotate (xyz 0 0 0))))");
        await Sql("INSERT INTO footprint_revision_model3d(footprint_revision_id,position,model3d_id) VALUES(@revision,0,@model)", ("revision", footprint.Revision), ("model", model));
        var verified = await Read(await Act(client, footprint, await Current(client, footprint)));
        await ModelRevision(await Blob("synthetic model v2 " + filename), 2);
        Assert.Equal("stale", (await Current(client, footprint))["state"]!.GetValue<string>());
        await Sql("UPDATE footprint_revision_model3d SET offset_x=10 WHERE footprint_revision_id=@revision", ("revision", footprint.Revision));
        var blocked = await Current(client, footprint);
        Assert.Equal("blocked", blocked["state"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Act(client, footprint, verified)).StatusCode);
    }

    [Fact]
    public async Task DiscoveryPaginationAndFiltering_DoNotSkipPendingItems()
    {
        var client = await Admin(); var first = await Create("footprint", "(footprint \"First\")");
        var second = await Create("footprint", "(footprint \"Second\")");
        await Read(await Act(client, first, await Current(client, first)));
        var page = await Read(await client.GetAsync($"/api/verifications?kind=footprint&after={first.Id - 1}&limit=1"));
        Assert.Single(page["items"]!.AsArray());
        Assert.Equal(second.Uuid.ToString(), page["items"]![0]!["assetUuid"]!.GetValue<string>());
        Assert.Equal(second.Id, page["nextAfter"]!.GetValue<int>());
        Assert.Empty((await Read(await client.GetAsync($"/api/verifications?kind=footprint&after={second.Id}&limit=1")))["items"]!.AsArray());
        var all = await Read(await client.GetAsync($"/api/verifications?kind=footprint&status=verified&after={first.Id - 1}&limit=1"));
        Assert.Equal(first.Uuid.ToString(), all["items"]![0]!["assetUuid"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/verifications?kind=part")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/verifications?limit=101")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/verifications?after=-1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/verifications?status=unknown")).StatusCode);
    }

    [Fact]
    public async Task ReaderCannotVerify_NonAdminWriterCan_AndCallerCannotBeForged()
    {
        var admin = await Admin(); var asset = await Create("symbol", "(symbol \"Permissions\")"); var current = await Current(admin, asset);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(Url(asset))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Act(factory.CreateClient(), asset, current)).StatusCode);
        var token = await Read(await admin.PostAsJsonAsync("/api/token", new { name = "asset-reader-" + Guid.NewGuid(), canWrite = false }));
        var reader = factory.CreateClient(); reader.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token["token"]!.GetValue<string>());
        (await reader.GetAsync(Url(asset))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await Act(reader, asset, current)).StatusCode);
        var groups = JsonNode.Parse(await admin.GetStringAsync("/api/group"))!.AsArray();
        int group = groups.Single(g => g!["name"]!.GetValue<string>() == "Read and write")!["id"]!.GetValue<int>();
        string username = "asset-writer-" + Guid.NewGuid().ToString("N");
        (await admin.PostAsJsonAsync("/api/user", new { username, password = "writer-test-password", groupIds = new[] { group } })).EnsureSuccessStatusCode();
        var writer = factory.CreateClient(); var login = await writer.PostAsJsonAsync("/api/auth/login", new { username, password = "writer-test-password" });
        login.EnsureSuccessStatusCode(); TestSessions.UseBearer(writer, login);
        Assert.False((await Read(await writer.GetAsync("/api/auth/me")))["isAdmin"]!.GetValue<bool>());
        Guid request = Guid.NewGuid(); var verified = await Read(await Act(writer, asset, current, id: request));
        Assert.Equal(username, verified["actor"]!["username"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await Act(admin, asset, current, id: request)).StatusCode);
        var forged = new { action = "verify", definitionDigest = Digest(verified), expectedStateVersion = 1, requestUuid = Guid.NewGuid(), note = "fake", actor = "admin" };
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Url(asset) + "/actions", forged)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Act(admin, asset, verified, note: " ")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Url(asset) + "/actions", new AssetVerificationRequest("verify", Digest(verified), 1, Guid.NewGuid(), "Reference", ["file:///etc/passwd"]))).StatusCode);
    }

    [Fact]
    public async Task ConcurrentRetriesAreIdempotent_AndStaleDecisionsWaitForWriters()
    {
        var client = await Admin(); var asset = await Create("symbol", "(symbol \"Concurrent\")"); var current = await Current(client, asset);
        Guid request = Guid.NewGuid();
        var responses = await Task.WhenAll(Act(client, asset, current, id: request), Act(client, asset, current, id: request));
        Assert.True(JsonNode.DeepEquals(await Read(responses[0]), await Read(responses[1])));
        Assert.Equal(1L, await Sql("SELECT count(*) FROM asset_verification_action WHERE request_uuid=@id", ("id", request)));
        current = await Current(client, asset);
        await using var connection = new NpgsqlConnection(factory.ConnectionString); await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using var edit = new NpgsqlCommand("UPDATE symbol SET name=name || '-changed' WHERE id=@id", connection, tx);
        edit.Parameters.AddWithValue("id", asset.Id); await edit.ExecuteNonQueryAsync();
        var pending = Act(client, asset, current);
        await Task.Delay(100); Assert.False(pending.IsCompleted);
        await tx.CommitAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await pending).StatusCode);
    }

    [Fact]
    public async Task CorruptSourceAndMissingInheritanceCannotBeVerified()
    {
        var client = await Admin(); var asset = await Create("symbol", "(symbol \"Broken\" (extends \"Absent\"))");
        var current = await Current(client, asset);
        Assert.Equal("blocked", current["state"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Act(client, asset, current)).StatusCode);
        asset = await Revise(asset, "(symbol \"Broken\")");
        current = await Current(client, asset);
        await Sql("UPDATE blob SET data=decode('00','hex') WHERE hash=@hash", ("hash", asset.Hash));
        Assert.Equal("blocked", (await Current(client, asset))["state"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Act(client, asset, current)).StatusCode);
    }
}
