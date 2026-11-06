-- Read-only inventory. No credential or secret columns are selected.
SELECT max(version) AS schema_version FROM public.schema_version;
SELECT 'parts' AS entity, count(*) AS rows FROM public.part
UNION ALL SELECT 'symbols', count(*) FROM public.symbol
UNION ALL SELECT 'footprints', count(*) FROM public.footprint
UNION ALL SELECT 'models', count(*) FROM public.model3d
UNION ALL SELECT 'datasheets', count(*) FROM public.datasheet
UNION ALL SELECT 'blobs', count(*) FROM public.blob
UNION ALL SELECT 'accounts', count(*) FROM public.app_user
UNION ALL SELECT 'projects', count(*) FROM public.project;
SELECT pg_database_size(current_database()) AS database_bytes;
