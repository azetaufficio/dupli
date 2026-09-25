-- Tracks how long an agent has been running a version other than the one DesiredVersionResolver resolves,
-- so AlertEvaluator can raise AgentOutdated after a grace period instead of on every transient mismatch
-- (e.g. the short window between a new release being published and the agent's next heartbeat).

ALTER TABLE agent
    ADD COLUMN outdated_since timestamptz NULL;
