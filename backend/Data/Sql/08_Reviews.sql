-- Additive schema v2: immutable evidence, mutable state, append-only decisions.
CREATE TABLE public.review_server (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    instance_id uuid NOT NULL DEFAULT gen_random_uuid()
);
INSERT INTO public.review_server DEFAULT VALUES;
CREATE TABLE public.review_definition (
    digest text PRIMARY KEY CHECK (digest ~ '^sha256:[0-9a-f]{64}$'),
    part_uuid uuid NOT NULL,
    schema_version integer NOT NULL,
    body text NOT NULL,
    provenance jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE INDEX review_definition_part ON public.review_definition(part_uuid, created_at DESC);
CREATE TABLE public.review_definition_blob (
    digest text NOT NULL REFERENCES public.review_definition(digest),
    hash character(64) NOT NULL REFERENCES public.blob(hash) ON DELETE RESTRICT,
    PRIMARY KEY(digest, hash)
);
CREATE TABLE public.review_state (
    digest text PRIMARY KEY REFERENCES public.review_definition(digest),
    state text NOT NULL CHECK (state IN ('in_review','approved','rejected','revoked')),
    version integer NOT NULL CHECK (version > 0),
    evidence_policy text,
    evidence_valid boolean NOT NULL DEFAULT false
);
CREATE TABLE public.review_receipt (
    request_uuid uuid PRIMARY KEY,
    fingerprint text NOT NULL,
    status integer NOT NULL,
    result jsonb NOT NULL
);
CREATE TABLE public.review_action (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    request_uuid uuid NOT NULL UNIQUE,
    request_fingerprint text NOT NULL,
    digest text NOT NULL REFERENCES public.review_definition(digest),
    action text NOT NULL,
    actor jsonb NOT NULL,
    comment text,
    state_version integer NOT NULL,
    result jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE INDEX review_action_digest ON public.review_action(digest, id DESC);
CREATE TABLE public.review_validation (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    digest text NOT NULL REFERENCES public.review_definition(digest),
    request_uuid uuid NOT NULL REFERENCES public.review_action(request_uuid) DEFERRABLE INITIALLY DEFERRED,
    policy text NOT NULL,
    report jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE FUNCTION public.review_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN RAISE EXCEPTION 'Review evidence is append-only'; END $$;
DO $$ DECLARE t text; BEGIN
    FOREACH t IN ARRAY ARRAY['review_definition','review_definition_blob','review_action','review_validation','review_receipt'] LOOP
        EXECUTE format('CREATE TRIGGER review_immutable BEFORE UPDATE OR DELETE ON public.%I FOR EACH STATEMENT EXECUTE FUNCTION public.review_immutable()', t);
    END LOOP;
END $$;
-- Same lock is explicitly acquired BEFORE review reads. Statement triggers cover
-- all existing write paths (imports, reverts, cascading deletes, and controllers).
CREATE FUNCTION public.review_catalog_lock() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN PERFORM pg_advisory_xact_lock(1936418162, 1); RETURN NULL; END $$;
DO $$ DECLARE t text; BEGIN
    FOREACH t IN ARRAY ARRAY['part','part_number','manufacturer','library','library_file',
        'symbol','symbol_revision','footprint','footprint_revision','footprint_revision_model3d',
        'model3d','model3d_revision','datasheet','datasheet_revision','blob'] LOOP
        EXECUTE format('CREATE TRIGGER review_catalog_lock BEFORE INSERT OR UPDATE OR DELETE ON public.%I FOR EACH STATEMENT EXECUTE FUNCTION public.review_catalog_lock()', t);
    END LOOP;
END $$;
-- A retained blob cannot change underneath an approval, even through a future writer.
CREATE FUNCTION public.review_protect_blob() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF EXISTS(SELECT 1 FROM public.review_definition_blob WHERE hash = OLD.hash) THEN
        RAISE EXCEPTION 'Blob is retained by review evidence';
    END IF;
    IF TG_OP = 'UPDATE' THEN RETURN NEW; END IF;
    RETURN OLD;
END $$;
CREATE TRIGGER review_protect_blob BEFORE UPDATE OR DELETE ON public.blob FOR EACH ROW EXECUTE FUNCTION public.review_protect_blob();
INSERT INTO public.schema_version(version) VALUES (2);
