-- Dupli control-plane schema. Enums are stored as text (enum member names).

CREATE TABLE storage_target (
    id          uuid PRIMARY KEY,
    name        text NOT NULL UNIQUE,
    endpoint    text NOT NULL,
    bucket      text NOT NULL,
    region      text NULL,
    created_at  timestamptz NOT NULL
);

CREATE TABLE agent (
    id                              uuid PRIMARY KEY,
    name                            text NOT NULL UNIQUE,
    status                          text NOT NULL,
    hostname                        text NULL,
    machine_id                      text NULL,
    os_version                      text NULL,
    version                         text NULL,
    restic_version                  text NULL,
    last_heartbeat_at               timestamptz NULL,
    last_backup_at                  timestamptz NULL,
    free_disk_space                 bigint NULL,
    secret_hash                     text NULL,
    secret_rotated_at               timestamptz NULL,
    storage_target_id               uuid NOT NULL REFERENCES storage_target (id),
    storage_prefix                  text NOT NULL,
    s3_access_key_id                text NOT NULL,
    s3_secret_key_protected         text NOT NULL,
    repository_password_protected   text NOT NULL,
    created_at                      timestamptz NOT NULL,
    enrolled_at                     timestamptz NULL,
    last_retention_scheduled_for    timestamptz NULL,
    last_check_scheduled_for        timestamptz NULL,
    CONSTRAINT agent_storage_prefix_unique UNIQUE (storage_target_id, storage_prefix)
);

CREATE TABLE enrollment_token (
    id          uuid PRIMARY KEY,
    agent_id    uuid NOT NULL REFERENCES agent (id) ON DELETE CASCADE,
    token_hash  text NOT NULL UNIQUE,
    created_at  timestamptz NOT NULL,
    expires_at  timestamptz NOT NULL,
    used_at     timestamptz NULL
);

CREATE TABLE backup_policy (
    id                  uuid PRIMARY KEY,
    agent_id            uuid NOT NULL REFERENCES agent (id) ON DELETE CASCADE,
    name                text NOT NULL,
    cron                text NOT NULL,
    time_zone           text NOT NULL,
    enabled             boolean NOT NULL,
    keep_daily          integer NOT NULL,
    keep_weekly         integer NOT NULL,
    keep_monthly        integer NOT NULL,
    last_scheduled_for  timestamptz NULL,
    created_at          timestamptz NOT NULL,
    updated_at          timestamptz NOT NULL,
    CONSTRAINT backup_policy_name_unique UNIQUE (agent_id, name)
);

CREATE TABLE backup_source (
    id          uuid PRIMARY KEY,
    policy_id   uuid NOT NULL REFERENCES backup_policy (id) ON DELETE CASCADE,
    source_key  text NOT NULL,
    type        text NOT NULL,
    spec        jsonb NOT NULL,
    CONSTRAINT backup_source_key_unique UNIQUE (policy_id, source_key)
);

CREATE TABLE job (
    id                  uuid PRIMARY KEY,
    agent_id            uuid NOT NULL REFERENCES agent (id) ON DELETE CASCADE,
    policy_id           uuid NULL REFERENCES backup_policy (id) ON DELETE SET NULL,
    type                text NOT NULL,
    trigger             text NOT NULL,
    state               text NOT NULL,
    payload             jsonb NOT NULL,
    created_at          timestamptz NOT NULL,
    scheduled_at        timestamptz NOT NULL,
    expires_at          timestamptz NOT NULL,
    assigned_at         timestamptz NULL,
    started_at          timestamptz NULL,
    completed_at        timestamptz NULL,
    lease_until         timestamptz NULL,
    cancel_requested    boolean NOT NULL DEFAULT false,
    error               text NULL
);

CREATE INDEX job_agent_state_idx ON job (agent_id, state, scheduled_at);
CREATE INDEX job_policy_idx ON job (policy_id, created_at DESC);
CREATE INDEX job_active_lease_idx ON job (lease_until) WHERE state IN ('Assigned', 'Running');

-- Coalescing: at most one not-yet-started job per policy and type.
CREATE UNIQUE INDEX job_one_pending_per_policy
    ON job (policy_id, type) WHERE state IN ('Pending', 'Assigned') AND policy_id IS NOT NULL;

-- Same rule for system jobs (retention/check) of an agent.
CREATE UNIQUE INDEX job_one_pending_system_per_agent
    ON job (agent_id, type) WHERE state IN ('Pending', 'Assigned') AND policy_id IS NULL;

CREATE TABLE backup_run (
    id                  uuid PRIMARY KEY,
    job_id              uuid NOT NULL UNIQUE REFERENCES job (id) ON DELETE CASCADE,
    policy_id           uuid NOT NULL REFERENCES backup_policy (id) ON DELETE CASCADE,
    agent_id            uuid NOT NULL REFERENCES agent (id) ON DELETE CASCADE,
    started_at          timestamptz NOT NULL,
    completed_at        timestamptz NOT NULL,
    status              text NOT NULL,
    bytes_processed     bigint NOT NULL,
    bytes_added         bigint NOT NULL,
    items               jsonb NOT NULL,
    error_message       text NULL
);

CREATE INDEX backup_run_policy_idx ON backup_run (policy_id, completed_at DESC);

CREATE TABLE agent_log (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    agent_id    uuid NOT NULL REFERENCES agent (id) ON DELETE CASCADE,
    job_id      uuid NULL,
    timestamp   timestamptz NOT NULL,
    level       text NOT NULL,
    message     text NOT NULL,
    exception   text NULL,
    properties  jsonb NULL
);

CREATE INDEX agent_log_agent_idx ON agent_log (agent_id, timestamp DESC);
CREATE INDEX agent_log_job_idx ON agent_log (job_id) WHERE job_id IS NOT NULL;

CREATE TABLE software_release (
    id          uuid PRIMARY KEY,
    product     text NOT NULL,
    version     text NOT NULL,
    platform    text NOT NULL,
    source_url  text NOT NULL,
    sha256      text NOT NULL,
    is_current  boolean NOT NULL,
    created_at  timestamptz NOT NULL,
    CONSTRAINT software_release_unique UNIQUE (product, version, platform)
);

CREATE UNIQUE INDEX software_release_one_current
    ON software_release (product, platform) WHERE is_current;

CREATE TABLE alert (
    id                      uuid PRIMARY KEY,
    kind                    text NOT NULL,
    subject_key             text NOT NULL,
    agent_id                uuid NULL REFERENCES agent (id) ON DELETE CASCADE,
    policy_id               uuid NULL REFERENCES backup_policy (id) ON DELETE CASCADE,
    message                 text NOT NULL,
    opened_at               timestamptz NOT NULL,
    resolved_at             timestamptz NULL,
    notified_at             timestamptz NULL,
    resolved_notified_at    timestamptz NULL
);

-- Dedup: one open alert per condition.
CREATE UNIQUE INDEX alert_one_open ON alert (kind, subject_key) WHERE resolved_at IS NULL;

-- Pinned restic 0.19.1 for Windows agents (sha256 of the official release asset).
INSERT INTO software_release (id, product, version, platform, source_url, sha256, is_current, created_at)
VALUES ('6b1f7f7e-5d4a-4f7e-9d0e-2a4c1b1f0191', 'restic', '0.19.1', 'windows_amd64',
        'https://github.com/restic/restic/releases/download/v0.19.1/restic_0.19.1_windows_amd64.zip',
        'da948ad707ed690426473aaba2046cd61f8f90f6f0e7dab6be0d5796531de67d', true, now());
