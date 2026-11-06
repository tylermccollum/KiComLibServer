# Review schema upgrade (v1 → v2)

Build from `feat/kicomlib-review` using a fixed commit:

```sh
docker build --build-arg BUILD_REVISION="$(git rev-parse HEAD)" -t kicomlib-review:YOUR_COMMIT .
```

Run `deployment/review-inventory.sql` using the existing database container before
migration. Record the running image ID, source revision, schema version, counts,
Compose project/config path, and all volume names. Do not print environment values
or put resolved production Compose files into Git.

1. Build/test the image before the maintenance window. Save the original Compose
   file, `.env`, image ID/tag and configuration in a private directory (0700).
2. Take a custom-format PostgreSQL dump (`pg_dump -Fc --no-owner --no-acl`) and an
   archive of the data-protection keys. Retain backups/caches/repos in their existing
   named volumes. Restore the dump to an isolated PostgreSQL 18 instance and start
   the new image against it. Compare inventory and exercise authenticated API reads.
3. Stop the application, leaving PostgreSQL running. Take a final dump and key
   archive with writes stopped. Verify the dump with `pg_restore --list`.
4. Preserve the existing Compose project name and exact volume mappings when moving
   directories. Mark existing named volumes external so absent volumes fail visibly.
   Preserve any existing backup helper and its absolute bind paths. A locally generated
   `deployment/compose.local.json` may contain resolved secrets: mode 0600 and ignored
   by Git. Use explicit `-p PROJECT -f deployment/compose.local.json` on all commands.
5. Update only the application image to the fixed tested tag and start it. Startup
   transactionally applies migration 08. Fresh databases apply baseline v1 then v2;
   existing rows are not rewritten and begin unreviewed on demand. Migration errors,
   partial schemas, and newer unsupported schemas abort startup. With `DB_AUTO_INIT=false`,
   startup validates the exact schema instead of migrating.
6. Check running image/build revision, schema 2, original inventory/accounts, logs,
   HTTP library output, resolver permission behavior, and frontend assets. Keep the
   pre-migration configuration and dump for rollback. No catalog part is approved
   automatically and no KiBot release enforcement is installed by this upgrade.

Rollback after a failed cutover: keep the application stopped, preserve a failure
snapshot, restore the pre-upgrade database dump to the original DB and restore keys
if changed, then start the recorded old application image with the original Compose
configuration. Rehearse restoration on an isolated database first. Never run
`docker compose down -v` against the live project. A rollback after users resume
writing must reconcile post-cutover data rather than blindly restoring an old dump.
