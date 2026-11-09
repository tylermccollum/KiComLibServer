using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using KiComLibBackend.Services.Reviews;

namespace KiComLibBackend.Tests;

public partial class AssetVerificationTests
{
    // Zstandard/base64 of a synthetic ISO-10303-21 document; legacy SHA-256 is
    // also accepted by KiCad's embedded-file reader. No third-party model data.
    private const string EmbeddedPayload = "KLUv/SBqvQIAIgUTF4DpAcDHw10Crk+OmD/8qtRzQ4SMOmcEy+fDC/10fOvzTyvU2qFAIYwoSZMPgMF5wQhKaVJ5C1mjfTeekMLp+g5/ON3ceuyxtqiVAQIAX5DkBDzN";
    private const string EmbeddedChecksum = "082a3ba4b2ec89ca4aa4814a96152ea19c1d1dee8f147474c3828cc124c76834";
    private const string EmbeddedFile = "(file (name \"fixture.step\") (type model) (data |" + EmbeddedPayload + "|) (checksum \"" + EmbeddedChecksum + "\"))";
    private static string EmbeddedFootprint(string extraModels = "", string? file = null) =>
        "(footprint \"Embedded\" (model \"kicad-embed://fixture.step\" (offset (xyz 0 0 0)) (scale (xyz 1 1 1)) (rotate (xyz 0 0 0))) " + extraModels + " (embedded_files " + (file ?? EmbeddedFile) + "))";

