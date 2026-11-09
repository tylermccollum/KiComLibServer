# Part review API v1

Review is advisory during editing. A later KiBot checker must fetch exact saved
pairs and compare the actual design with the retained definition before release.
Neither `approved` in a schematic field nor `approvalValid` alone proves that
comparison took place. KiBot enforcement is not included in this server change.

## Part fields and workflow

The KiCad HTTP library exports `KiComLibUUID`, `KiComLibRevision` (`sha256:` plus
64 lowercase hex digits), and `KiComLibReviewState`. These are server-owned.
Ordinary part and commit/import writes containing these property keys are rejected.
Existing placed components are not retroactively stamped. Refresh from the HTTP
library using the usual KiCad workflow when the release checker is introduced.

Open a part's **Review** tab to inspect its review status, compare against a
previous submission, download retained sources, and see decisions and findings.
A catalog writer submits; an administrator with effective catalog write access
approves, rejects, revokes, reopens, or revalidates. Read-only admin tokens cannot
make decisions. Self-review is allowed. Reject, revoke, and reopen require a reason.

The review status uses a filled rounded badge: yellow for unreviewed/in-review
or approval requiring validation, green for a valid approval, and red for
rejected, revoked, or other unavailable states.

Controlled changes create an unreviewed digest on demand. Restoring identical
content restores its existing state, including revocation. Approving/rejecting a
stale current definition returns 409. Historical approvals remain available until
revoked; deleting the part makes them ineligible for release. The affected-file
lookup and selection in the Review tab allow revocation of definitions sharing a
bad source file. It does not automatically revoke on asset deletion or replacement.

## Endpoints

All endpoints require catalog read access; actions additionally require the rights
above. Browser cookie requests retain existing CSRF requirements, including the
read-only POST resolver. Bearer credentials use existing token authentication.

| Method and path | Contract |
| --- | --- |
| GET `/api/parts/{uuid}/review` | Current body, provenance, digest, state/version, findings, policy, approval validity. |
| GET `/api/parts/{uuid}/review-definitions` | Up to 200 most recent submitted definitions for the part. |
| POST `/api/parts/{uuid}/review-actions` | `action`, `definitionDigest`, `expectedStateVersion`, UUID `requestUuid`, optional `comment` (max 4000 characters). |
| GET `/api/review-definitions/{digest}` | Immutable body/provenance plus separate live state, current/historical indicator, and latest validation evidence. |
| GET `/api/review-definitions/{digest}/history?before=0&limit=50` | Append-only actions, newest first. `nextBefore` is the last returned ID; stop on an empty/short page. Maximum page size 100. |
| GET `/api/review-definitions/{digest}/blobs/{hash}` | Retained bytes, only if referenced by this definition. |
| GET `/api/reviews/affected/{hash}` | Approved definitions retaining this exact source hash. |
| GET `/api/reviews/current` | Current digest/state summaries for the part list. |
| POST `/api/reviews/resolve` | Read-only JSON array of 1–200 `{partUuid, definitionDigest}` pairs. Maximum request body 128 KiB. |

Actions are `submit`, `approve`, `reject`, `revoke`, `reopen`, and `revalidate`.
An identical request UUID, payload, and actor returns the original result; changed
payload or actor returns 409. Invalid transitions and expected-version mismatches
return 409. Validation attempts are retained, including failures (422). They
advance the state version so a subsequent reviewer must reload. Submission of an
already approved current digest is a no-op with an idempotency receipt, not a new
decision. Submission with broken capture returns 422 without creating a definition.

Resolver envelope:

```json
{
  "schemaVersion": 1,
  "serverInstanceUuid": "persistent-server-uuid",
  "responseUuid": "unique-observation-uuid",
  "observedAt": "2026-11-05T00:00:00.0000000+00:00",
  "policyId": "kicomlib-review/1",
  "results": [{
    "partUuid": "part-uuid",
    "definitionDigest": "sha256:64-lowercase-hex-digits",
    "state": "approved",
    "stateVersion": 2,
    "isCurrent": true,
    "approvalValid": true,
    "reasons": [],
    "definitionUrl": "/api/review-definitions/sha256:64-lowercase-hex-digits"
  }]
}
```

Unknown/unsubmitted or wrong-part digests, deleted parts, revoked/rejected/in-review
states, unsupported definition schemas, and insufficient current-policy evidence
never pass. There is no fallback to the latest revision. Resolve all pairs for a
release in a single request when possible; a response is one database snapshot,
not a promise that approval cannot be revoked afterward. Fail closed on network,
authentication, parse, unsupported-version, missing-result, or evidence errors.
Server UUID survives backup restore; provision a new identity when intentionally
forking a restored installation into an independently authoritative server.

## Definition schema v1 and canonicalization

Digest = SHA-256 of UTF-8 RFC 8785 canonical JSON. Object keys sort by UTF-16 code
units, numbers use ECMAScript binary64 formatting, and strings retain their value.
`schemaVersion` is part of the hash. Arrays are ordered; none are treated as sets.
Golden numeric/string/hash vectors are in `backend.Tests/ReviewTests.cs`.

The body has `schemaVersion`, `part`, `primarySource`, `alternativeSource`,
`sources`, `alternatives`, `symbol`, `footprint`, `datasheets`, `datasheetUrl`:

