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
    public class SymbolController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IBlobStore _blobStore;
        private readonly DiffService _diffService;
        private readonly AssetVerificationService _verification;

        public SymbolController(ApplicationDbContext context, IBlobStore blobStore, DiffService diffService, AssetVerificationService verification)
        {
            _context = context;
            _blobStore = blobStore;
            _diffService = diffService;
            _verification = verification;
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<SymbolDTO>>> GetSymbols(
            [FromQuery] string _sort = "id",
            [FromQuery] string _order = "asc",
            [FromQuery] string? _filterby = null,
            [FromQuery] string? _filter = null)
        {
            IQueryable<SymbolDTO> query = _context.GetSymbolList();

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
                "in_bom" => _order == "asc" ? query.OrderBy(m => m.InBom) : query.OrderByDescending(m => m.InBom),
                "on_board" => _order == "asc" ? query.OrderBy(m => m.OnBoard) : query.OrderByDescending(m => m.OnBoard),
                "pin_numbers_hidden" => _order == "asc" ? query.OrderBy(m => m.PinNumbersHidden) : query.OrderByDescending(m => m.PinNumbersHidden),
                "pin_names_hidden" => _order == "asc" ? query.OrderBy(m => m.PinNamesHidden) : query.OrderByDescending(m => m.PinNamesHidden),
                "pin_names_offset" => _order == "asc" ? query.OrderBy(m => m.PinNamesOffset) : query.OrderByDescending(m => m.PinNamesOffset),
                "description" => _order == "asc" ? query.OrderBy(m => m.Description) : query.OrderByDescending(m => m.Description),
                "createdat" => _order == "asc" ? query.OrderBy(m => m.CreatedAt) : query.OrderByDescending(m => m.CreatedAt),
                "updatedat" => _order == "asc" ? query.OrderBy(m => m.UpdatedAt) : query.OrderByDescending(m => m.UpdatedAt),
                _ => query
            };

            return await _verification.WithStates("symbol", () => query.ToListAsync(), item => item.Uuid,
                (item, state) => { item.Verified = state == "verified"; item.VerificationState = state; }, HttpContext.RequestAborted);
        }

        [HttpGet("{id:int}")]
        public async Task<ActionResult<SymbolDetailDto>> GetSymbol(int id, CancellationToken cancellationToken)
        {
            return await _verification.Snapshot<ActionResult<SymbolDetailDto>>(async () =>
            {
                SymbolHeadView? head = await _context.SymbolHeadViews.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
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

                string verificationState = head.Uuid is Guid uuid
                    ? (await _verification.Current("symbol", uuid, cancellationToken))["state"]!.GetValue<string>() : "blocked";
                return new SymbolDetailDto
                {
                    Id = head.Id,
                    Verified = verificationState == "verified",
                    VerificationState = verificationState,
                    Uuid = head.Uuid,
                    LibraryFileId = head.LibraryFileId,
                    Name = head.Name,
                    Reference = head.Reference,
                    InBom = head.InBom,
                    OnBoard = head.OnBoard,
                    PinNumbersHidden = head.PinNumbersHidden,
                    PinNamesHidden = head.PinNamesHidden,
                    PinNamesOffset = head.PinNamesOffset,
                    Description = head.Description,
                    Data = data,
                    Hash = head.Hash?.Trim(),
                    FileHash = (head.FileHash ?? head.Hash)?.Trim(),
                    RevisionNo = head.RevisionNo,
                    HeadRevisionId = head.HeadRevisionId,
                    UpdatedAt = head.UpdatedAt,
                    CreatedAt = head.CreatedAt
                };
            }, cancellationToken);
        }

        [HttpGet("preview/{id:int}")]
        public async Task<ActionResult<string>> GetSymbolPreviewImage(int id, CancellationToken cancellationToken)
        {
            SymbolHeadView? head = await _context.SymbolHeadViews.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
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
            if (!await _context.Symbol.AnyAsync(s => s.Id == id, cancellationToken))
            {
                return NotFound();
            }

            var rows = await (
                from r in _context.SymbolRevisions
                join c in _context.Commits on r.CommitId equals c.Id
                where r.SymbolId == id
                orderby r.RevisionNo
                select new
                {
                    revisionId = r.Id,
                    revisionNo = r.RevisionNo,
                    isDeleted = r.IsDeleted,
                    name = r.Name,
                    libraryFileId = r.LibraryFileId,
                    hash = r.BlobHash,
                    previewBlobHash = r.PreviewBlobHash,
                    commitId = c.Id,
                    commitUuid = c.Uuid,
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
            SymbolRevision? rev = await _context.SymbolRevisions
                .FirstOrDefaultAsync(r => r.SymbolId == id && r.RevisionNo == revisionNo, cancellationToken);
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
                previewBlobHash = rev.PreviewBlobHash?.Trim(),
                reference = rev.Reference,
                inBom = rev.InBom,
                onBoard = rev.OnBoard,
                pinNumbersHidden = rev.PinNumbersHidden,
                pinNamesHidden = rev.PinNamesHidden,
                pinNamesOffset = rev.PinNamesOffset,
                description = rev.Description,
                data
            });
        }

        [HttpGet("{id:int}/diff")]
        public async Task<IActionResult> Diff(int id, [FromQuery] int from, [FromQuery] int to, CancellationToken cancellationToken)
        {
            object? diff = await _diffService.GetTextDiffAsync("symbol", id, from, to, cancellationToken);
            return diff == null ? NotFound() : Ok(diff);
        }

        [HttpPost]
        public IActionResult PostSymbol() => Gone();

        [HttpPut("{id:int}")]
        public IActionResult PutSymbol(int id) => Gone();

        [HttpDelete("{id:int}")]
        public IActionResult DeleteSymbol(int id) => Gone();

        private ObjectResult Gone() =>
            StatusCode(StatusCodes.Status410Gone, new { message = "Direct writes are disabled. Use POST /api/commit." });
    }
}
