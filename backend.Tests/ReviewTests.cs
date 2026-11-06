using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KiComLibBackend.Services.Reviews;
using Npgsql;

namespace KiComLibBackend.Tests;

public class ReviewJsonTests
{
    [Theory]
    [InlineData("-0", "0")]
    [InlineData("1e-6", "0.000001")]
    [InlineData("1e-7", "1e-7")]
    [InlineData("1e20", "100000000000000000000")]
    [InlineData("1e21", "1e+21")]
    [InlineData("333333333.33333329", "333333333.3333333")]
    [InlineData("4.50", "4.5")]
    [InlineData("2e-3", "0.002")]
    [InlineData("5e-324", "5e-324")]
    [InlineData("1.7976931348623157e308", "1.7976931348623157e+308")]
    public void JcsNumbers(string input, string expected) => Assert.Equal(expected, ReviewJson.Canonical(JsonNode.Parse(input)));

    [Fact]
    public void JcsSortsUtf16AndPreservesStrings()
    {
        var input = JsonNode.Parse("{\"z\":1,\"a\":\"€\\n\\u000f\\\"\\\\/\",\"😀\":2,\"דּ\":3}");
        Assert.Equal("{\"a\":\"€\\n\\u000f\\\"\\\\/\",\"z\":1,\"😀\":2,\"דּ\":3}", ReviewJson.Canonical(input));
        Assert.Equal("sha256:44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a", ReviewJson.Digest("{}"));
    }
}

