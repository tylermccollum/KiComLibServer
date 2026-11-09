using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace KiComLibBackend.Services.Reviews;

public sealed record ReviewFinding(string RuleId, string Severity, string EntityPath, string Message);
public sealed record ReviewCapture(Guid PartUuid, string Digest, JsonObject Body, JsonArray Provenance, HashSet<string> Blobs, List<ReviewFinding> Findings);

public sealed record ReviewAssetCapture(string Kind, Guid AssetUuid, string Name, string Digest, JsonObject Body, JsonArray Provenance, HashSet<string> Blobs, List<ReviewFinding> Findings);

public sealed class ReviewDefinitionBuilder(ReviewDatabase db)
{
    public Task<ReviewCapture> Capture(Guid partUuid, CancellationToken ct) => new CaptureSession(db, ct).Build(partUuid);

    public Task<ReviewAssetCapture> CaptureAsset(string kind, Guid uuid, CancellationToken ct)
    {
        AssetVerificationService.Kind(kind);
        return new CaptureSession(db, ct).BuildAsset(kind, uuid);
    }

    private sealed class CaptureSession(ReviewDatabase db, CancellationToken ct)
    {
        private readonly HashSet<string> blobs = [];
        private readonly JsonArray provenance = [];
        private readonly List<ReviewFinding> findings = [];
        private static JsonObject Pick(JsonObject row, params string[] keys)
        {
            JsonObject result = new();
            foreach (string key in keys) result[key] = row[key]?.DeepClone();
            return result;
        }
        private void Error(string path, string message) => findings.Add(new("capture.dependency", "error", path, message));
        private Task<JsonObject?> Get(string table, int id) => db.Row($"SELECT to_jsonb(t)::text FROM public.{table} t WHERE id=@id", ct, ("id", id));
        private static int Id(JsonObject row, string key) => row[key]?.GetValue<int>() ?? 0;
        private static string Text(JsonObject row, string key) => row[key]?.GetValue<string>() ?? "";
        private void Provenance(string kind, JsonObject row)
        {
            provenance.Add(new JsonObject { ["entity"] = kind, ["uuid"] = row["uuid"]?.DeepClone(), ["revision"] = row["revision_no"]?.DeepClone(), ["commitId"] = row["commit_id"]?.DeepClone() });
        }
        public async Task<ReviewCapture> Build(Guid uuid)
        {
            var part = await db.Row("SELECT to_jsonb(p)::text FROM public.part p WHERE uuid=@uuid", ct, ("uuid", uuid))
                ?? throw new ReviewException(404, "Part does not exist");
            JsonObject fields = Pick(part, "uuid", "name", "value", "comment", "properties");
            if (fields["properties"] is JsonObject properties)
                foreach (string name in properties.Select(x => x.Key).Where(ReservedReviewFields.IsReserved).ToArray()) properties.Remove(name);
            JsonArray sources = await Sources(Id(part, "id"), "sources");
            JsonArray alternatives = [];
            foreach (var id in part["alternatives"]?.AsArray() ?? [])
            {
                var alternative = await Get("part", id!.GetValue<int>());
                if (alternative == null) { Error("alternatives", "An alternative part no longer exists"); continue; }
                alternatives.Add(new JsonObject { ["partUuid"] = alternative["uuid"]?.DeepClone(), ["sources"] = await Sources(Id(alternative, "id"), "alternatives") });
            }
            JsonNode? primary = sources.FirstOrDefault()?.DeepClone();
            JsonNode? alternate = alternatives.Count > 0 ? alternatives[0]?["sources"]?.AsArray().FirstOrDefault()?.DeepClone() : sources.ElementAtOrDefault(1)?.DeepClone();
            JsonArray sheets = [];
            var ids = (part["datasheet_ids"]?.AsArray() ?? []).Select(n => n!.GetValue<int>()).ToList();
            if (ids.Count == 0 && Id(part, "datasheet_id") > 0) ids.Add(Id(part, "datasheet_id"));
            foreach (int id in ids) sheets.Add(await Asset("datasheet", id, "datasheets", []));
            JsonObject body = new()
            {
                ["schemaVersion"] = 1,
                ["part"] = fields,
                ["primarySource"] = primary,
                ["alternativeSource"] = alternate,
                ["sources"] = sources,
                ["alternatives"] = alternatives,
                ["symbol"] = await Asset("symbol", Id(part, "symbol_id"), "symbol", []),
                ["footprint"] = await Asset("footprint", Id(part, "footprint_id"), "footprint", []),
                ["datasheets"] = sheets,
                ["datasheetUrl"] = part["datasheet"]?.DeepClone()
            };
            if (primary == null) findings.Add(new("part.mpn", "warning", "primarySource", "No manufacturer part number is selected"));
            if (body["symbol"] == null) findings.Add(new("part.symbol", "warning", "symbol", "No symbol is selected"));
            if (body["footprint"] == null) findings.Add(new("part.footprint", "warning", "footprint", "No footprint is selected"));
            var catalog = await db.Rows("SELECT jsonb_build_object('entity',entity,'uuid',entity_uuid,'revision',revision_no,'commitId',commit_id)::text FROM public.catalog_revision WHERE entity='part' AND entity_uuid=@uuid ORDER BY revision_no DESC LIMIT 1", ct, ("uuid", uuid));
            foreach (var item in catalog) provenance.Add(item);
            string digest = "";
            try { if (!findings.Any(f => f.Severity == "error")) digest = ReviewJson.Digest(ReviewJson.Canonical(body)); }
            catch (FormatException ex) { Error("definition", ex.Message); }
            return new(uuid, digest, body, provenance, blobs, findings);
        }