    private async Task<(Guid Uuid, JsonObject Approved)> ApprovePart(HttpClient client, Asset footprint)
    {
        var part = await Read(await client.PostAsJsonAsync("/api/part", new { name = "Optional-model-" + Guid.NewGuid(), value = "test", footprintId = footprint.Id }));
        Guid uuid = Guid.Parse(part["uuid"]!.GetValue<string>());
        var current = await Read(await client.GetAsync($"/api/parts/{uuid}/review"));
        Assert.DoesNotContain(current["findings"]!.AsArray(), finding => finding!["entityPath"]!.GetValue<string>().Contains("models", StringComparison.Ordinal));
        var submitted = await Read(await client.PostAsJsonAsync($"/api/parts/{uuid}/review-actions", new ReviewActionRequest("submit", Digest(current), 0, Guid.NewGuid(), "Fixture review")));
        var approved = await Read(await client.PostAsJsonAsync($"/api/parts/{uuid}/review-actions", new ReviewActionRequest("approve", Digest(submitted), submitted["stateVersion"]!.GetValue<int>(), Guid.NewGuid(), "Fixture approval")));
        Assert.True(approved["approvalValid"]!.GetValue<bool>());
        return (uuid, approved);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EmbeddedOrAbsentModels_DoNotRequireSeparateModelRecords(bool embedded, bool wrapped)
    {
        var client = await Admin();
        object? modelCount = await Sql("SELECT count(*) FROM model3d");
        string source = embedded ? EmbeddedFootprint() : "(footprint \"NoModel\")";
        if (wrapped) source = source.Replace(EmbeddedPayload, EmbeddedPayload[..76] + "\n" + EmbeddedPayload[76..]);
        var footprint = await Create("footprint", source);
        var current = await Current(client, footprint);
        Assert.Equal("unverified", current["state"]!.GetValue<string>());
        Assert.Empty(current["findings"]!.AsArray());
        Assert.Empty(current["body"]!["asset"]!["models"]!.AsArray());
        Assert.Single(current["blobs"]!.AsArray());
        var verified = await Read(await Act(client, footprint, current));
        Assert.True(verified["verified"]!.GetValue<bool>());
        var part = await ApprovePart(client, footprint);
        Assert.Equal(modelCount, await Sql("SELECT count(*) FROM model3d"));
        string retained = await client.GetStringAsync($"/api/review-definitions/{Digest(part.Approved)}/blobs/{footprint.Hash}");
        Assert.Equal(source, retained);
    }

    [Theory]
    [InlineData("missing-file")]
    [InlineData("wrong-name")]
    [InlineData("wrong-type")]
    [InlineData("duplicate-file")]
    [InlineData("empty-data")]
    [InlineData("invalid-base64")]
    [InlineData("invalid-frame")]
    [InlineData("missing-checksum")]
    [InlineData("invalid-transform")]
    public async Task BrokenEmbeddedReferences_AreNotTreatedAsOptionalModelAbsence(string problem)
    {
        var client = await Admin();
        string source = problem switch
        {
            "missing-file" => EmbeddedFootprint(file: ""),
            "wrong-name" => EmbeddedFootprint().Replace("kicad-embed://fixture.step", "kicad-embed://absent.step"),
            "wrong-type" => EmbeddedFootprint().Replace("(type model)", "(type other)"),
            "duplicate-file" => EmbeddedFootprint(file: EmbeddedFile + " " + EmbeddedFile),
            "empty-data" => EmbeddedFootprint().Replace(EmbeddedPayload, ""),
            "invalid-base64" => EmbeddedFootprint().Replace(EmbeddedPayload, "!invalid!"),
            "invalid-frame" => EmbeddedFootprint().Replace(EmbeddedPayload, "AAAAAA=="),
            "missing-checksum" => EmbeddedFootprint().Replace(EmbeddedChecksum, ""),
            "invalid-transform" => EmbeddedFootprint().Replace("(offset (xyz 0 0 0))", "(offset (xyz nan 0 0))"),
            _ => throw new InvalidOperationException()
        };
        var footprint = await Create("footprint", source);
        var current = await Current(client, footprint);
        Assert.Equal("blocked", current["state"]!.GetValue<string>());
        Assert.Contains(current["findings"]!.AsArray(), finding => finding!["severity"]!.GetValue<string>() == "error");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Act(client, footprint, current)).StatusCode);
    }

    [Fact]
    public async Task EmbeddedPlacementChanges_InvalidateVerificationAndPreserveHistoricalApproval()
    {
        var client = await Admin(); var footprint = await Create("footprint", EmbeddedFootprint());
        var verified = await Read(await Act(client, footprint, await Current(client, footprint)));
        var part = await ApprovePart(client, footprint);
        string oldHash = footprint.Hash;
        footprint = await Revise(footprint, EmbeddedFootprint().Replace("(offset (xyz 0 0 0))", "(offset (xyz 1 0 0))"));
        var current = await Current(client, footprint);
        Assert.Equal("stale", current["state"]!.GetValue<string>());
        Assert.NotEqual(Digest(verified), Digest(current));
        var partCurrent = await Read(await client.GetAsync($"/api/parts/{part.Uuid}/review"));
        Assert.NotEqual(Digest(part.Approved), Digest(partCurrent));
        Assert.Equal("unreviewed", partCurrent["state"]!.GetValue<string>());
        var historical = await Read(await client.PostAsJsonAsync("/api/reviews/resolve", new[] { new ReviewPair(part.Uuid, Digest(part.Approved)) }));
        Assert.True(historical["results"]![0]!["approvalValid"]!.GetValue<bool>());
        Assert.Equal(EmbeddedFootprint(), await client.GetStringAsync($"/api/verifications/definitions/{Digest(verified)}/blobs/{oldHash}"));
        await Read(await Act(client, footprint, current));
        string changedPayload = EmbeddedFootprint().Replace("(offset (xyz 0 0 0))", "(offset (xyz 1 0 0))")
            .Replace(EmbeddedPayload, "KLUv/SByBQMAokUVGXDPA0ATONV2uwRrgKYoRYHvFyuX6n99HQHS71eleuvhsd9vHR/t4AHxZcqqMulxWJauTDknQxMTNQ/Ce1gFscmA35c3LNE6ea9ftKaBFKYwqTywDQIAZ5BkBTzN")
            .Replace(EmbeddedChecksum, "695438361b0e54f3b7827e57014cb566beb79c27fb8580f2d1cd782ac2e8a420");
        footprint = await Revise(footprint, changedPayload);
        Assert.Equal("stale", (await Current(client, footprint))["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task MixedEmbeddedAndRegisteredModels_UseOriginalSourcePositions()
    {
        var client = await Admin(); string filename = "external-" + Guid.NewGuid() + ".step";
        string hash = await Blob("Synthetic external model " + filename);
        int model = (int)(await Sql("INSERT INTO model3d(directory,filename) VALUES('',@filename) RETURNING id", ("filename", filename)))!;
        int commit = (int)(await Sql("INSERT INTO public.commit(message,author) VALUES('mixed model test','test') RETURNING id"))!;
        int revision = (int)(await Sql("INSERT INTO model3d_revision(model3d_id,commit_id,revision_no,filename,blob_hash) VALUES(@id,@commit,1,@filename,@hash) RETURNING id", ("id", model), ("commit", commit), ("filename", filename), ("hash", hash)))!;
        await Sql("UPDATE model3d SET head_revision_id=@revision WHERE id=@id", ("revision", revision), ("id", model));
        var footprint = await Create("footprint", EmbeddedFootprint("(model \"" + filename + "\")"));
        await Sql("INSERT INTO footprint_revision_model3d(footprint_revision_id,position,model3d_id) VALUES(@revision,1,@model)", ("revision", footprint.Revision), ("model", model));
        var current = await Current(client, footprint);
        Assert.Equal("unverified", current["state"]!.GetValue<string>());
        Assert.Single(current["body"]!["asset"]!["models"]!.AsArray());
        Assert.Equal(1, current["body"]!["asset"]!["models"]![0]!["position"]!.GetValue<int>());
        Assert.Equal(2, current["blobs"]!.AsArray().Count);
        await Read(await Act(client, footprint, current));
        await Sql("UPDATE footprint_revision_model3d SET position=0 WHERE footprint_revision_id=@revision", ("revision", footprint.Revision));
        Assert.Equal("blocked", (await Current(client, footprint))["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task StockModelPaths_StillRequireRegistrationOrEmbeddingWhenReferenced()
    {
        var client = await Admin();
        var footprint = await Create("footprint", "(footprint \"Stock\" (model \"${KICAD10_3DMODEL_DIR}/Resistor_SMD.3dshapes/R_0402_1005Metric.step\"))");
        var current = await Current(client, footprint);
        Assert.Equal("blocked", current["state"]!.GetValue<string>());
        Assert.Contains(current["findings"]!.AsArray(), finding => finding!["message"]!.GetValue<string>().Contains("embedded", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Act(client, footprint, current)).StatusCode);
    }
}
