-- M4: agent update infrastructure (channels, pins, launcher-reported update status, agent releases).

ALTER TABLE agent
    ADD COLUMN platform               text NOT NULL DEFAULT 'windows_amd64',
    ADD COLUMN channel                text NOT NULL DEFAULT 'stable',
    ADD COLUMN pinned_agent_version   text NULL,
    ADD COLUMN pinned_restic_version  text NULL,
    ADD COLUMN launcher_managed       boolean NOT NULL DEFAULT false,
    ADD COLUMN last_update_version    text NULL,
    ADD COLUMN last_update_outcome    text NULL,
    ADD COLUMN last_update_error      text NULL,
    ADD COLUMN last_update_at         timestamptz NULL,
    ADD COLUMN restic_update_error    text NULL,
    ADD CONSTRAINT agent_channel_check CHECK (channel IN ('dev', 'beta', 'stable'));

-- Channel is required for the agent product (dev/beta/stable releases), null for restic (no channels).
ALTER TABLE software_release
    ADD COLUMN channel text NULL,
    ADD CONSTRAINT software_release_channel_check
        CHECK ((product = 'agent' AND channel IS NOT NULL) OR (product <> 'agent' AND channel IS NULL));

-- The "one current" rule now scopes by channel too: an agent release can be current in one channel at a
-- time per platform, restic (channel NULL) is unaffected by the coalesce.
DROP INDEX software_release_one_current;
CREATE UNIQUE INDEX software_release_one_current
    ON software_release (product, platform, coalesce(channel, '')) WHERE is_current;