[Collection("kicomlib-db")]
public class ReviewTests(CustomWebApplicationFactory factory)
{
    private async Task<HttpClient> Admin()
    {
        Assert.True(factory.DatabaseAvailable, "Review integration tests require a real PostgreSQL database; refusing to skip.");
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "kicomlib" });
        login.EnsureSuccessStatusCode(); TestSessions.UseBearer(client, login); return client;
    }
    private async Task<JsonObject> Read(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");
        return JsonNode.Parse(body)!.AsObject();
    }
    private Task<JsonObject> Current(HttpClient client, Guid uuid) => ReadGet(client, $"/api/parts/{uuid}/review");
    private async Task<JsonObject> ReadGet(HttpClient client, string url) => await Read(await client.GetAsync(url));
    private static string Digest(JsonObject review) => review["definitionDigest"]!.GetValue<string>();
    private Task<HttpResponseMessage> Act(HttpClient client, Guid uuid, JsonObject review, string action, Guid? request = null, string? comment = "Test decision") =>
        client.PostAsJsonAsync($"/api/parts/{uuid}/review-actions", new ReviewActionRequest(action, Digest(review), review["stateVersion"]!.GetValue<int>(), request ?? Guid.NewGuid(), comment));
    private async Task<object?> Sql(string sql, params (string, object)[] args)
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value);
        return await command.ExecuteScalarAsync();
    }
    private async Task<(int Id, Guid Uuid)> Part(HttpClient client)
    {
        var part = await Read(await client.PostAsJsonAsync("/api/part", new { name = "Review-" + Guid.NewGuid(), value = "10k", comment = "Test component" }));
        return (part["id"]!.GetValue<int>(), Guid.Parse(part["uuid"]!.GetValue<string>()));
    }
    private async Task<JsonObject> Approved(HttpClient client, Guid uuid)
    {
        var current = await Current(client, uuid);
        var submitted = await Read(await Act(client, uuid, current, "submit"));
        return await Read(await Act(client, uuid, submitted, "approve"));
    }
    private async Task<JsonObject> Resolve(HttpClient client, Guid uuid, string digest)
    {
        var result = await Read(await client.PostAsJsonAsync("/api/reviews/resolve", new[] { new ReviewPair(uuid, digest) }));
        Assert.Equal(1, result["schemaVersion"]!.GetValue<int>());
        Assert.True(Guid.TryParse(result["serverInstanceUuid"]!.GetValue<string>(), out _));
        return result["results"]![0]!.AsObject();
    }

    [Fact]
    public async Task Lifecycle_Idempotency_ExactRestore_Revocation_AndDeletion()
    {
        var client = await Admin(); var part = await Part(client);
        var initial = await Current(client, part.Uuid);
        Assert.Equal("unreviewed", initial["state"]!.GetValue<string>());
        Assert.False((await Resolve(client, part.Uuid, Digest(initial)))["approvalValid"]!.GetValue<bool>());
        var request = Guid.NewGuid();
        var submitted = await Read(await Act(client, part.Uuid, initial, "submit", request));
        var retry = await Read(await Act(client, part.Uuid, initial, "submit", request));
        Assert.True(JsonNode.DeepEquals(submitted, retry));
        Assert.Equal(HttpStatusCode.Conflict, (await Act(client, part.Uuid, initial, "submit", request, "Changed payload")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Act(client, part.Uuid, initial, "approve")).StatusCode);
        var approved = await Read(await Act(client, part.Uuid, submitted, "approve"));
        string digest = Digest(approved);
        Assert.True((await Resolve(client, part.Uuid, digest))["approvalValid"]!.GetValue<bool>());
        // Inventory/lifecycle/search metadata are deliberately outside definition v1.
        await Sql("UPDATE part SET lifecycle_status='nrnd',keywords='search-only' WHERE id=@id", ("id", part.Id));
        Assert.Equal(digest, Digest(await Current(client, part.Uuid)));
        await Sql("UPDATE part SET value='22k' WHERE id=@id", ("id", part.Id));
        var changed = await Current(client, part.Uuid);
        Assert.NotEqual(digest, Digest(changed)); Assert.Equal("unreviewed", changed["state"]!.GetValue<string>());
        Assert.True((await Resolve(client, part.Uuid, digest))["approvalValid"]!.GetValue<bool>());
        Assert.False((await Resolve(client, part.Uuid, digest))["isCurrent"]!.GetValue<bool>());
        var revoked = await Read(await Act(client, part.Uuid, approved, "revoke"));
        Assert.False((await Resolve(client, part.Uuid, digest))["approvalValid"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Conflict, (await Act(client, part.Uuid, revoked, "revalidate")).StatusCode);
        await Sql("UPDATE part SET value='10k' WHERE id=@id", ("id", part.Id));
        Assert.Equal("revoked", (await Current(client, part.Uuid))["state"]!.GetValue<string>());
        var reopened = await Read(await Act(client, part.Uuid, revoked, "reopen"));
        approved = await Read(await Act(client, part.Uuid, reopened, "approve"));
        await Sql("UPDATE review_state SET evidence_policy='old-policy' WHERE digest=@digest", ("digest", digest));
        Assert.False((await Resolve(client, part.Uuid, digest))["approvalValid"]!.GetValue<bool>());
        await Read(await Act(client, part.Uuid, approved, "revalidate"));
        Assert.True((await Resolve(client, part.Uuid, digest))["approvalValid"]!.GetValue<bool>());
        (await client.DeleteAsync("/api/part/" + part.Id)).EnsureSuccessStatusCode();
        Assert.False((await Resolve(client, part.Uuid, digest))["approvalValid"]!.GetValue<bool>());
        var retained = await ReadGet(client, "/api/review-definitions/" + digest);
        Assert.Equal("10k", retained["body"]!["part"]!["value"]!.GetValue<string>());
        Assert.False(retained["partExists"]!.GetValue<bool>());
        var history = await ReadGet(client, "/api/review-definitions/" + digest + "/history?limit=2");
        Assert.Equal(2, history["items"]!.AsArray().Count);
        Assert.Equal("admin", history["items"]![0]!["actor"]!["username"]!.GetValue<string>());
    }

    [Fact]
    public async Task Permissions_ReservedFields_AndFailClosedResolution()
    {
        var admin = await Admin(); var part = await Part(admin); var review = await Current(admin, part.Uuid);
        var token = await Read(await admin.PostAsJsonAsync("/api/token", new { name = "review-reader-" + Guid.NewGuid(), canWrite = false }));
        var reader = factory.CreateClient();
        reader.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token["token"]!.GetValue<string>());
        (await reader.GetAsync($"/api/parts/{part.Uuid}/review")).EnsureSuccessStatusCode();
        Assert.False((await Resolve(reader, part.Uuid, Digest(review)))["approvalValid"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Forbidden, (await Act(reader, part.Uuid, review, "submit")).StatusCode);
        var submitted = await Read(await Act(admin, part.Uuid, review, "submit"));
        Assert.Equal(HttpStatusCode.Forbidden, (await Act(reader, part.Uuid, submitted, "approve")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/part", new { name = "forged", properties = new { KiComLibRevision = Digest(review) } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/commit", new { changes = new[] { new { properties = new { KiComLibReviewState = "approved" } } } })).StatusCode);
        Assert.False((await Resolve(reader, Guid.NewGuid(), Digest(review)))["approvalValid"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.PostAsJsonAsync("/api/reviews/resolve", Enumerable.Repeat(new ReviewPair(part.Uuid, Digest(review)), 201))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/reviews/resolve", new[] { new ReviewPair(part.Uuid, Digest(review)) })).StatusCode);
    }

    [Fact]
    public async Task StaleDecisionWaitsForCatalogWriter_AndRejectRequiresReason()
    {
        var client = await Admin(); var part = await Part(client);
        var submitted = await Read(await Act(client, part.Uuid, await Current(client, part.Uuid), "submit"));
        Assert.Equal(HttpStatusCode.BadRequest, (await Act(client, part.Uuid, submitted, "reject", comment: " ")).StatusCode);
        await using var connection = new NpgsqlConnection(factory.ConnectionString); await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var update = new NpgsqlCommand("UPDATE part SET value='changed' WHERE id=@id", connection, transaction);
        update.Parameters.AddWithValue("id", part.Id); await update.ExecuteNonQueryAsync();
        var pending = Act(client, part.Uuid, submitted, "approve");
        await Task.Delay(100); Assert.False(pending.IsCompleted);
        await transaction.CommitAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await pending).StatusCode);
        var next = await Read(await Act(client, part.Uuid, await Current(client, part.Uuid), "submit"));
        var rejected = await Read(await Act(client, part.Uuid, next, "reject"));
        Assert.Equal("rejected", rejected["state"]!.GetValue<string>());
        Assert.Equal("in_review", (await Read(await Act(client, part.Uuid, rejected, "submit")))["state"]!.GetValue<string>());
    }

    private async Task<(int Id, string Hash)> Symbol(string name, int library, string source)
    {
        byte[] data = Encoding.UTF8.GetBytes(source); string hash = Convert.ToHexStringLower(SHA256.HashData(data));
        await Sql("INSERT INTO blob(hash,size,media_type,data) VALUES(@hash,@size,'text/plain',@data) ON CONFLICT DO NOTHING", ("hash", hash), ("size", data.Length), ("data", data));
        int commit = (int)(await Sql("INSERT INTO public.commit(message,author) VALUES('Review fixture','test') RETURNING id"))!;
        int id = (int)(await Sql("INSERT INTO symbol(library_file_id,name) VALUES(@lib,@name) RETURNING id", ("lib", library), ("name", name)))!;
        int revision = (int)(await Sql("INSERT INTO symbol_revision(symbol_id,commit_id,revision_no,library_file_id,name,blob_hash,reference) VALUES(@id,@commit,1,@lib,@name,@hash,'R') RETURNING id", ("id", id), ("commit", commit), ("lib", library), ("name", name), ("hash", hash)))!;
        await Sql("UPDATE symbol SET head_revision_id=@revision WHERE id=@id", ("revision", revision), ("id", id));
        return (id, hash);
    }

    [Fact]
    public async Task Inheritance_SharedLibraryChanges_ImmutableRetention_AndHttpFields()
    {
        var client = await Admin(); var part = await Part(client);
        int library = (int)(await Sql("INSERT INTO library_file(type,name,filename) VALUES(0,@name,@name) RETURNING id", ("name", "review-lib-" + Guid.NewGuid())))!;
        var parent = await Symbol("Parent", library, "(symbol \"Parent\" (property \"Value\" \"R\"))");
        var child = await Symbol("Child", library, "(symbol \"Child\" (extends \"Parent\"))");
        await Sql("UPDATE part SET symbol_id=@symbol WHERE id=@id", ("symbol", child.Id), ("id", part.Id));
        var current = await Current(client, part.Uuid);
        Assert.Equal(parent.Hash, current["body"]!["symbol"]!["parent"]!["blobHash"]!.GetValue<string>());
        var approved = await Approved(client, part.Uuid); string digest = Digest(approved);
        var http = await ReadGet(client, $"/api/v1/parts/{part.Id}.json");
        Assert.Equal(digest, http["fields"]!["KiComLibRevision"]!["value"]!.GetValue<string>());
        Assert.Equal("approved", http["fields"]!["KiComLibReviewState"]!["value"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/review-definitions/{digest}/blobs/{new string('0', 64)}")).StatusCode);
        var affected = JsonNode.Parse(await client.GetStringAsync("/api/reviews/affected/" + parent.Hash))!.AsArray();
        Assert.Contains(affected, x => x!["definitionDigest"]!.GetValue<string>() == digest);
        await Assert.ThrowsAsync<PostgresException>(() => Sql("UPDATE blob SET data=@data WHERE hash=@hash", ("data", Encoding.UTF8.GetBytes("bad")), ("hash", parent.Hash)));
        await Assert.ThrowsAsync<PostgresException>(() => Sql("UPDATE review_definition SET body='{}' WHERE digest=@digest", ("digest", digest)));
        await Sql("UPDATE library_file SET filename=filename || '-renamed' WHERE id=@id", ("id", library));
        Assert.NotEqual(digest, Digest(await Current(client, part.Uuid)));
        await Sql("UPDATE symbol SET deleted_at=now() WHERE id=@id", ("id", parent.Id));
        current = await Current(client, part.Uuid); Assert.Equal("", Digest(current));
        http = await ReadGet(client, $"/api/v1/parts/{part.Id}.json");
        Assert.Equal("", http["fields"]!["KiComLibRevision"]!["value"]!.GetValue<string>());
        Assert.True((await Resolve(client, part.Uuid, digest))["approvalValid"]!.GetValue<bool>());
        Assert.Equal(Encoding.UTF8.GetBytes("(symbol \"Parent\" (property \"Value\" \"R\"))"), await client.GetByteArrayAsync($"/api/review-definitions/{digest}/blobs/{parent.Hash}"));
        await Read(await Act(client, part.Uuid, approved, "revalidate"));
    }

    [Fact]
    public async Task Models_Datasheets_AndManufacturerChangesParticipateInIdentity()
    {
        var client = await Admin(); var part = await Part(client);
        int library = (int)(await Sql("INSERT INTO library_file(type,name,filename) VALUES(1,@name,@name) RETURNING id", ("name", "models-" + Guid.NewGuid())))!;
        int commit = (int)(await Sql("INSERT INTO public.commit(message,author) VALUES('models','test') RETURNING id"))!;
        async Task<string> Blob(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text); string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            await Sql("INSERT INTO blob(hash,size,media_type,data) VALUES(@hash,@size,'application/octet-stream',@data) ON CONFLICT DO NOTHING", ("hash", hash), ("size", bytes.Length), ("data", bytes)); return hash;
        }
        async Task<int> Asset(string kind, string filename, string hash)
        {
            int id = (int)(await Sql($"INSERT INTO {kind}(directory,filename) VALUES('',@filename) RETURNING id", ("filename", filename)))!;
            int revision = (int)(await Sql($"INSERT INTO {kind}_revision({kind}_id,commit_id,revision_no,filename,blob_hash) VALUES(@id,@commit,1,@filename,@hash) RETURNING id", ("id", id), ("commit", commit), ("filename", filename), ("hash", hash)))!;
            await Sql($"UPDATE {kind} SET head_revision_id=@revision WHERE id=@id", ("revision", revision), ("id", id)); return id;
        }
        string filename = "Review-" + Guid.NewGuid() + ".step";
        int model = await Asset("model3d", filename, await Blob("STEP fixture"));
        int sheet = await Asset("datasheet", "Review-" + Guid.NewGuid() + ".pdf", await Blob("PDF fixture"));
        string fpHash = await Blob("(footprint \"Fixture\" (model \"${PLACEHOLDER}/" + filename + "\" (offset (xyz 1 2 3)) (scale (xyz 1 1 1)) (rotate (xyz 0 0 0))))");
        int footprint = (int)(await Sql("INSERT INTO footprint(library_file_id,name) VALUES(@library,'Fixture') RETURNING id", ("library", library)))!;
        int fpRevision = (int)(await Sql("INSERT INTO footprint_revision(footprint_id,commit_id,revision_no,library_file_id,name,blob_hash,type,layer) VALUES(@id,@commit,1,@lib,'Fixture',@hash,0,'F.Cu') RETURNING id", ("id", footprint), ("commit", commit), ("lib", library), ("hash", fpHash)))!;
        await Sql("INSERT INTO footprint_revision_model3d(footprint_revision_id,position,model3d_id,offset_x,offset_y,offset_z) VALUES(@revision,0,@model,1,2,3)", ("revision", fpRevision), ("model", model));
        await Sql("UPDATE footprint SET head_revision_id=@revision WHERE id=@id", ("revision", fpRevision), ("id", footprint));
        await Sql("UPDATE part SET footprint_id=@footprint,datasheet_id=@sheet,datasheet_ids=json_build_array(@sheet) WHERE id=@id", ("footprint", footprint), ("sheet", sheet), ("id", part.Id));
        int maker = (int)(await Sql("INSERT INTO manufacturer(name) VALUES(@name) RETURNING id", ("name", "Maker-" + Guid.NewGuid())))!;
        await Sql("INSERT INTO part_number(part_id,manufacturer_id,manufacturer_part_number) VALUES(@part,@maker,'M1')", ("part", part.Id), ("maker", maker));
        var approved = await Approved(client, part.Uuid); string digest = Digest(approved);
        await Sql("UPDATE manufacturer SET name=name || ' revised' WHERE id=@id", ("id", maker));
        Assert.NotEqual(digest, Digest(await Current(client, part.Uuid)));
        await Sql("UPDATE footprint_revision_model3d SET offset_x=9 WHERE footprint_revision_id=@id", ("id", fpRevision));
        Assert.Equal("", Digest(await Current(client, part.Uuid)));
        await Sql("UPDATE footprint_revision_model3d SET offset_x=1 WHERE footprint_revision_id=@id", ("id", fpRevision));
        await Sql("UPDATE datasheet SET deleted_at=now() WHERE id=@id", ("id", sheet));
        Assert.Equal("", Digest(await Current(client, part.Uuid)));
        Assert.True((await Resolve(client, part.Uuid, digest))["approvalValid"]!.GetValue<bool>());
        await Read(await Act(client, part.Uuid, approved, "revalidate"));
    }

    [Fact]
    public async Task CorruptBlobCannotApprove_AndValidationFailureIsRecorded()
    {
        var client = await Admin(); var part = await Part(client);
        int library = (int)(await Sql("INSERT INTO library_file(type,name,filename) VALUES(0,@name,@name) RETURNING id", ("name", "bad-lib-" + Guid.NewGuid())))!;
        var symbol = await Symbol("Bad", library, "(symbol \"Bad\")");
        await Sql("UPDATE blob SET data=@data WHERE hash=@hash", ("data", Encoding.UTF8.GetBytes("(symbol \"Other\")")), ("hash", symbol.Hash));
        await Sql("UPDATE part SET symbol_id=@symbol WHERE id=@id", ("symbol", symbol.Id), ("id", part.Id));
        var review = await Current(client, part.Uuid);
        var response = await Act(client, part.Uuid, review, "submit");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        review = await Current(client, part.Uuid);
        response = await Act(client, part.Uuid, review, "approve");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.False((await Resolve(client, part.Uuid, Digest(review)))["approvalValid"]!.GetValue<bool>());
        Assert.Equal(2L, (long)(await Sql("SELECT count(*) FROM review_validation WHERE digest=@digest", ("digest", Digest(review))))!);
    }
}
