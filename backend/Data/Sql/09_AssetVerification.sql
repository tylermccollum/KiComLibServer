-- Verification belongs to an exact asset/dependency snapshot, not a mutable tag.
CREATE TABLE public.asset_verification_definition (
    digest text PRIMARY KEY CHECK (digest ~ '^sha256:[0-9a-f]{64}$'),
    kind text NOT NULL CHECK (kind IN ('symbol','footprint')),
    asset_uuid uuid NOT NULL,
    body text NOT NULL,
    provenance jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE TABLE public.asset_verification_blob (
    digest text NOT NULL REFERENCES public.asset_verification_definition(digest),
    hash character(64) NOT NULL REFERENCES public.blob(hash) ON DELETE RESTRICT,
    PRIMARY KEY(digest,hash)
);
CREATE TABLE public.asset_verification_state (
    kind text NOT NULL CHECK (kind IN ('symbol','footprint')),
    asset_uuid uuid NOT NULL,
    digest text NOT NULL REFERENCES public.asset_verification_definition(digest),
    state text NOT NULL CHECK (state IN ('verified','needs_attention')),
    version integer NOT NULL CHECK (version > 0),
    PRIMARY KEY(kind,asset_uuid)
);
CREATE TABLE public.asset_verification_action (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    request_uuid uuid NOT NULL UNIQUE,
    fingerprint text NOT NULL,
    kind text NOT NULL,
    asset_uuid uuid NOT NULL,
    digest text NOT NULL REFERENCES public.asset_verification_definition(digest),
    action text NOT NULL CHECK (action IN ('verify','flag')),
    actor jsonb NOT NULL,
    note text NOT NULL,
    reference_urls jsonb NOT NULL,
    result jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE INDEX asset_verification_history ON public.asset_verification_action(kind,asset_uuid,id DESC);
DO $$ DECLARE t text; BEGIN
    FOREACH t IN ARRAY ARRAY['asset_verification_definition','asset_verification_blob','asset_verification_action'] LOOP
        EXECUTE format('CREATE TRIGGER review_immutable BEFORE UPDATE OR DELETE ON public.%I FOR EACH STATEMENT EXECUTE FUNCTION public.review_immutable()', t);
    END LOOP;
END $$;
CREATE OR REPLACE FUNCTION public.review_protect_blob() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF EXISTS(SELECT 1 FROM public.review_definition_blob WHERE hash=OLD.hash)
       OR EXISTS(SELECT 1 FROM public.asset_verification_blob WHERE hash=OLD.hash) THEN
        RAISE EXCEPTION 'Blob is retained by review or verification evidence';
    END IF;
    IF TG_OP='UPDATE' THEN RETURN NEW; END IF;
    RETURN OLD;
END $$;
INSERT INTO public.schema_version(version) VALUES (3);
