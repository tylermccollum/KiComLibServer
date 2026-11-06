using System.Text;
using KiComLibBackend.Data;
using KiComLibBackend.Models;
using KiComLibBackend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KiComLibBackend.Controllers
{
    [Route("api/blob")]
    [ApiController]
    public class BlobController : ControllerBase
    {
        private readonly IBlobStore _blobStore;
        private readonly long _maxBytes;

        public BlobController(IBlobStore blobStore)
        {
            _blobStore = blobStore;
            int maxMb = 200;
            if (int.TryParse(Environment.GetEnvironmentVariable("BLOB_MAX_SIZE_MB"), out int parsed) && parsed > 0)
            {
                maxMb = parsed;
            }

            _maxBytes = maxMb * 1024L * 1024L;
        }

        [HttpHead("{hash}")]
        public async Task<IActionResult> Head(string hash, CancellationToken cancellationToken)
        {
            return await _blobStore.ExistsAsync(hash, cancellationToken) ? Ok() : NotFound();
        }

        [HttpPost("exists")]
        public async Task<ActionResult<object>> Exists([FromBody] List<string> hashes, CancellationToken cancellationToken)
        {
            IReadOnlyList<string> missing = await _blobStore.FindMissingAsync(hashes ?? new List<string>(), cancellationToken);
            return Ok(new { missing });
        }

        [HttpPut("{hash}")]
        [RequestSizeLimit(209715200)]
        [RequestFormLimits(MultipartBodyLengthLimit = 209715200)]
        public async Task<IActionResult> Put(string hash, CancellationToken cancellationToken)
        {
            if (!BlobMedia.TryAccept(Request.Headers["X-Media-Type"].FirstOrDefault(), out string mediaType, out string? mediaError))
            {
                return BadRequest(new { error = mediaError });
            }

            using MemoryStream ms = new MemoryStream();
            await Request.Body.CopyToAsync(ms, cancellationToken);
            if (ms.Length > _maxBytes)
            {
                return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = $"Blob exceeds BLOB_MAX_SIZE_MB limit ({_maxBytes} bytes)" });
            }

            byte[] data = ms.ToArray();
            try
            {
                bool created = await _blobStore.PutAsync(hash, data, mediaType, cancellationToken);
                return created ? StatusCode(StatusCodes.Status201Created) : Ok();
            }
            catch (BlobHashMismatchException ex)
            {
                return BadRequest(new { error = ex.Message, expectedHash = ex.ExpectedHash, actualHash = ex.ActualHash });
            }
        }

        [HttpGet("{hash}")]
        public async Task<IActionResult> Get(string hash, CancellationToken cancellationToken)
        {
            Blob? blob = await _blobStore.GetAsync(hash, cancellationToken);
            if (blob == null || blob.Data == null)
            {
                return NotFound();
            }

            return BlobMedia.File(this, blob.Data, blob.MediaType, null, download: true);
        }
    }

    [Route("api/info")]
    [ApiController]
    public class InfoController : ControllerBase
    {
        private readonly ApplicationDbContext _db;
        private readonly IWebHostEnvironment _environment;

        public InfoController(ApplicationDbContext db, IWebHostEnvironment environment)
        {
            _db = db;
            _environment = environment;
        }

        [HttpGet]
        public async Task<ActionResult<object>> Get(CancellationToken cancellationToken)
        {
            int schemaVersion = await _db.SchemaVersions.MaxAsync(s => (int?)s.Version, cancellationToken) ?? 0;
            int? headCommitId = await _db.Commits.OrderByDescending(c => c.Id).Select(c => (int?)c.Id).FirstOrDefaultAsync(cancellationToken);
            CallerContext caller = CallerContext.Current(HttpContext);
            return Ok(new
            {
                version = ProductVersion.Read(_environment.ContentRootPath),
                httpLibraryVersion = ProductVersion.HttpLibraryApiVersion,
                apiVersion = ProductVersion.ApiVersion,
                projectsApiVersion = 1,
                reviewsApiVersion = 1,
                assetVerificationApiVersion = 1,
                reviewDefinitionSchemaVersion = 1,
                reviewPolicyId = "kicomlib-review/1",
                buildRevision = Environment.GetEnvironmentVariable("BUILD_REVISION"),
                schemaVersion,
                headCommitId,
                authenticated = caller.IsAuthenticated,
                username = caller.Username,
                canRead = caller.CanRead,
                canWrite = caller.CanWrite,
                isAdmin = caller.IsAdmin
            });
        }
    }

    [Route("api/commit")]
    [ApiController]
    public class CommitController : ControllerBase
    {
        private readonly CommitService _commitService;
        private readonly ApplicationDbContext _db;
        private readonly DiffService _diffService;
        private readonly CatalogRevisionService _catalog;
        private readonly RevertService _revert;

        public CommitController(
            CommitService commitService,
            ApplicationDbContext db,
            DiffService diffService,
            CatalogRevisionService catalog,
            RevertService revert)
        {
            _commitService = commitService;
            _db = db;
            _diffService = diffService;
            _catalog = catalog;
            _revert = revert;
        }

        [HttpPost]
        public async Task<IActionResult> Post([FromBody] CommitRequest request, CancellationToken cancellationToken)
        {
            CommitOutcome outcome = await _commitService.ApplyAsync(
                request,
                CallerContext.Current(HttpContext).Username,
                cancellationToken);
            return outcome.StatusCode switch
            {
                201 => StatusCode(201, outcome.Response),
                409 when outcome.InUse != null => Conflict(new { error = outcome.Error, inUse = outcome.InUse }),
                409 => Conflict(new { conflicts = outcome.Conflicts }),
                400 when outcome.MissingBlobs != null || outcome.UnknownModels != null =>
                    BadRequest(new { missingBlobs = outcome.MissingBlobs, unknownModels = outcome.UnknownModels }),
                _ => BadRequest(new { error = outcome.Error })
            };
        }

        [HttpPost("{id:int}/revert")]
        public async Task<IActionResult> Revert(int id, [FromBody] RevertRequest? request, CancellationToken cancellationToken)
        {
            request = WithAccountAuthor(HttpContext, request);
            RevertOutcome outcome = await _revert.RevertCommitAsync(id, request, cancellationToken);
            return RevertResult(outcome);
        }

        internal static RevertRequest WithAccountAuthor(HttpContext http, RevertRequest? request)
        {
            request ??= new RevertRequest();
            string? account = CallerContext.Current(http).Username;
            if (!string.IsNullOrWhiteSpace(account))
            {
                request.Author = account.Trim();
            }

            return request;
        }

        internal static IActionResult RevertResult(RevertOutcome outcome) =>
            outcome.StatusCode switch
            {
                201 => new ObjectResult(new { commitId = outcome.CommitId }) { StatusCode = 201 },
                409 when outcome.InUse != null => new ConflictObjectResult(new { error = outcome.Error, inUse = outcome.InUse }),
                409 => new ConflictObjectResult(new { error = outcome.Error, conflicts = outcome.Conflicts }),
                404 => new NotFoundObjectResult(new { error = outcome.Error }),
                _ => new BadRequestObjectResult(new { error = outcome.Error })
            };

        [HttpGet]
        public async Task<ActionResult<IEnumerable<object>>> List(
            [FromQuery] int _limit = 50,
            [FromQuery] int _offset = 0,
            CancellationToken cancellationToken = default)
        {
            List<Commit> commits = await _db.Commits
                .OrderByDescending(c => c.Id)
                .Skip(_offset)
                .Take(_limit)
                .ToListAsync(cancellationToken);

            List<object> result = new();
            foreach (Commit c in commits)
            {
                int changeCount =
                    await _db.SymbolRevisions.CountAsync(r => r.CommitId == c.Id, cancellationToken)
                    + await _db.FootprintRevisions.CountAsync(r => r.CommitId == c.Id, cancellationToken)
                    + await _db.Model3DRevisions.CountAsync(r => r.CommitId == c.Id, cancellationToken)
                    + await _db.DatasheetRevisions.CountAsync(r => r.CommitId == c.Id, cancellationToken)
                    + await _db.TemplateRevisions.CountAsync(r => r.CommitId == c.Id, cancellationToken)
                    + await _catalog.CountForCommitAsync(c.Id!.Value, cancellationToken);

                result.Add(new
                {
                    id = c.Id,
                    uuid = c.Uuid,
                    message = c.Message,
                    author = c.Author,
                    client = c.Client,
                    createdAt = c.CreatedAt,
                    changeCount
                });
            }

            return Ok(result);
        }

        [HttpGet("{id:int}")]
        public async Task<IActionResult> Get(int id, CancellationToken cancellationToken)
        {
            Commit? commit = await _db.Commits.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
            if (commit == null)
            {
                return NotFound();
            }

            List<object> changes = new();
            changes.AddRange(await LoadSymbolChanges(id, cancellationToken));
            changes.AddRange(await LoadFootprintChanges(id, cancellationToken));
            changes.AddRange(await LoadModel3DChanges(id, cancellationToken));
            changes.AddRange(await LoadDatasheetChanges(id, cancellationToken));
            changes.AddRange(await LoadTemplateChanges(id, cancellationToken));
            changes.AddRange(await _catalog.ChangesForCommitAsync(id, cancellationToken));

            return Ok(new
            {
                id = commit.Id,
                uuid = commit.Uuid,
                message = commit.Message,
                author = commit.Author,
                client = commit.Client,
                parentCommitId = commit.ParentCommitId,
                createdAt = commit.CreatedAt,
                changes
            });
        }

        [HttpGet("{id:int}/diff")]
        public async Task<IActionResult> Diff(int id, CancellationToken cancellationToken)
        {
            Commit? commit = await _db.Commits.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
            if (commit == null)
            {
                return NotFound();
            }

            List<object> diffs = new();

            foreach (var rev in await _db.SymbolRevisions.Where(r => r.CommitId == id).ToListAsync(cancellationToken))
            {
                int from = Math.Max(1, rev.RevisionNo - 1);
                object? d = await _diffService.GetTextDiffAsync("symbol", rev.SymbolId, from, rev.RevisionNo, cancellationToken);
                if (d != null) diffs.Add(new { entity = "symbol", id = rev.SymbolId, revisionNo = rev.RevisionNo, diff = d });
            }

            foreach (var rev in await _db.FootprintRevisions.Where(r => r.CommitId == id).ToListAsync(cancellationToken))
            {
                int from = Math.Max(1, rev.RevisionNo - 1);
                object? d = await _diffService.GetTextDiffAsync("footprint", rev.FootprintId, from, rev.RevisionNo, cancellationToken);
                if (d != null) diffs.Add(new { entity = "footprint", id = rev.FootprintId, revisionNo = rev.RevisionNo, diff = d });
            }

            foreach (var rev in await _db.Model3DRevisions.Where(r => r.CommitId == id).ToListAsync(cancellationToken))
            {
                int from = Math.Max(1, rev.RevisionNo - 1);
                object? d = await _diffService.GetBinaryDiffAsync("model3d", rev.Model3dId, from, rev.RevisionNo, cancellationToken);
                if (d != null) diffs.Add(new { entity = "model3d", id = rev.Model3dId, revisionNo = rev.RevisionNo, diff = d });
            }

            foreach (var rev in await _db.DatasheetRevisions.Where(r => r.CommitId == id).ToListAsync(cancellationToken))
            {
                int from = Math.Max(1, rev.RevisionNo - 1);
                object? d = await _diffService.GetBinaryDiffAsync("datasheet", rev.DatasheetId, from, rev.RevisionNo, cancellationToken);
                if (d != null) diffs.Add(new { entity = "datasheet", id = rev.DatasheetId, revisionNo = rev.RevisionNo, diff = d });
            }

            foreach (var rev in await _db.TemplateRevisions.Where(r => r.CommitId == id).ToListAsync(cancellationToken))
            {
                int from = Math.Max(1, rev.RevisionNo - 1);
                object? d = await _diffService.GetBinaryDiffAsync("template", rev.TemplateId, from, rev.RevisionNo, cancellationToken);
                if (d != null) diffs.Add(new { entity = "template", id = rev.TemplateId, revisionNo = rev.RevisionNo, diff = d });
            }

            diffs.AddRange(await _catalog.DiffsForCommitAsync(id, cancellationToken));

            return Ok(diffs);
        }

        private async Task<List<object>> LoadSymbolChanges(int commitId, CancellationToken cancellationToken)
        {
            var rows = await (
                from r in _db.SymbolRevisions
                join s in _db.Symbol on r.SymbolId equals s.Id
                where r.CommitId == commitId
                select new { r, s }).ToListAsync(cancellationToken);

            List<object> list = new();
            foreach (var row in rows)
            {
                SymbolRevision? prev = await _db.SymbolRevisions
                    .Where(x => x.SymbolId == row.r.SymbolId && x.RevisionNo == row.r.RevisionNo - 1)
                    .FirstOrDefaultAsync(cancellationToken);
                list.Add(new
                {
                    entity = "symbol",
                    uuid = row.s.Uuid,
                    id = row.s.Id,
                    name = row.r.Name,
                    op = InferOp(row.r.RevisionNo, row.r.IsDeleted, prev != null,
                        nameChanged: prev != null && prev.Name != row.r.Name,
                        contentUnchanged: prev != null && string.Equals(prev.BlobHash, row.r.BlobHash, StringComparison.OrdinalIgnoreCase)),
                    revisionId = row.r.Id,
                    revisionNo = row.r.RevisionNo,
                    previousRevisionId = prev?.Id,
                    isHead = row.s.HeadRevisionId == row.r.Id
                });
            }

            return list;
        }

        private async Task<List<object>> LoadFootprintChanges(int commitId, CancellationToken cancellationToken)
        {
            var rows = await (
                from r in _db.FootprintRevisions
                join s in _db.Footprint on r.FootprintId equals s.Id
                where r.CommitId == commitId
                select new { r, s }).ToListAsync(cancellationToken);

            List<object> list = new();
            foreach (var row in rows)
            {
                FootprintRevision? prev = await _db.FootprintRevisions
                    .Where(x => x.FootprintId == row.r.FootprintId && x.RevisionNo == row.r.RevisionNo - 1)
                    .FirstOrDefaultAsync(cancellationToken);
                list.Add(new
                {
                    entity = "footprint",
                    uuid = row.s.Uuid,
                    id = row.s.Id,
                    name = row.r.Name,
                    op = InferOp(row.r.RevisionNo, row.r.IsDeleted, prev != null,
                        nameChanged: prev != null && prev.Name != row.r.Name,
                        contentUnchanged: prev != null && string.Equals(prev.BlobHash, row.r.BlobHash, StringComparison.OrdinalIgnoreCase)),
                    revisionId = row.r.Id,
                    revisionNo = row.r.RevisionNo,
                    previousRevisionId = prev?.Id,
                    isHead = row.s.HeadRevisionId == row.r.Id
                });
            }

            return list;
        }

        private async Task<List<object>> LoadModel3DChanges(int commitId, CancellationToken cancellationToken)
        {
            var rows = await (
                from r in _db.Model3DRevisions
                join s in _db.Model3D on r.Model3dId equals s.Id
                where r.CommitId == commitId
                select new { r, s }).ToListAsync(cancellationToken);

            List<object> list = new();
            foreach (var row in rows)
            {
                int? prevId = await _db.Model3DRevisions
                    .Where(x => x.Model3dId == row.r.Model3dId && x.RevisionNo == row.r.RevisionNo - 1)
                    .Select(x => (int?)x.Id)
                    .FirstOrDefaultAsync(cancellationToken);
                list.Add(new
                {
                    entity = "model3d",
                    uuid = row.s.Uuid,
                    id = row.s.Id,
                    name = row.r.Filename,
                    op = InferOp(row.r.RevisionNo, row.r.IsDeleted, prevId.HasValue),
                    revisionId = row.r.Id,
                    revisionNo = row.r.RevisionNo,
                    previousRevisionId = prevId,
                    isHead = row.s.HeadRevisionId == row.r.Id
                });
            }

            return list;
        }

        private async Task<List<object>> LoadDatasheetChanges(int commitId, CancellationToken cancellationToken)
        {
            var rows = await (
                from r in _db.DatasheetRevisions
                join s in _db.Datasheet on r.DatasheetId equals s.Id
                where r.CommitId == commitId
                select new { r, s }).ToListAsync(cancellationToken);

            List<object> list = new();
            foreach (var row in rows)
            {
                int? prevId = await _db.DatasheetRevisions
                    .Where(x => x.DatasheetId == row.r.DatasheetId && x.RevisionNo == row.r.RevisionNo - 1)
                    .Select(x => (int?)x.Id)
                    .FirstOrDefaultAsync(cancellationToken);
                list.Add(new
                {
                    entity = "datasheet",
                    uuid = row.s.Uuid,
                    id = row.s.Id,
                    name = row.r.Filename,
                    op = InferOp(row.r.RevisionNo, row.r.IsDeleted, prevId.HasValue),
                    revisionId = row.r.Id,
                    revisionNo = row.r.RevisionNo,
                    previousRevisionId = prevId,
                    isHead = row.s.HeadRevisionId == row.r.Id
                });
            }

            return list;
        }

        private async Task<List<object>> LoadTemplateChanges(int commitId, CancellationToken cancellationToken)
        {
            var rows = await (
                from r in _db.TemplateRevisions
                join s in _db.Template on r.TemplateId equals s.Id
                where r.CommitId == commitId
                select new { r, s }).ToListAsync(cancellationToken);

            List<object> list = new();
            foreach (var row in rows)
            {
                int? prevId = await _db.TemplateRevisions
                    .Where(x => x.TemplateId == row.r.TemplateId && x.RevisionNo == row.r.RevisionNo - 1)
                    .Select(x => (int?)x.Id)
                    .FirstOrDefaultAsync(cancellationToken);
                list.Add(new
                {
                    entity = "template",
                    uuid = row.s.Uuid,
                    id = row.s.Id,
                    name = row.r.Filename,
                    op = InferOp(row.r.RevisionNo, row.r.IsDeleted, prevId.HasValue),
                    revisionId = row.r.Id,
                    revisionNo = row.r.RevisionNo,
                    previousRevisionId = prevId,
                    isHead = row.s.HeadRevisionId == row.r.Id
                });
            }

            return list;
        }

        private static string InferOp(int revisionNo, bool isDeleted, bool hasPrevious,
            bool nameChanged = false, bool contentUnchanged = false)
        {
            if (isDeleted) return "delete";
            if (revisionNo == 1 || !hasPrevious) return "create";
            if (nameChanged && contentUnchanged) return "rename";
            return "update";
        }
    }

    [Route("api/sync")]
    [ApiController]
    public class SyncController : ControllerBase
    {
        private readonly ApplicationDbContext _db;

        public SyncController(ApplicationDbContext db)
        {
            _db = db;
        }

        [HttpGet("changes")]
        public async Task<IActionResult> Changes([FromQuery] int sinceCommitId = 0, CancellationToken cancellationToken = default)
        {
            int? headCommitId = await _db.Commits.OrderByDescending(c => c.Id).Select(c => (int?)c.Id).FirstOrDefaultAsync(cancellationToken);

            Dictionary<string, object> latest = new();

            async Task CollectSymbol()
            {
                var rows = await (
                    from r in _db.SymbolRevisions
                    join s in _db.Symbol on r.SymbolId equals s.Id
                    where r.CommitId > sinceCommitId
                    orderby r.CommitId, r.RevisionNo
                    select new { r, s }).ToListAsync(cancellationToken);
                foreach (var row in rows)
                {
                    latest[$"symbol:{row.s.Uuid}"] = new
                    {
                        entity = "symbol",
                        uuid = row.s.Uuid,
                        id = row.s.Id,
                        op = row.r.IsDeleted ? "delete" : (row.r.RevisionNo == 1 ? "create" : "update"),
                        hash = row.r.BlobHash,
                        name = row.r.Name,
                        libraryFileId = row.r.LibraryFileId
                    };
                }
            }

            async Task CollectFootprint()
            {
                var rows = await (
                    from r in _db.FootprintRevisions
                    join s in _db.Footprint on r.FootprintId equals s.Id
                    where r.CommitId > sinceCommitId
                    orderby r.CommitId, r.RevisionNo
                    select new { r, s }).ToListAsync(cancellationToken);
                foreach (var row in rows)
                {
                    latest[$"footprint:{row.s.Uuid}"] = new
                    {
                        entity = "footprint",
                        uuid = row.s.Uuid,
                        id = row.s.Id,
                        op = row.r.IsDeleted ? "delete" : (row.r.RevisionNo == 1 ? "create" : "update"),
                        hash = row.r.BlobHash,
                        name = row.r.Name,
                        libraryFileId = row.r.LibraryFileId
                    };
                }
            }

            async Task CollectFile(string entity)
            {
                if (entity == "model3d")
                {
                    var rows = await (
                        from r in _db.Model3DRevisions
                        join s in _db.Model3D on r.Model3dId equals s.Id
                        where r.CommitId > sinceCommitId
                        orderby r.CommitId, r.RevisionNo
                        select new { r, s }).ToListAsync(cancellationToken);
                    foreach (var row in rows)
                    {
                        latest[$"model3d:{row.s.Uuid}"] = new
                        {
                            entity = "model3d",
                            uuid = row.s.Uuid,
                            id = row.s.Id,
                            op = row.r.IsDeleted ? "delete" : (row.r.RevisionNo == 1 ? "create" : "update"),
                            hash = row.r.BlobHash,
                            name = row.r.Filename,
                            directory = row.r.Directory,
                            filename = row.r.Filename
                        };
                    }
                }
                else if (entity == "datasheet")
                {
                    var rows = await (
                        from r in _db.DatasheetRevisions
                        join s in _db.Datasheet on r.DatasheetId equals s.Id
                        where r.CommitId > sinceCommitId
                        orderby r.CommitId, r.RevisionNo
                        select new { r, s }).ToListAsync(cancellationToken);
                    foreach (var row in rows)
                    {
                        latest[$"datasheet:{row.s.Uuid}"] = new
                        {
                            entity = "datasheet",
                            uuid = row.s.Uuid,
                            id = row.s.Id,
                            op = row.r.IsDeleted ? "delete" : (row.r.RevisionNo == 1 ? "create" : "update"),
                            hash = row.r.BlobHash,
                            name = row.r.Filename,
                            directory = row.r.Directory,
                            filename = row.r.Filename
                        };
                    }
                }
                else
                {
                    var rows = await (
                        from r in _db.TemplateRevisions
                        join s in _db.Template on r.TemplateId equals s.Id
                        where r.CommitId > sinceCommitId
                        orderby r.CommitId, r.RevisionNo
                        select new { r, s }).ToListAsync(cancellationToken);
                    foreach (var row in rows)
                    {
                        latest[$"template:{row.s.Uuid}"] = new
                        {
                            entity = "template",
                            uuid = row.s.Uuid,
                            id = row.s.Id,
                            op = row.r.IsDeleted ? "delete" : (row.r.RevisionNo == 1 ? "create" : "update"),
                            hash = row.r.BlobHash,
                            name = row.r.Filename,
                            directory = row.r.Directory,
                            filename = row.r.Filename
                        };
                    }
                }
            }

            await CollectSymbol();
            await CollectFootprint();
            await CollectFile("model3d");
            await CollectFile("datasheet");
            await CollectFile("template");

            return Ok(new { headCommitId, changes = latest.Values.ToList() });
        }
    }
}
