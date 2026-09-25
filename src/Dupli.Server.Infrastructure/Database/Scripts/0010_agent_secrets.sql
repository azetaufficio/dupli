-- Just-in-time credentials for the agent: business secrets (repository password, S3 keys, PostgreSQL
-- passwords) are no longer kept on the agent's disk. PostgreSQL passwords move here, escrowed per agent and
-- fetched by the agent per job (POST api/agents/jobs/{jobId}/credentials), the same pattern already used for
-- Agent.repository_password_protected / s3_secret_key_protected.

CREATE TABLE agent_secret (
    agent_id         uuid NOT NULL REFERENCES agent (id) ON DELETE CASCADE,
    name             text NOT NULL,
    value_protected  text NOT NULL,
    updated_at       timestamptz NOT NULL,
    PRIMARY KEY (agent_id, name)
);