        public async Task<ReviewAssetCapture> BuildAsset(string kind, Guid uuid)
        {
            var row = await db.Row($"SELECT to_jsonb(a)::text FROM public.{kind} a WHERE uuid=@uuid AND deleted_at IS NULL", ct, ("uuid", uuid))
                ?? throw new ReviewException(404, "Asset not found");
            JsonObject body = new() { ["schemaVersion"] = 1, ["kind"] = kind, ["asset"] = await Asset(kind, Id(row, "id"), kind, []) };
            foreach (string hash in blobs)
            {
                byte[]? data = await db.Blob(hash, ct);
                if (data == null || Convert.ToHexStringLower(SHA256.HashData(data)) != hash)
                    findings.Add(new("blob.integrity", "error", "blobs/" + hash, "Missing source or SHA-256 mismatch"));
            }
            string digest = "";
            try { if (!findings.Any(f => f.Severity == "error")) digest = ReviewJson.Digest(ReviewJson.Canonical(body)); }
            catch (FormatException ex) { Error("definition", ex.Message); }
            return new(kind, uuid, Text(row, "name"), digest, body, provenance, blobs, findings);
        }

        private async Task<JsonArray> Sources(int partId, string path)
        {
            var rows = await db.Rows("SELECT to_jsonb(p)::text FROM public.part_number p WHERE part_id=@id ORDER BY id", ct, ("id", partId));
            JsonArray result = [];
            foreach (var row in rows)
            {
                var maker = Id(row, "manufacturer_id") == 0 ? null : await Get("manufacturer", Id(row, "manufacturer_id"));
                if (Id(row, "manufacturer_id") != 0 && maker == null) Error(path, "Manufacturer is missing");
                result.Add(new JsonObject
                {
                    ["uuid"] = row["uuid"]?.DeepClone(),
                    ["mpn"] = row["manufacturer_part_number"]?.DeepClone(),
                    ["manufacturer"] = maker == null ? null : Pick(maker, "uuid", "name")
                });
            }
            return result;
        }

