using System.Text;
using KiComLibBackend.Services.Reviews;
using KiComLibBackend.Data;
using KiComLibBackend.Models;
using KiComLibBackend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KiComLibBackend.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class FootprintController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IBlobStore _blobStore;
        private readonly DiffService _diffService;
        private readonly AssetVerificationService _verification;

        public FootprintController(ApplicationDbContext context, IBlobStore blobStore, DiffService diffService, AssetVerificationService verification)
        {
            _context = context;
            _blobStore = blobStore;
            _diffService = diffService;
            _verification = verification;
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<FootprintDTO>>> GetFootprints(
            [FromQuery] string _sort = "id",
            [FromQuery] string _order = "asc",
            [FromQuery] string? _filterby = null,
            [FromQuery] string? _filter = null)
        {
            IQueryable<FootprintDTO> query = _context.GetFootprintList();

            if (_filterby != null && _filter != null)
            {
                switch (_filterby.ToLowerInvariant())
                {
                    case "libraryfileid":
                    case "library_file_id":
                        query = query.Where(m => m.LibraryFileId == Convert.ToInt32(_filter));
                        break;
                    case "id":
                        query = query.Where(m => m.Id == Convert.ToInt32(_filter));
                        break;
                }
            }

            query = _sort?.ToLowerInvariant() switch
            {
                "id" => _order == "asc" ? query.OrderBy(m => m.Id) : query.OrderByDescending(m => m.Id),
                "uuid" => _order == "asc" ? query.OrderBy(m => m.Uuid) : query.OrderByDescending(m => m.Uuid),
                "library_file_id" => _order == "asc" ? query.OrderBy(m => m.LibraryFileId) : query.OrderByDescending(m => m.LibraryFileId),
                "name" => _order == "asc" ? query.OrderBy(m => m.Name) : query.OrderByDescending(m => m.Name),
                "type" => _order == "asc" ? query.OrderBy(m => m.Type) : query.OrderByDescending(m => m.Type),
                "board_only" => _order == "asc" ? query.OrderBy(m => m.BoardOnly) : query.OrderByDescending(m => m.BoardOnly),
                "exclude_from_pos_files" => _order == "asc" ? query.OrderBy(m => m.ExcludeFromPosFiles) : query.OrderByDescending(m => m.ExcludeFromPosFiles),
                "exclude_from_bom" => _order == "asc" ? query.OrderBy(m => m.ExcludeFromBom) : query.OrderByDescending(m => m.ExcludeFromBom),
                "height" => _order == "asc" ? query.OrderBy(m => m.Height) : query.OrderByDescending(m => m.Height),
                "layer" => _order == "asc" ? query.OrderBy(m => m.Layer) : query.OrderByDescending(m => m.Layer),
                "description" => _order == "asc" ? query.OrderBy(m => m.Description) : query.OrderByDescending(m => m.Description),
                "tags" => _order == "asc" ? query.OrderBy(m => m.Tags) : query.OrderByDescending(m => m.Tags),
                "property" => _order == "asc" ? query.OrderBy(m => m.Property) : query.OrderByDescending(m => m.Property),
                "model3d_id" => _order == "asc" ? query.OrderBy(m => m.Model3dId) : query.OrderByDescending(m => m.Model3dId),
                "createdat" => _order == "asc" ? query.OrderBy(m => m.CreatedAt) : query.OrderByDescending(m => m.CreatedAt),
                "updatedat" => _order == "asc" ? query.OrderBy(m => m.UpdatedAt) : query.OrderByDescending(m => m.UpdatedAt),
                _ => query
            };

            return await _verification.WithStates("footprint", async () =>
            {
                List<FootprintDTO> list = await query.ToListAsync();
                await AttachModelsToListAsync(list);
                return list;
            }, item => item.Uuid, (item, state) => { item.Verified = state == "verified"; item.VerificationState = state; }, HttpContext.RequestAborted);
        }

        private async Task AttachModelsToListAsync(List<FootprintDTO> list)
        {
            List<int> revIds = list
                .Where(f => f.HeadRevisionId.HasValue)
                .Select(f => f.HeadRevisionId!.Value)
                .Distinct()
                .ToList();

            foreach (FootprintDTO fp in list)
            {
                fp.Models = new List<FootprintModelDto>();
            }

            if (revIds.Count == 0)
            {
                return;
            }

            var links = await (
                from m in _context.FootprintRevisionModel3Ds
                join md in _context.Model3D on m.Model3dId equals md.Id
                where revIds.Contains(m.FootprintRevisionId)
                orderby m.FootprintRevisionId, m.Position
                select new { m, md }).ToListAsync();

            Dictionary<int, List<FootprintModelDto>> byRev = links
                .GroupBy(x => x.m.FootprintRevisionId)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(link => new FootprintModelDto
                    {
                        Position = link.m.Position,
                        Model3dId = link.m.Model3dId,
                        Model3dUuid = link.md.Uuid,
                        Directory = link.md.Directory,
                        Filename = link.md.Filename,
                        Offset = new[] { link.m.OffsetX, link.m.OffsetY, link.m.OffsetZ },
                        Scale = new[] { link.m.ScaleX, link.m.ScaleY, link.m.ScaleZ },
                        Rotate = new[] { link.m.RotateX, link.m.RotateY, link.m.RotateZ },
                        Opacity = link.m.Opacity,
                        Hide = link.m.Hide
                    }).ToList());

            foreach (FootprintDTO fp in list)
            {
                if (fp.HeadRevisionId.HasValue && byRev.TryGetValue(fp.HeadRevisionId.Value, out List<FootprintModelDto>? models))
                {
                    fp.Models = models;
                    fp.Model3dId = models.FirstOrDefault(m => m.Position == 0)?.Model3dId ?? fp.Model3dId;
                }
            }
        }

        [HttpGet("{id:int}")]
        public async Task<ActionResult<FootprintDetailDto>> GetFootprint(int id, CancellationToken cancellationToken)
        {
            return await _verification.Snapshot<ActionResult<FootprintDetailDto>>(async () =>
            {
                FootprintHeadView? head = await _context.FootprintHeadViews.FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
                if (head == null)
                {
                    return NotFound();
                }

                string? data = null;
                if (!string.IsNullOrWhiteSpace(head.Hash))
                {
                    Blob? blob = await _blobStore.GetAsync(head.Hash, cancellationToken);
                    if (blob?.Data != null)
                    {
                        data = Encoding.UTF8.GetString(blob.Data);
                    }
                }

                List<FootprintModelDto> models = new();
                if (head.HeadRevisionId.HasValue)
                {
                    var links = await (
                        from m in _context.FootprintRevisionModel3Ds
                        join md in _context.Model3D on m.Model3dId equals md.Id
                        where m.FootprintRevisionId == head.HeadRevisionId
                        orderby m.Position
                        select new { m, md }).ToListAsync(cancellationToken);

                    foreach (var link in links)
                    {
                        models.Add(new FootprintModelDto
                        {
                            Position = link.m.Position,
                            Model3dId = link.m.Model3dId,
                            Model3dUuid = link.md.Uuid,
                            Directory = link.md.Directory,
                            Filename = link.md.Filename,
                            Offset = new[] { link.m.OffsetX, link.m.OffsetY, link.m.OffsetZ },
                            Scale = new[] { link.m.ScaleX, link.m.ScaleY, link.m.ScaleZ },
                            Rotate = new[] { link.m.RotateX, link.m.RotateY, link.m.RotateZ },
                            Opacity = link.m.Opacity,
                            Hide = link.m.Hide
                        });
                    }
                }

                string verificationState = head.Uuid is Guid uuid
                    ? (await _verification.Current("footprint", uuid, cancellationToken))["state"]!.GetValue<string>() : "blocked";
                return new FootprintDetailDto
                {
                    Id = head.Id,
                    Verified = verificationState == "verified",
                    VerificationState = verificationState,
                    Uuid = head.Uuid,
                    LibraryFileId = head.LibraryFileId,
                    Name = head.Name,
                    Type = head.Type,
                    BoardOnly = head.BoardOnly,
                    ExcludeFromPosFiles = head.ExcludeFromPosFiles,
                    ExcludeFromBom = head.ExcludeFromBom,
                    Height = head.Height,
                    Layer = head.Layer,
                    Description = head.Description,
                    Tags = head.Tags,
                    Property = head.Property,
                    Data = data,
                    Hash = head.Hash?.Trim(),
                    FileHash = (head.FileHash ?? head.Hash)?.Trim(),
                    RevisionNo = head.RevisionNo,
                    HeadRevisionId = head.HeadRevisionId,
                    Model3dId = models.FirstOrDefault(m => m.Position == 0)?.Model3dId ?? head.Model3dId,
                    Models = models,
                    UpdatedAt = head.UpdatedAt,
                    CreatedAt = head.CreatedAt
                };
            }, cancellationToken);
        }

        [HttpGet("preview/{id:int}")]
        public async Task<ActionResult<string>> GetFootprintPreviewImage(int id, CancellationToken cancellationToken)
        {
            FootprintHeadView? head = await _context.FootprintHeadViews.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
            if (head == null)
            {
                return NotFound();
            }

            if (string.IsNullOrWhiteSpace(head.PreviewBlobHash))
            {
                return "";
            }

            Blob? blob = await _blobStore.GetAsync(head.PreviewBlobHash, cancellationToken);
            return blob?.Data == null ? "" : SvgSanitizer.Sanitize(Encoding.UTF8.GetString(blob.Data));
        }

        [HttpGet("{id:int}/history")]
        public async Task<IActionResult> History(int id, CancellationToken cancellationToken)
        {
            if (!await _context.Footprint.AnyAsync(s => s.Id == id, cancellationToken))
            {
                return NotFound();
            }

            var rows = await (
                from r in _context.FootprintRevisions
                join c in _context.Commits on r.CommitId equals c.Id
                where r.FootprintId == id
                orderby r.RevisionNo
                select new
                {
                    revisionId = r.Id,
                    revisionNo = r.RevisionNo,
                    isDeleted = r.IsDeleted,
                    name = r.Name,
                    libraryFileId = r.LibraryFileId,
                    hash = r.BlobHash,
                    commitId = c.Id,
                    message = c.Message,
                    author = c.Author,
                    client = c.Client,
                    createdAt = r.CreatedAt
                }).ToListAsync(cancellationToken);

            return Ok(rows);
        }

        [HttpGet("{id:int}/revision/{revisionNo:int}")]
        public async Task<IActionResult> Revision(int id, int revisionNo, CancellationToken cancellationToken)
        {
            FootprintRevision? rev = await _context.FootprintRevisions
                .FirstOrDefaultAsync(r => r.FootprintId == id && r.RevisionNo == revisionNo, cancellationToken);
            if (rev == null)
            {
                return NotFound();
            }

            string? data = null;
            if (!string.IsNullOrWhiteSpace(rev.BlobHash))
            {
                Blob? blob = await _blobStore.GetAsync(rev.BlobHash, cancellationToken);
                if (blob?.Data != null)
                {
                    data = Encoding.UTF8.GetString(blob.Data);
                }
            }

            return Ok(new
            {
                id,
                revisionId = rev.Id,
                revisionNo = rev.RevisionNo,
                isDeleted = rev.IsDeleted,
                libraryFileId = rev.LibraryFileId,
                name = rev.Name,
                hash = rev.BlobHash?.Trim(),
                type = rev.Type,
                boardOnly = rev.BoardOnly,
                excludeFromPosFiles = rev.ExcludeFromPosFiles,
                excludeFromBom = rev.ExcludeFromBom,
                height = rev.Height,
                layer = rev.Layer,
                description = rev.Description,
                tags = rev.Tags,
                property = rev.Property,
                data
            });
        }

        [HttpGet("{id:int}/diff")]
        public async Task<IActionResult> Diff(int id, [FromQuery] int from, [FromQuery] int to, CancellationToken cancellationToken)
        {
            object? diff = await _diffService.GetTextDiffAsync("footprint", id, from, to, cancellationToken);
            return diff == null ? NotFound() : Ok(diff);
        }

        [HttpPost]
        public IActionResult PostFootprint() => Gone();

        [HttpPut("{id:int}")]
        public IActionResult PutFootprint(int id) => Gone();

        [HttpDelete("{id:int}")]
        public IActionResult DeleteFootprint(int id) => Gone();

        private ObjectResult Gone() =>
            StatusCode(StatusCodes.Status410Gone, new { message = "Direct writes are disabled. Use POST /api/commit." });
    }
}