- `part`: UUID, name, value, comment/description, custom properties (server fields excluded).
- `sources`: manufacturer part numbers in selection order (current numeric-ID order,
  with IDs omitted from the body), each with UUID, MPN, manufacturer UUID/name.
- `alternatives`: configured part order with stable part UUID and its ordered sources.
  `primarySource` and `alternativeSource` reproduce the HTTP library's first-source
  and first-alternative/second-own-source selection. Alternatives do not authorize
  substitutions automatically.
- `symbol`: UUID/name, library UUID/filename, exact blob hash, metadata, and recursive
  `parent`. Same-library and explicit `library:name` inheritance resolve transitively.
  Missing parents and cycles fail capture. Depth limit: 128; text limit: 16 MiB.
- `footprint`: UUID/name, library UUID/filename, exact blob hash, metadata, and
  ordered separately registered model associations including source position,
  transforms, hide, and opacity. 3D models are optional; an empty model list and
  a footprint with no model declarations are valid without a missing-model finding.
  Native `kicad-embed://` model references resolve to `embedded_files` in the same
  footprint and require no separate server model record. Their bytes and placement
  are retained in the footprint blob and covered by its SHA-256 identity; the
  definition body/schema remains unchanged. External references, including stock
  KiCad paths, still require registered models at their original source positions.
  Registered models contain UUID, directory, filename, and blob hash; paths and
  transforms must agree with the source. Stored float transforms compare at
  relative 1e-5 to account for the PostgreSQL `real` representation.
  Embedded capture checks unique references, file type, nonempty base64 with a
  Zstandard frame header, checksum format, and finite transforms. It does not
  decompress STEP data or verify KiCad's internal checksum; native KiCad loading
  and engineering inspection remain part of the review workflow.
- `datasheets`: linked evidence order, UUID/directory/filename/blob hash. `datasheetUrl`
  is the external URL field; it does not freeze a remote resource. General-purpose
  part resource links are not datasheet evidence in v1.

Absent optional dependencies are explicit nulls/empty arrays. Missing declared
sources yield findings, an empty revision, and unreviewed HTTP fields without
breaking normal catalog reads. Metadata uses existing source column names inside
asset bodies. Provenance is separate (source entity UUID, revision, commit ID).
Numeric DB IDs, timestamps, review decisions, inventory, supplier/order-number data,
product photos, lifecycle/search tags on the part, and operational settings are
excluded. Geometry metadata includes its own description/tags/attributes.

## Validation and concurrency

`IReviewValidator` is the extension point. Initial policy `kicomlib-review/1` checks
supported schema, canonical digest, exact retained references, blob SHA-256 integrity,
and capture-time dependency/association consistency. Missing MPN, symbol, or footprint
is a warning in this initial policy. This is not electrical, dimensional, or datasheet
correctness certification. Independent [symbol and footprint verification](asset-verification-api.md)
records an agent/engineer check without changing this approval policy. Change the
policy ID whenever mandatory rules change.
Existing approvals then require explicit revalidation of retained content. Failed
revalidation leaves the historical decision but makes approval invalid. It cannot
undo a revocation. Slow external tools and worker execution are deferred.

Review actions take a PostgreSQL transaction advisory lock before decision reads
under READ COMMITTED. BEFORE STATEMENT triggers take the same lock for every existing
controlled-table mutation path, including imports, reverts, library changes, and
cascades. Current HTTP fields and batch resolution use one repeatable-read snapshot;
tracked EF entities are not reused for HTTP fields. Saved bodies, decisions, validation
reports, and receipts reject update/delete. Referenced blobs have retention foreign
keys and cannot be changed or removed. This is application integrity protection;
a database owner can deliberately bypass triggers and must remain trusted.

Capture is on demand, with no per-edit snapshot jobs or per-part approval cache.
The current-state list is intended for the initial catalog scale; large catalogs
may need a measured cache/invalidation strategy later. Retained evidence has no
purge endpoint. Geometry preview rendering in the Overview tab remains the current
catalog view; the Review tab uses definition diffs and retained source downloads.

Machine-readable contracts: [definition v1](review-definition-v1.schema.json),
[resolver v1](review-resolve-v1.schema.json), and an independent
[canonical body/hash fixture](review-definition-v1.fixture.json).

| Existing mutation path | Tables covered by the shared lock |
| --- | --- |
| Part controller and catalog commits/imports/reverts | `part`, `part_number`, `manufacturer` |
| Alternative edits and MPN/manufacturer selection | `part`, `part_number`, `manufacturer` |
| Library/library-file controllers, cascaded deletion | `library`, `library_file`, dependent heads |
| Geometry commits and reverts | `symbol`, `symbol_revision`, `footprint`, `footprint_revision` |
| Models and model attachment changes | `model3d`, `model3d_revision`, `footprint_revision_model3d` |
| Datasheet commits/links/deletion | `datasheet`, `datasheet_revision`, `part` |
| Blob upload/mutation | `blob` (retained bytes additionally immutable) |

Uncontrolled supplier/order-number and inventory writes do not take the review
lock. HTTP field reads still include them in the same database snapshot. The lock
orders mutations and decisions, not arbitrary administrative SQL reads; production
bulk SQL operations should take the advisory lock before taking catalog row locks.
