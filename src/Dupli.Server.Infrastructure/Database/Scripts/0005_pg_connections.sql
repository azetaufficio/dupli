-- Postgres connection data moves from per-policy backup_source specs to agent-level pg_connection rows,
-- referenced by policy sources via connectionId. This survives policy deletion (restore into a new
-- database no longer needs the original policy/source to still exist).

CREATE TABLE pg_connection (
    id              uuid PRIMARY KEY,
    agent_id        uuid NOT NULL REFERENCES agent (id) ON DELETE CASCADE,
    name            text NOT NULL,
    host            text NOT NULL,
    port            integer NOT NULL,
    username        text NOT NULL,
    password_secret text NOT NULL,
    bin_directory   text NULL,
    created_at      timestamptz NOT NULL,
    updated_at      timestamptz NOT NULL,
    CONSTRAINT pg_connection_name_unique UNIQUE (agent_id, name)
);

-- One connection per distinct (agent, host, port, username, passwordSecret, binDirectory) combination found
-- in existing PostgreSql backup_source specs. Host/port fall back to the DTO defaults when absent from an
-- older spec; name collisions (same username@host:port, different secret/bin directory) get a "-2", "-3"... suffix.
WITH pg_sources AS (
    SELECT
        bp.agent_id,
        COALESCE(bs.spec ->> 'host', 'localhost') AS host,
        COALESCE((bs.spec ->> 'port')::int, 5432) AS port,
        bs.spec ->> 'username' AS username,
        bs.spec ->> 'passwordSecret' AS password_secret,
        bs.spec ->> 'binDirectory' AS bin_directory
    FROM backup_source bs
    JOIN backup_policy bp ON bp.id = bs.policy_id
    WHERE bs.type = 'PostgreSql'
),
distinct_combos AS (
    SELECT DISTINCT agent_id, host, port, username, password_secret, bin_directory
    FROM pg_sources
),
named AS (
    SELECT
        gen_random_uuid() AS id,
        agent_id, host, port, username, password_secret, bin_directory,
        username || '@' || host || ':' || port AS base_name,
        row_number() OVER (PARTITION BY agent_id, username, host, port ORDER BY password_secret, bin_directory) AS rn
    FROM distinct_combos
)
INSERT INTO pg_connection (id, agent_id, name, host, port, username, password_secret, bin_directory, created_at, updated_at)
SELECT id, agent_id,
       base_name || CASE WHEN rn = 1 THEN '' ELSE '-' || rn END,
       host, port, username, password_secret, bin_directory, now(), now()
FROM named;

-- Rewrite each postgres source spec: drop the fields now on the connection, add connectionId.
UPDATE backup_source bs
SET spec = (bs.spec - 'host' - 'port' - 'username' - 'passwordSecret' - 'binDirectory')
    || jsonb_build_object('connectionId', pc.id)
FROM backup_policy bp, pg_connection pc
WHERE bs.policy_id = bp.id
  AND bs.type = 'PostgreSql'
  AND pc.agent_id = bp.agent_id
  AND pc.host = COALESCE(bs.spec ->> 'host', 'localhost')
  AND pc.port = COALESCE((bs.spec ->> 'port')::int, 5432)
  AND pc.username = bs.spec ->> 'username'
  AND pc.password_secret = bs.spec ->> 'passwordSecret'
  AND pc.bin_directory IS NOT DISTINCT FROM bs.spec ->> 'binDirectory';
