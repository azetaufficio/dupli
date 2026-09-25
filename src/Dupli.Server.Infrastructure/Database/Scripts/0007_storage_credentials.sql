-- S3 credential rotation ("desired state via heartbeat", same pattern as the agent/restic version fields
-- from M4): the operator sets new credentials, the agent picks them up on its next heartbeat and reports
-- back the version it applied.

ALTER TABLE agent
    ADD COLUMN s3_credentials_version          integer NOT NULL DEFAULT 1,
    ADD COLUMN s3_credentials_applied_version  integer NULL,
    ADD COLUMN s3_credentials_updated_at       timestamptz NULL,
    ADD COLUMN s3_credentials_updated_by       text NULL;
