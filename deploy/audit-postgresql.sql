-- Run as the provisioning owner in a SEPARATE audit database.
CREATE TABLE public.audit_events (
    event_id uuid NOT NULL,
    payload_hash char(64) NOT NULL,
    timestamp timestamptz NOT NULL,
    actor_id varchar(512) NOT NULL,
    action varchar(512) NOT NULL,
    target_id uuid NULL,
    event_json text NOT NULL,
    signing_key_id varchar(128) NOT NULL,
    signature bytea NOT NULL,
    CONSTRAINT pk_audit_events PRIMARY KEY (event_id, payload_hash)
);
CREATE INDEX ix_audit_events_timestamp ON public.audit_events(timestamp DESC);
CREATE INDEX ix_audit_events_actor ON public.audit_events(actor_id, timestamp DESC);
CREATE INDEX ix_audit_events_target ON public.audit_events(target_id, timestamp DESC);
CREATE ROLE valvra_audit_writer NOLOGIN;
CREATE ROLE valvra_audit_reader NOLOGIN;
REVOKE ALL ON public.audit_events FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO valvra_audit_writer, valvra_audit_reader;
GRANT INSERT ON public.audit_events TO valvra_audit_writer;
GRANT SELECT ON public.audit_events TO valvra_audit_reader;
-- GRANT valvra_audit_writer TO your_writer_login;
-- GRANT valvra_audit_reader TO your_reader_login;
-- Login roles must not own the database/table, be superusers, inherit wider roles,
-- or have CREATE permission on the schema. Do not grant sequence privileges: none are needed.
