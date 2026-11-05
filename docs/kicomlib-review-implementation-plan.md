# KiComLib review implementation plan

KiComLib will identify exactly what was reviewed, record review decisions, and
expose those decisions to a later KiBot release check. Engineers can continue
designing with unreviewed parts. Official hardware release prevention will live
in the release pipeline.

This is the implementation handoff for the `feat/kicomlib-review` branch of
`tylermccollum/KiComLibServer`. It supersedes the earlier hardware-monorepo
history and Git mirror proposal. This branch currently contains the plan;
implementation status must be reported separately.

The first delivery covers the KiComLib server and web UI. The KiBot checker,
comparison of placed geometry, and release enforcement are a separate delivery.
Completing this plan alone does not make hardware releases protected.

## Scope and initial defaults

Deliver computed KiCad fields, immutable review definitions, an audited review
workflow, a small review UI, and an authenticated API for future release checks.
Keep existing catalog editing, history, revert, and client synchronization
behavior. Basic integrity checks remain on server writes.

- Catalog readers can inspect definitions and review results. Catalog writers
  can submit the current definition for review.
- Existing administrators with effective write permission can approve, reject,
  reopen, revalidate, and revoke reviews. A read-only token cannot perform these
  actions, even if its owner is an administrator. Self-review is permitted initially;
  there is no invented two-person approval requirement.
- Approval applies to an exact definition. A newer unreviewed definition does
  not revoke an older approved definition. Revocation is an explicit action.
- Review status is informational in KiCad. The server's decision for the exact
  definition is authoritative for the future release checker.
- Human review and fast structural checks are sufficient for this delivery.
  Provide an internal validation extension point; defer automatic render jobs,
  datasheet interpretation, and external review integrations.

Do not implement a Git catalog mirror, automatic commits or PRs, an ordered
catalog export feed, a shared offline policy CLI, or draft-only library channels.
Hardware pipeline launchers are outside this repository change. Database
backups and live deployment remain separate operator work.

## Source and repository baseline

