-- M3: per-item job results (restore tests, maintenance) and restore-test scheduling watermark.

ALTER TABLE job ADD COLUMN result_items jsonb NULL;

ALTER TABLE agent ADD COLUMN last_restore_test_scheduled_for timestamptz NULL;