        private async Task<JsonObject?> Asset(string kind, int id, string path, HashSet<int> ancestors)
        {
            if (id == 0) return null;
            if (kind == "symbol" && (ancestors.Count >= 128 || !ancestors.Add(id))) { Error(path, "Symbol inheritance cycle or depth limit"); return null; }
            var row = await Get(kind + "_head_v", id);
            if (row == null || row["head_revision_id"] == null) { Error(path, $"Missing {kind} or head revision"); return null; }
            string hash = Text(row, "hash");
            var blob = hash.Length != 64 ? null : await db.Row("SELECT jsonb_build_object('available',data IS NOT NULL,'size',size)::text FROM public.blob WHERE hash=@hash", ct, ("hash", hash));
            if (blob?["available"]?.GetValue<bool>() != true) { Error(path, $"Missing {kind} source blob"); return null; }
            blobs.Add(hash); Provenance(kind, row);
            var result = Pick(row, "uuid"); result["blobHash"] = hash;
            if (kind is "symbol" or "footprint")
            {
                result["name"] = row["name"]?.DeepClone();
                var library = await Get("library_file", Id(row, "library_file_id"));
                if (library == null) Error(path, "Library is missing");
                result["library"] = library == null ? null : Pick(library, "uuid", "filename");
                result["metadata"] = kind == "symbol"
                    ? Pick(row, "reference", "in_bom", "on_board", "pin_numbers_hidden", "pin_names_hidden", "pin_names_offset", "description")
                    : Pick(row, "type", "board_only", "exclude_from_pos_files", "exclude_from_bom", "height", "layer", "description", "tags", "property");
                try
                {
                    if (blob["size"]!.GetValue<long>() > 16 * 1024 * 1024) throw new FormatException("Geometry source exceeds the 16 MiB review capture limit");
                    byte[] data = (await db.Blob(hash, ct))!;
                    var expression = KiCadExpression.Parse(new UTF8Encoding(false, true).GetString(data));
                    if (expression.Head != kind && !(kind == "footprint" && expression.Head == "module")) throw new FormatException($"Expected {kind} source expression");
                    if (kind == "symbol")
                    {
                        var parents = expression.Named("extends").ToList();
                        if (parents.Count > 1) throw new FormatException("Multiple symbol parents");
                        result["parent"] = null;
                        if (parents.Count == 1)
                        {
                            string parent = parents[0].Argument() ?? throw new FormatException("Empty parent name");
                            string parentName = parent.Contains(':') ? parent[(parent.IndexOf(':') + 1)..] : parent;
                            string parentLibrary = parent.Contains(':') ? parent[..parent.IndexOf(':')] : Text(library!, "filename");
                            var found = await db.Row("SELECT jsonb_build_object('id',s.id)::text FROM public.symbol s JOIN public.library_file l ON l.id=s.library_file_id WHERE s.deleted_at IS NULL AND s.name=@name AND l.filename=@library", ct, ("name", parentName), ("library", parentLibrary));
                            if (found == null) Error(path + ".parent", "Unresolved inherited symbol: " + parent);
                            else result["parent"] = await Asset("symbol", Id(found, "id"), path + ".parent", ancestors);
                        }
                    }
                    else
                    {
                        JsonArray models = [];
                        var associations = await db.Rows("SELECT to_jsonb(m)::text FROM public.footprint_revision_model3d m WHERE footprint_revision_id=@id ORDER BY position", ct, ("id", Id(row, "head_revision_id")));
                        var declared = expression.Named("model").ToList();
                        var embedded = new HashSet<int>();
                        for (int position = 0; position < declared.Count; position++)
                        {
                            if (!KiCadEmbeddedModel.IsReference(declared[position])) continue;
                            KiCadEmbeddedModel.Validate(expression, declared[position]);
                            embedded.Add(position);
                        }
                        var expected = Enumerable.Range(0, declared.Count).Where(position => !embedded.Contains(position)).ToHashSet();
                        var registered = associations.Select(a => Id(a, "position")).ToHashSet();
                        if (!expected.SetEquals(registered))
                            Error(path + ".models", "Referenced models must be embedded in the footprint or registered at their source positions; a footprint may omit 3D models");
                        foreach (var association in associations)
                        {
                            var model = await Asset("model3d", Id(association, "model3d_id"), path + ".models", []);
                            var item = Pick(association, "position", "offset_x", "offset_y", "offset_z", "scale_x", "scale_y", "scale_z", "rotate_x", "rotate_y", "rotate_z", "hide", "opacity");
                            item["model"] = model; models.Add(item);
                            int position = Id(association, "position");
                            if (model != null && expected.Contains(position)) CheckModel(declared[position], model, association, path + ".models[" + position + "]");
                            else Error(path + ".models", "Model positions must match source order");
                        }
                        result["models"] = models;
                    }
                }
                catch (Exception ex) when (ex is FormatException or DecoderFallbackException) { Error(path, ex.Message); }
            }
            else
            {
                result["directory"] = row["directory"]?.DeepClone(); result["filename"] = row["filename"]?.DeepClone();
            }
            return result;
        }

        private void CheckModel(KiCadExpression source, JsonObject model, JsonObject association, string path)
        {
            string sourcePath = (source.Argument() ?? "").Replace('\\', '/');
            if (sourcePath.StartsWith("${", StringComparison.Ordinal) && sourcePath.Contains('}')) sourcePath = sourcePath[(sourcePath.IndexOf('}') + 1)..];
            sourcePath = sourcePath.TrimStart('/');
            string expected = (Text(model, "directory").Trim('/') + "/" + Text(model, "filename")).TrimStart('/');
            if (sourcePath != expected) Error(path, "Model path does not match stored association");
            foreach (string transform in new[] { "offset", "scale", "rotate" })
            {
                var xyz = source.Named(transform).FirstOrDefault()?.Named("xyz").FirstOrDefault();
                for (int i = 0; i < 3; i++)
                {
                    double actual = transform == "scale" ? 1 : 0;
                    if (xyz != null && !double.TryParse(xyz.Argument(i + 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out actual)) { Error(path, "Invalid model transform"); continue; }
                    double stored = association[transform + "_" + "xyz"[i]]!.GetValue<double>();
                    if (!double.IsFinite(actual) || Math.Abs(actual - stored) > 1e-5 * Math.Max(1, Math.Abs(actual))) Error(path, "Model transform does not match stored association");
                }
            }
            bool hidden = source.Children.Any(c => c.Atom == "hide" || c.Head == "hide");
            if (hidden != (association["hide"]?.GetValue<bool>() ?? false)) Error(path, "Model visibility does not match stored association");
            var opacity = source.Named("opacity").FirstOrDefault()?.Argument();
            if (opacity != null && (!double.TryParse(opacity, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value) || Math.Abs(value - (association["opacity"]?.GetValue<double>() ?? 1)) > 1e-5)) Error(path, "Model opacity does not match stored association");
        }
    }
}
