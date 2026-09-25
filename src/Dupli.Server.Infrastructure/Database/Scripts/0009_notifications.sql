-- Per-user notification preferences and the fanned-out notification feed (mail + in-app) that replaces the
-- single Notifications:*:To mailing list: recipients are now operator users with at least one channel on.

CREATE TABLE notification_preference (
    user_id     uuid NOT NULL REFERENCES operator_user (id) ON DELETE CASCADE,
    kind        text NOT NULL,
    email       boolean NOT NULL,
    in_app      boolean NOT NULL,
    PRIMARY KEY (user_id, kind)
);

CREATE TABLE notification (
    id              uuid PRIMARY KEY,
    user_id         uuid NOT NULL REFERENCES operator_user (id) ON DELETE CASCADE,
    alert_id        uuid NOT NULL REFERENCES alert (id) ON DELETE CASCADE,
    kind            text NOT NULL,
    event           text NOT NULL CHECK (event IN ('Opened', 'Resolved')),
    subject         text NOT NULL,
    body            text NOT NULL,
    agent_id        uuid NULL REFERENCES agent (id) ON DELETE CASCADE,
    policy_id       uuid NULL REFERENCES backup_policy (id) ON DELETE CASCADE,
    created_at      timestamptz NOT NULL,
    in_app          boolean NOT NULL,
    read_at         timestamptz NULL,
    email_status    text NOT NULL CHECK (email_status IN ('None', 'Pending', 'Sent', 'Failed')),
    email_attempts  int NOT NULL DEFAULT 0,
    email_sent_at   timestamptz NULL,
    email_error     text NULL
);

-- The bell / notifications page reads the newest in-app rows for one user.
CREATE INDEX notification_user_created ON notification (user_id, created_at DESC) WHERE in_app;

-- The dispatcher's mail retry pass scans this, not the whole table.
CREATE INDEX notification_email_pending ON notification (created_at) WHERE email_status = 'Pending';
