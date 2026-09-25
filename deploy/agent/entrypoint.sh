#!/bin/sh
# Bootstrap for the Linux test agent: on first start it registers itself through the admin API (storage
# target, agent, enrollment token), enrolls, stores the PostgreSQL password, creates a demo policy and
# queues a first backup. Then it runs the Launcher, which supervises the agent in server mode and applies
# updates/rollbacks exactly as the Windows service does.
set -eu

: "${DUPLI_SERVER_URL:?}" "${DUPLI_ADMIN_KEY:?}" "${S3_ENDPOINT:?}" "${S3_ACCESS_KEY:?}" "${S3_SECRET_KEY:?}"
AGENT_NAME="${AGENT_NAME:-linux-agent}"
S3_BUCKET="${S3_BUCKET:-backups}"
S3_REGION="${S3_REGION:-us-east-1}"
PG_BIN="${PG_BIN:-/usr/lib/postgresql/18/bin}"
SAMPLE_DIR="${SAMPLE_DIR:-/data/sample}"

api() {
    method=$1 path=$2
    shift 2
    curl -fsS -X "$method" -H "X-Dupli-Admin-Key: $DUPLI_ADMIN_KEY" -H "Content-Type: application/json" \
        "$DUPLI_SERVER_URL/api/admin$path" "$@"
}

seed_sample_data() {
    mkdir -p "$SAMPLE_DIR/docs" "$SAMPLE_DIR/bin"
    for i in $(seq 1 30); do
        echo "Sample document $i, created $(date -u +%FT%TZ)" > "$SAMPLE_DIR/docs/doc-$i.txt"
    done
    head -c 1048576 /dev/urandom > "$SAMPLE_DIR/bin/random-1m.bin"

    if [ -n "${PG_HOST:-}" ]; then
        PGPASSWORD="$PG_PASSWORD" "$PG_BIN/psql" -h "$PG_HOST" -p "${PG_PORT:-5432}" -U "${PG_USER:-postgres}" \
            -d "${PG_SAMPLE_DB:-sampledb}" -v ON_ERROR_STOP=1 -q -c "
            CREATE TABLE IF NOT EXISTS invoices (id serial PRIMARY KEY, customer text NOT NULL, total numeric(12,2) NOT NULL);
            INSERT INTO invoices (customer, total) SELECT 'customer ' || g, g * 10.5 FROM generate_series(1, 100) g;"
    fi
}

bootstrap() {
    echo "Waiting for $DUPLI_SERVER_URL ..."
    until curl -fsS "$DUPLI_SERVER_URL/health" > /dev/null 2>&1; do sleep 2; done

    # restic retries for minutes on a missing bucket: create it first (S3 PUT bucket, SigV4). Already existing is fine.
    curl -sS -o /dev/null --aws-sigv4 "aws:amz:$S3_REGION:s3" --user "$S3_ACCESS_KEY:$S3_SECRET_KEY" \
        -X PUT "$S3_ENDPOINT/$S3_BUCKET" || true

    # The dev server may run on a macOS host: it reads repositories (snapshot browse) with its own restic build.
    # Conflict = already registered.
    for platform in darwin_arm64:7be0a144ccc377880f294204aa271d76e4b79554b42a751151d425ce6ebac143 \
                    darwin_amd64:c38d579622cf602f665234c5a8c315030b6cf70656028fe6dc29a786b60e5f35; do
        api POST /releases/restic -d "$(jq -n --arg p "${platform%%:*}" --arg s "${platform#*:}" \
            '{version: "0.19.1", platform: $p, sourceUrl: ("https://github.com/restic/restic/releases/download/v0.19.1/restic_0.19.1_" + $p + ".bz2"), sha256: $s}')" \
            > /dev/null 2>&1 || true
    done

    storage=$(api GET /storage-targets | jq -r '.[] | select(.name == "local-s3") | .id')
    if [ -z "$storage" ]; then
        storage=$(api POST /storage-targets -d "$(jq -n --arg e "$S3_ENDPOINT" --arg b "$S3_BUCKET" --arg r "$S3_REGION" \
            '{name: "local-s3", endpoint: $e, bucket: $b, region: $r}')" | jq -r .id)
    fi

    agent=$(api GET /agents | jq -r --arg n "$AGENT_NAME" '.[] | select(.name == $n) | .id')
    if [ -z "$agent" ]; then
        agent=$(api POST /agents -d "$(jq -n --arg n "$AGENT_NAME" --arg s "$storage" --arg ak "$S3_ACCESS_KEY" --arg sk "$S3_SECRET_KEY" \
            '{name: $n, storageTargetId: $s, storagePrefix: ("agents/" + $n), s3AccessKeyId: $ak, s3SecretAccessKey: $sk}')" | jq -r .id)
    fi

    token=$(api POST "/agents/$agent/enrollment-tokens" | jq -r .token)
    dupli-agent install --server "$DUPLI_SERVER_URL" --token "$token"

    sources='[{"type": "directory", "sourceId": "sample", "paths": ["'"$SAMPLE_DIR"'"]}]'
    if [ -n "${PG_HOST:-}" ]; then
        # The password is escrowed server-side (agent_secret) and handed to the agent per job: it is never set
        # on the VM (no "dupli-agent secret set" any more).
        connection=$(api POST "/agents/$agent/connections" -d "$(jq -n --arg h "$PG_HOST" --argjson p "${PG_PORT:-5432}" \
            --arg u "${PG_USER:-postgres}" --arg b "$PG_BIN" --arg pw "$PG_PASSWORD" \
            '{name: "pg-sample", host: $h, port: $p, username: $u, passwordSecret: "pg-sample", binDirectory: $b, password: $pw}')" | jq -r .id)
        sources=$(echo "$sources" | jq --arg c "$connection" \
            '. + [{type: "postgres", sourceId: "pg", connectionId: $c, excludeDatabases: ["dupli"]}]')
    fi

    if [ "$(api GET "/agents/$agent/policies" | jq length)" = "0" ]; then
        policy=$(api POST "/agents/$agent/policies" -d "$(jq -n --argjson s "$sources" \
            '{name: "demo", cron: "*/15 * * * *", timeZone: "Europe/Rome",
              retention: {keepDaily: 7, keepWeekly: 4, keepMonthly: 3}, sources: $s}')" | jq -r .id)
        api POST "/policies/$policy/run" > /dev/null
        echo "Demo policy $policy created, first backup queued"
    fi
}

[ -d "$SAMPLE_DIR" ] || seed_sample_data
[ -f "$DUPLI_HOME/config/agent.json" ] || bootstrap
# Repair mode (no token): stages this image's binary and the Launcher when missing, e.g. for a home enrolled
# by an older image. An update applied at run time lives in versions/ and survives container restarts.
[ -x "$DUPLI_HOME/launcher/dupli-agent" ] || dupli-agent install

# The Launcher restarts the agent on exit 75 (restart job) and switches versions on exit 76 (update).
# SIGTERM (container stop) stops the Launcher, which closes the agent's stdin for a clean shutdown.
exec "$DUPLI_HOME/launcher/dupli-agent" launch