- Implementation repository: [tylermccollum/KiComLibServer](https://github.com/tylermccollum/KiComLibServer).
- Base branch: `main`.
- Base commit: `e6bdbb6c5de8adafe13b640c2f8a7dfdc26b5408`, version `1.9.0`.
- Implementation branch: `feat/kicomlib-review`.
- Upstream: `heronitec-solutions/KiComLibServer` at the same reviewed commit.
- Work directly in this fork's existing `backend/`, `backend.Tests/`,
  `frontend/`, `deployment/`, and `docs/` directories. Preserve the root
  Dockerfile, runner, version file, GPL-3.0 license, and third-party notices.
  No server import or hardware-monorepo dependency is required.

Keep migration/schema code and fixtures with the backend and its tests, local
Compose configuration under `deployment/`, and review/operator documentation
under `docs/`. Pin build dependencies and container images. Start only isolated
local services with a loopback endpoint and synthetic test data. Preserve
unrelated edits and existing repository conventions. The separate hardware
pipeline will consume the review API in a later delivery.

| Upstream source area | Work |
| --- | --- |
| `backend/Services/KiCadFieldBuilder.cs` | Already exports `KiComLibUUID`; add computed revision and review fields through a consistent definition builder. |
| `CommitService`, `CatalogRevisionService`, `RevertService` | Preserve existing history; coordinate mutations with review submission and decisions. |
| `LibraryController`, `LibraryFileController` | Include relevant naming and relationship changes in definition consistency. |
| `AccessService` and API middleware | Enforce reader, writer, and reviewer access, including token restrictions. |
| `DatabaseInitializer` | Add an ordered additive migration and explicit schema compatibility checks. |
| Frontend part list, part editor, and history views | Add status, review details, actions, and links to existing change history. |

The inspected source is not evidence of the deployed version. Verify deployment
versions during rollout preparation. Do not use a temporary inspection checkout
as a build dependency.

## Definition identity and retained content

A review definition is a frozen description of one part and the dependencies
needed to reproduce its approved library definition. Its identity is separate
from an upstream catalog revision number: changing a shared footprint can
change a part's definition without editing the part row.

Create a versioned `ReviewDefinition` contract and one server service that builds
it. The controlled body contains:

| Content | Required treatment |
| --- | --- |
| Part identity and electrical metadata | Stable part UUID, name, value, description, and custom electrical properties; exclude reserved review fields. |
| Manufacturer and source selection | Effective manufacturer/MPN fields supplied to KiCad, their stable entity UUIDs, and explicitly configured alternative manufacturer/MPN choices. Preserve primary selection. Alternatives are not automatically authorized substitutions. |
| Symbol | UUID, logical library/name, metadata, and exact source blob hash. Capture inherited symbol parents transitively; reject unresolved parents and cycles on review submission. |
| Footprint | UUID, logical library/name, relevant attributes, exact source blob hash, and ordered model associations. |
| Referenced 3D models | Stable identities, exact blob hashes, and association position, offset, rotation, scale, visibility, and opacity. |
| Datasheet evidence | Ordered linked datasheet identities and stored blob hashes, plus displayed external URLs. A URL alone is not a frozen copy of its remote contents. |

Version the inclusion rules. Exclude timestamps, database numeric keys,
review state, reviewer identity, inventory, distributor prices/order numbers,
product photos, and operational settings from the controlled body. Preserve
source commit/revision references separately as provenance. Excluding review
state avoids changing the definition identity when it is approved.

Define `definitionDigest` as `sha256:` plus the lowercase SHA-256 digest of the
UTF-8 [RFC 8785 canonical JSON](https://www.rfc-editor.org/rfc/rfc8785) body,
including its schema version. Define ordered versus unordered arrays in the
schema; sort unordered sets by stable identity. Preserve string values, reject
invalid/non-finite numeric inputs, and publish golden byte/hash fixtures. Do not
assume an ordinary serializer's sorted-key output is JCS.

This digest identifies the server definition. It is not a hash of a placed
footprint or schematic instance. The later checker must compare actual design
content using its own versioned normalization rules.

Calculate the current definition on demand from a consistent database snapshot.
The first implementation does not need to snapshot every catalog edit. Persist
the immutable body and provenance when submitted for review. An unsubmitted
digest has state `unreviewed` and cannot authorize release.

Keep incomplete parts readable and editable. Represent optional absent data
explicitly in the schema. If a declared dependency cannot be captured, return
actionable capture findings and an empty `KiComLibRevision` with `unreviewed`
state, rather than breaking the existing part response or exporting a usable
approval identity. Such a part cannot be submitted until capture succeeds.

Retain blobs referenced by submitted definitions independently of current asset
heads. A definition-to-blob reference table must prevent garbage collection or
cascading deletion of those bytes. Retain rejected and revoked definitions too.
Historical definitions must resolve without consulting mutable library names,
manufacturer rows, or current model heads.

A controlled change produces a different digest and an unreviewed current
definition. A non-controlled change leaves approval intact. Restoring exactly
the same controlled body may reuse its existing definition and current review
state; a revoked definition remains revoked until explicitly reopened and
approved again. Schema changes create new identities and never silently rewrite
old definitions.

## Review records and transitions

Add immutable definition records, append-only review actions, retained blob
references, and a transactionally maintained current review state/version.
Each action records its UUID, definition digest, action, prior/new state,
authenticated actor, server timestamp, comment, and validation evidence identity.
Preserve attribution after account rename or removal without retaining
credentials. Existing upstream commits describe library edits; review actions
are a decision history linked to those edits.

| From | Action and actor | To |
| --- | --- | --- |
| Unreviewed or rejected | Submit current definition; catalog writer | In review |
| In review | Approve; reviewer, with passing mandatory checks | Approved |
| In review | Reject; reviewer, reason required | Rejected |
| Approved | Revoke; reviewer, reason required, including historical definitions | Revoked |
| Approved | Revalidate retained definition under current requirements; reviewer | Approved, with new evidence on success |
| Revoked | Explicitly reopen current definition; reviewer, reason required | In review |

Submitting an already approved definition returns its existing state without
creating a decision. Approval/rejection require the submitted definition to
still match the current part. If it changed while the review page was open,
return a conflict and show the changed dependency. Earlier approved definitions
remain approved unless explicitly revoked.

Revalidation can target historical approved definitions. It checks retained
content, not current library heads, and appends new evidence without changing
the definition. Failed revalidation does not create a new approval; insufficient
evidence makes `approvalValid` false. Revalidation cannot clear a revocation.

Use an expected review-state version for concurrent actions. Require a request
UUID for retries: the same UUID and payload returns the original outcome;
reuse with different content is an error. Record a real repeated review as a
new action, not an overwrite of its predecessor.

Initially, deleting a part makes its definitions unavailable for new release
authorization while preserving review evidence. Geometry retained by a historical
definition remains readable; deleting or superseding its live head does not
erase that evidence or imply revocation. To prohibit use of a defective shared
asset, the UI must identify affected approved definitions and let a reviewer
explicitly revoke the selected set with a recorded reason.

## Consistency and mutation coverage

Compute KiCad fields, definition identity, and review state from the same database
snapshot. Never pair fields from one part version with approval from another.
Do not trust a cached `approved` flag on the live part row.

For the initial scale, use one transaction advisory lock shared by mutations of
controlled catalog/dependency data and review decisions. Acquire it before
reading decision-sensitive state or taking row locks; hold it through commit.
Choose isolation and retry behavior so a stale snapshot acquired before waiting
on the lock cannot approve obsolete content. Submission and approval re-read
the complete graph and compare the expected digest under this boundary.
Aborted decision transactions roll back new decisions and retained references
together. Failed validation attempts may be retained as non-authorizing reports;
they must never be mistaken for committed approval actions.

Audit coverage across part/MPN/manufacturer edits, alternative source changes,
geometry commits, model associations, library renames, imports, deletion, and
reverts. Include inherited-symbol and indirect dependency changes. Produce a
route/service coverage table. On-demand digest computation avoids requiring
background invalidation jobs to make status correct; later caches must preserve
the same semantics.

Keep existing integrity validation on writes. Incomplete metadata may remain a
warning during multi-call part creation. This delivery does not add general
optimistic concurrency to every catalog editor or repair all upstream history
coverage. It must prevent races that misattribute review approval.

## KiCad fields and existing designs

Export these fields through the existing HTTP library response:

| Field | Meaning |
| --- | --- |
| `KiComLibUUID` | Existing stable part identity. |
| `KiComLibRevision` | Current `definitionDigest`; opaque to clients, empty if a declared dependency cannot be captured. |
| `KiComLibReviewState` | State for that exact definition: `unreviewed`, `in_review`, `approved`, `rejected`, or `revoked`. |

Keep names and enum spelling stable. Present review state clearly in part
details; symbol-canvas visibility can remain off by default. Reserve these names
against editable custom properties. Reject attempts to write review state or
revision through ordinary part update/import payloads. Obtain actors from
authentication, never from a submitted reviewer name.

Continue serving unreviewed parts through existing HTTP and sync APIs. Metadata
and downloaded geometry can arrive at different times; the revision field alone
does not prove that a user's local geometry matches it.

Existing placed parts do not gain fields automatically. Document an explicit
refresh/migration procedure and test field export with synthetic designs using
a recorded compatible client/KiCad version. Do not relabel unknown historical
geometry with today's approved revision. The later checker must report missing
identity/revision as unverified and offer a separate deliberate update workflow.

## Server API for review and future release checks

Publish schemas and synthetic fixtures alongside the code. Proposed v1 endpoints:

| Endpoint | Contract |
| --- | --- |
| `GET /api/parts/{uuid}/review` | Current digest, computed state/version, definition summary, findings, and history links. |
| `POST /api/parts/{uuid}/review-actions` | Submit/approve/reject/revoke/reopen/revalidate, with digest, expected state version, request UUID, and comment. Enforce transitions and permissions. |
| `GET /api/review-definitions/{digest}` | Immutable definition and provenance; separate current decision metadata from the immutable body. |
| `GET /api/review-definitions/{digest}/history` | Paginated decision history. |
| `GET /api/review-definitions/{digest}/blobs/{hash}` | Exact retained bytes, only when referenced by that definition. |
| `POST /api/reviews/resolve` | Bounded read-only batch lookup of `(partUuid, definitionDigest)` pairs. Catalog read permission is sufficient. |

The batch response contains contract version, persistent source-instance UUID,
response UUID, observation time, and active review-policy identity. Each result
contains the requested pair, definition digest, decision ID/version, review
state, current-versus-historical status, `approvalValid`, and reason codes.
Use one consistent snapshot for all entries. Missing, unknown, revoked, deleted,
unsupported, and failed-required-check cases return explicit non-passing results.
Do not resolve a missing revision to latest. Invalid credentials and whole-request
failures use distinct HTTP errors.

`approvalValid` means the server recognizes approval for the requested definition
under the applicable review requirements. It does not mean local geometry was
compared or a board is ready for release. A historical approved definition can
return true even when the current definition is unreviewed.

Register the read-only POST explicitly in authorization middleware so a reader
token does not accidentally require write permission. Review mutations retain
normal authentication, effective write checks, and CSRF protections. Use `409`
for stale state/content, `422` for failed review validation, and machine-readable
rule IDs and entity/field paths. Specify batch limits and unsupported-version
handling in the schema.

## Review UI and validation hooks

Add status/filtering to the part list and a review panel showing the exact
definition, dependencies, changes since the previous reviewed definition,
findings, and decision history. Reuse available previews with their source
identity; label supplied previews as such. Never present a current preview as
evidence for a different historical definition.

Expose Submit, Approve, Reject, Revoke, Reopen, and Revalidate according to
permissions and state. Distinguish an approved decision with stale check evidence
from one currently valid for release lookup. On conflicts, refresh the panel and
require review of changed content.
The server enforces actions even when the UI hides the button. Provide an
affected-definitions view for shared-asset revocation; ordinary library edits
must not implicitly revoke approvals.

Define an internal `IReviewValidator` interface over an immutable definition.
Results carry rule ID, severity, entity/field path, message, definition digest,
validator identity, and policy identity. Initial mandatory checks cover schema,
resolvable dependencies, retained blob availability/hashes, and consistency
between captured fields and associations. Missing business metadata is initially
a warning; do not invent company naming or sourcing requirements.

Store findings used for each decision. Errors block approval. If an enabled
mandatory validator fails, times out, or is unavailable, approval fails. Attempts
remain separately identifiable. Changing required policy/tool identities makes
previous evidence insufficient until explicitly revalidated; the resolver
reports this rather than quietly treating it as a pass.

Initial fast validators can run with the decision transaction. Future slow
validators must consume an immutable submitted definition outside that lock and
return evidence bound to its digest. Any future job request must be persisted
atomically with submission. This delivery does not require a worker service,
webhooks, AI review, or automatic approval.

## Migration and local packaging

Add an ordered additive migration after the pinned version-1 schema. Fresh and
upgraded databases must reach the same supported state. Serialize migrations,
fail on errors/unsupported schemas, and expose the schema, company build identity,
and review API capability through `/api/info`.

Existing parts begin unreviewed. Calculate definitions lazily; do not fabricate
reviewers, backfill approvals, or create synthetic edit commits. Support populated
catalogs as well as empty/seeded databases. Inventory versions, row counts, and
seed/configuration presence without printing secrets. Preserve UUIDs, revisions,
accounts, settings, and blobs.

Use isolated PostgreSQL and server containers for migration and API tests. Stop
old writers during operator cutover: old binaries do not participate in the new
review consistency boundary. Accept review traffic only after migration succeeds.
No live schema change, credential creation, volume deletion, or deployment is
part of this coding delivery.

## Delivery increments

1. **Service and schema foundation.** Extend this fork with isolated local
   packaging, inventory, tested migrations, and definition/action/blob-reference
   schemas. Prove populated upgrades preserve data and create no approvals.
2. **Definition service and fields.** Implement deterministic capture, dependency
   closure, retained definitions on submission, and the three KiCad fields.
   Add golden digest fixtures and old-client response compatibility tests.
3. **Review workflow and API.** Add permissions, transitions, concurrency control,
   idempotency, validators, history, and batch resolution. Publish schemas and
   a fake release-checker contract test that does not need KiBot.
4. **Review UI and handoff.** Add list/detail review controls, dependency diffs,
   affected-definition revocation, operator instructions, and synthetic client
   checks. Document what the future release checker must verify.

Build/test the backend and frontend and run real PostgreSQL integration and
container smoke tests. Tests that return early because PostgreSQL is absent do
not satisfy acceptance. Do not run physical fixtures. Report commands, results,
skipped checks, route coverage, tested versions, and remaining deployment work.

## Acceptance checks

| Scenario | Required evidence |
| --- | --- |
| Fresh and populated upgrade | Same supported schema; existing data preserved; all existing parts start unreviewed. |
| Deterministic definition | Equivalent records produce identical canonical bytes/digest; controlled changes alter it; review-state changes do not. |
| Shared dependency edit | A model, footprint, symbol parent, or relevant library-name edit changes every affected current definition without a part-row edit. |
| Review preservation | Previous approved definitions/blobs remain available after edits; a new definition is unreviewed. |
| Revocation | Historical approval becomes invalid for new release checks; evidence remains readable. Shared-asset affected-definition selection is accurate. |
| Exact restoration | Identical content resolves the same definition/current state; restoration never clears revocation. |
| Stale review | A dependency edit during approval cannot attribute approval to new content; conflicting decisions return a conflict. |
| Retried action | A lost response followed by retry produces one action; mismatched request-ID reuse fails. |
| Access control | Readers resolve but cannot mutate; writers cannot approve; read-only admin tokens cannot approve; forged fields cannot establish approval. |
| KiCad fields | UUID, revision, state, and effective fields come from one snapshot; existing sync/HTTP clients still operate. |
| Missing identity | Unknown/missing digests never fall back to latest; legacy designs are reported as unverified. |
| Incomplete draft | Incomplete multi-call part creation remains usable; uncapturable dependencies yield findings and no usable revision identity. |
| Batch resolution | Approved, pending, revoked, deleted, and unknown definitions return separate explicit results from one snapshot. |
| Retained content | Submitted definitions remain reconstructable after live-head changes/deletion; hashes verify and blobs cannot be collected. |
| Inheritance and references | Missing parents, cycles, blobs, or required model references prevent submission/approval with useful findings. |
| Validator evidence | Mandatory errors/unavailability block approval; policy changes invalidate evidence; explicit revalidation of retained content can restore validity without clearing revocation; retries preserve reports. |
| UI | Reviewer sees exact submitted content, history, and conflicts; ordinary editing cannot set approval. |
| Integration boundary | Server tests establish review status and retained content without claiming placed-geometry comparison or release blocking. |

## Contract for the later KiBot delivery

The later checker will resolve exact design identities against this API, compare
actual schematic fields, embedded symbols, and placed footprints, and bind its
result to the candidate's input/output hashes. Copied state or digest fields
alone are insufficient. Define allowed instance differences, variants, DNP
fabrication coverage, and explicit exceptions in that delivery.

Ordinary builds and reproduction of archived artifacts can use pinned local
inputs. Authorizing a new official release requires a fresh authenticated lookup;
an unavailable server blocks authorization. Archive the response and comparison
evidence with the candidate, and recheck revocation at promotion if publishing
occurs later. An archived approval proves a past decision, not perpetual
authorization for a new release.

This changes the older pipeline spec's blanket offline-release assumption.
Builds still use exact local inputs and must not replace them with current server
assets during validation. Implementing the checker and enforcing it at
publication remain separate from this KiComLib-first plan.
