using KiComLibBackend.Services;
using KiComLibBackend.Services.Reviews;
using Microsoft.AspNetCore.Mvc;

namespace KiComLibBackend.Controllers;

[ApiController]
[Route("api")]
public sealed class ReviewController(ReviewService reviews) : ControllerBase
{
    [HttpGet("parts/{uuid:guid}/review")]
    public async Task<IActionResult> Current(Guid uuid, CancellationToken ct) => Ok(await reviews.Current(uuid, ct));

    [HttpGet("parts/{uuid:guid}/review-definitions")]
    public async Task<IActionResult> Definitions(Guid uuid, CancellationToken ct) => Ok(await reviews.Definitions(uuid, ct));

    [HttpPost("parts/{uuid:guid}/review-actions")]
    public async Task<IActionResult> Act(Guid uuid, [FromBody] ReviewActionRequest request, CancellationToken ct)
    {
        var result = await reviews.Act(uuid, request, CallerContext.Current(HttpContext), ct);
        return StatusCode(result.Status, result.Body);
    }

    [HttpGet("review-definitions/{digest}")]
    public async Task<IActionResult> Definition(string digest, CancellationToken ct) => Ok(await reviews.Definition(digest, ct));

    [HttpGet("review-definitions/{digest}/history")]
    public async Task<IActionResult> History(string digest, CancellationToken ct, [FromQuery] long before = 0, [FromQuery] int limit = 50) => Ok(await reviews.History(digest, before, limit, ct));

    [HttpGet("review-definitions/{digest}/blobs/{hash}")]
    public async Task<IActionResult> Blob(string digest, string hash, CancellationToken ct) => File(await reviews.RetainedBlob(digest, hash, ct), "application/octet-stream", hash);

    [HttpPost("reviews/resolve")]
    [RequestSizeLimit(131072)]
    public async Task<IActionResult> Resolve([FromBody] List<ReviewPair> pairs, CancellationToken ct) => Ok(await reviews.Resolve(pairs, ct));

    [HttpGet("reviews/current")]
    public async Task<IActionResult> States(CancellationToken ct) => Ok(await reviews.CurrentStates(ct));

    [HttpGet("reviews/affected/{hash}")]
    public async Task<IActionResult> Affected(string hash, CancellationToken ct) => Ok(await reviews.Affected(hash, ct));
}
