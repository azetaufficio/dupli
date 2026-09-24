-- Operators allowed to use the web UI. Invited by email; the first successful Entra ID sign-in binds the row to
-- the immutable (tenant_id, object_id) pair, which is what later sign-ins and session validation match on.

CREATE TABLE operator_user (
    id              uuid PRIMARY KEY,
    email           text NOT NULL,
    role            text NOT NULL CHECK (role IN ('Owner', 'Operator', 'Viewer')),
    tenant_id       text NULL,
    object_id       text NULL,
    display_name    text NULL,
    last_login_at   timestamptz NULL,
    disabled_at     timestamptz NULL,
    created_at      timestamptz NOT NULL,
    created_by      text NOT NULL,
    updated_at      timestamptz NOT NULL,
    updated_by      text NOT NULL,
    CONSTRAINT operator_user_binding_complete CHECK ((tenant_id IS NULL) = (object_id IS NULL))
);

CREATE UNIQUE INDEX operator_user_email_unique ON operator_user (lower(email));
CREATE UNIQUE INDEX operator_user_identity_unique ON operator_user (tenant_id, object_id) WHERE object_id IS NOT NULL;
