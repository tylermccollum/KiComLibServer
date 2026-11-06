# Agent workflow

## Repository work

This fork adds part review and library asset verification to KiComLibServer.
Keep upstream history, licenses, client synchronization, and ordinary catalog
editing intact. Follow the user's scope for changes, publication, and deployment.
Merging code does not deploy the service or authorize edits to the live catalog.

For backend changes, use .NET 10 and run the relevant tests against disposable
PostgreSQL 18. The integration tests must actually reach PostgreSQL; an early
return due to an unavailable database is not a passing integration check. Run
the backend build and formatting check. For frontend changes, use Node 22 and
run the production build, lint, and relevant browser tests. Preserve CRLF for C#.

## Initial symbol and footprint review workflow

Use the [asset verification API](docs/asset-verification-api.md). This workflow
records whether an agent or engineer checked one exact symbol or footprint and
its dependencies. It does not approve a component, prove compatibility with all
MPNs, or authorize a board release. Part approval remains the separate
[review workflow](docs/review-api.md).

1. **Discover.** Use the configured server URL and a scoped catalog credential.
   Read `/api/info` and require `assetVerificationApiVersion: 1`. Read pending
   work from `GET /api/verifications?kind=symbol` and repeat for `kind=footprint`.
   Follow `nextAfter` until it is null. Do not assume names are unique across
   libraries or reuse database IDs from another server.
2. **Inspect.** Fetch the item's `inspectUrl`. Save its UUID, definition digest,
   and state version. Read the body, metadata, inherited parents or models, and
   source blobs; check downloaded bytes against their SHA-256 hashes. The
   inspection snapshot is authoritative for this check. A current preview may
   help inspection but is not evidence for a different saved revision.
3. **Check the symbol.** Inspect pin numbers/names, electrical types, units and
   alternate representations, inheritance, reference, and relevant graphical
   conventions. Use manufacturer information for device-specific mappings.
   A generic symbol can be checked against its intended generic function; do
   not claim a device-specific pinout was checked when no device was selected.
4. **Check the footprint.** Inspect pad numbers, positions, dimensions, pitch,
   drills, orientation/pin-one indication, outlines, and any model associations
   and transforms. Compare with the applicable package drawing or other
   identified dimensional source. Keep units and package variants explicit.
   A valid generic package does not establish that every component using it fits.
5. **Record a small decision.** Call the item's `/actions` endpoint with `verify`
   only when the applicable checks were completed. Include the inspected digest,
   expected state version, a new request UUID, a short note describing the scope,
   and useful reference URLs. A full uploaded report is not required. A PDF is
   not mandatory when an appropriate product/package source or generic-symbol
   specification supplies the required facts. Do not invent missing evidence.
6. **Flag gaps or errors.** Use `flag` with a useful note when correctness cannot
   be established or a defect is found. Structurally broken captures already
   appear as `blocked`; diagnose their findings before trying to verify. Do not
   turn a successful parse or render into a correctness claim.
7. **Handle changes.** On HTTP 409, reload and repeat the affected checks. Never
   substitute the new digest into an old decision without inspecting the change.
   Retry a lost response with the same UUID and identical payload/credential;
   use a new UUID for a new decision. After a successful action, read back state.

When the task includes correcting assets, use the normal commit API so changes
retain their history; then inspect and verify the resulting definition. Do not
write directly to PostgreSQL, edit a synchronized client library to change server
state, or hide verification in editable tags/custom properties. Credentials stay
out of source, examples, logs, and decision notes. Source URLs are references;
the server does not download or execute them.

Existing assets start unverified. Changes to relevant content or dependencies
make verification stale or blocked. A flag clears the current verified state
and remains in append-only history. Neither verification nor ordinary editing
can set `KiComLibReviewState` or bypass the existing release review rules.
