using KiComLibBackend.Services;
using KiComLibBackend.Services.Reviews;
using Microsoft.AspNetCore.Mvc;

namespace KiComLibBackend.Controllers;

[ApiController]
[Route("api/verifications")]
public sealed class AssetVerificationController(AssetVerificationService verification) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct, [FromQuery] string kind = "symbol", [FromQuery] string status = "pending", [FromQuery] int after = 0, [FromQuery] int limit = 50) =>
        Ok(await verification.List(kind, status, after, limit, ct));

    [HttpGet("{kind}/{uuid:guid}")]
    public async Task<IActionResult> Current(string kind, Guid uuid, CancellationToken ct) => Ok(await verification.Current(kind, uuid, ct));

    [HttpPost("{kind}/{uuid:guid}/actions")]
    [RequestSizeLimit(65536)]
    public async Task<IActionResult> Act(string kind, Guid uuid, [FromBody] AssetVerificationRequest request, CancellationToken ct) =>
        Ok(await verification.Act(kind, uuid, request, CallerContext.Current(HttpContext), ct));

    [HttpGet("{kind}/{uuid:guid}/history")]
    public async Task<IActionResult> History(string kind, Guid uuid, CancellationToken ct, [FromQuery] long before = 0, [FromQuery] int limit = 50) =>
        Ok(await verification.History(kind, uuid, before, limit, ct));

    [HttpGet("definitions/{digest}")]
    public async Task<IActionResult> Definition(string digest, CancellationToken ct) => Ok(await verification.Definition(digest, ct));

    [HttpGet("definitions/{digest}/blobs/{hash}")]
    public async Task<IActionResult> Blob(string digest, string hash, CancellationToken ct) => File(await verification.Blob(digest, hash, ct), "application/octet-stream", hash);
}
