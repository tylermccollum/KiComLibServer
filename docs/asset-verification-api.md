# Symbol and footprint verification API v1

Verification records a check of one exact library asset and its dependencies.
It is separate from component approval and has no effect on `approvalValid` or
the part review policy. The server stores a short decision; the agent or engineer
performs the actual correctness check using the workflow in [AGENTS.md](../AGENTS.md).

## Access and discovery

Catalog readers can inspect current/retained definitions and history. A catalog
writer with effective read access can verify or flag; admin rights are not
required. A read-only token cannot mutate, even if its owner is an administrator.
Use existing bearer authentication or cookie/CSRF protections. `/api/info`
advertises `assetVerificationApiVersion: 1`. This feature requires schema 3;
the normal initializer upgrades fresh, v1, and v2 databases transactionally.

| Endpoint | Behavior |
| --- | --- |
| GET `/api/verifications?kind=symbol&status=pending&after=0&limit=50` | Discover work for `symbol` or `footprint`. Default status `pending` includes every state except verified. |
| GET `/api/verifications/{kind}/{uuid}` | Current body, digest, state/version, source URLs/hashes, provenance, and capture findings. |
| POST `/api/verifications/{kind}/{uuid}/actions` | Record `verify` or `flag` against the inspected digest and state version. |
| GET `/api/verifications/{kind}/{uuid}/history?before=0&limit=50` | Append-only decisions, including authenticated actor, timestamp, note, and references. |
| GET `/api/verifications/definitions/{digest}` | Exact retained body and provenance for a recorded decision. |
| GET `/api/verifications/definitions/{digest}/blobs/{hash}` | Retained source bytes, only if referenced by that definition. |

Discovery supports `all`, `pending`, `unverified`, `verified`, `stale`,
`needs_attention`, and `blocked`. `after` is a nonnegative asset ID cursor;
`nextAfter` is either the next cursor or null when exhausted. Follow cursors
until null, including after a full last page. Each request is one consistent
snapshot; start a new scan to see changes behind an earlier cursor. Limits are
1–100. History uses `nextBefore`; stop after an empty/short page.

Existing symbol/footprint list and detail responses expose `verified` and
`verificationState`. The catalog tables show the computed status; these are
server-derived fields, not editable source tags.

## Decision payload

```json
{
  "action": "verify",
  "definitionDigest": "sha256:<64 lowercase hex digits from inspection>",
  "expectedStateVersion": 0,
  "requestUuid": "07b8ce51-444e-4905-a067-cd4adb411308",
  "note": "Checked pad numbering, pitch, and dimensions against the package drawing.",
  "referenceUrls": ["https://manufacturer.example/package-drawing"]
}
```

Use a real inspected digest, not the placeholder above. `note` is required and
at most 4000 characters. Optional reference URLs are bounded to 10 HTTP(S) URLs,
2048 characters each, without embedded credentials. The server does not fetch
them or claim their contents are archived. Full reports and attachments are not
required. Unknown payload fields are rejected; actors and timestamps come from
authentication and the server.

An action returns HTTP 200 with kind/UUID/digest, `state`, `verified`, new
`stateVersion`, request UUID, actor, time, note, and reference URLs. An identical
request UUID/payload/actor returns the original response without another action.
Reusing it for other content, credentials, or an asset returns 409. Outdated
digest or state version also returns 409. Unknown/deleted assets return 404;
uncapturable or corrupt sources return 422. Malformed inputs return 400.
The action body limit is 64 KiB. No bulk automatic verification is performed.

## Identity, freshness, and retention

The digest is SHA-256 of a versioned RFC 8785 canonical body containing `kind`
and the captured asset. It includes the asset UUID, source hash, relevant metadata
and library identity/name, inherited symbol parents, or footprint models and
association transforms. Raw revision/commit numbers remain provenance rather
than the content identity. Asset and part definition digests are separate
contracts and must not be interchanged.

States are derived from the current capture and most recent decision:

- `unverified`: no decision has been recorded for this asset.
- `verified`: the most recent action verifies exactly the current capture.
- `needs_attention`: the most recent action flags exactly the current capture.
- `stale`: current content/dependencies differ from the last decision's digest.
- `blocked`: the complete definition cannot be captured or a blob hash fails.

Captures and retained blobs are immutable after a decision, including flags.
Actions are append-only; a new flag clears verification without erasing the
previous decision. Verification uses the existing catalog advisory lock before
reading content/state, so a concurrent edit cannot be marked verified by an
outdated decision. Source/dependency edits do not need cache invalidation jobs:
current identity is computed on read. Recreating exactly the last decided
content restores that decision, including a flag; history remains available
after asset deletion. This is intended for the current small catalog; discovery
and catalog status enrichment compute dependency captures rather than use an
unproven cache.
