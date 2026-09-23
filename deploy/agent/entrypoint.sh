#!/bin/sh
# Bootstrap for the Linux test agent: on first start it registers itself through the admin API (storage
# target, agent, enrollment token), enrolls, stores the PostgreSQL password, creates a demo policy and
# queues a first backup. Then it runs the agent in server mode.
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
        printf %s "$PG_PASSWORD" | dupli-agent secret set pg-sample
        sources=$(echo "$sources" | jq --arg h "$PG_HOST" --argjson p "${PG_PORT:-5432}" --arg u "${PG_USER:-postgres}" --arg b "$PG_BIN" \
            '. + [{type: "postgres", sourceId: "pg", host: $h, port: $p, username: $u, passwordSecret: "pg-sample",
                   excludeDatabases: ["dupli"], binDirectory: $b}]')
    fi

    if [ "$(api GET "/agents/$agent/policies" | jq length)" = "0" ]; then
        policy=$(api POST "/agents/$agent/policies" -d "$(jq -n --argjson s "$sources" \
            '{name: "demo", cron: "*/15 * * * *", timeZone: "Europe/Rome",
              retention: {keepDaily: 7, keepWeekly: 4, keepMonthly: 3}, sources: $s}')" | jq -r .id)
        api POST "/policies/$policy/run" > /dev/null
        echo "Demo policy $policy created, first backup queued"
    fi
}

# Plays the role of the Windows service recovery actions: exit code 75 ("restart agent" job) restarts the
# agent, any other exit ends the container. SIGTERM (container stop) is forwarded for a clean shutdown.
run_agent() {
    while :; do
        dupli-agent run &
        pid=$!
        trap 'kill -TERM "$pid" 2>/dev/null; wait "$pid"; exit 0' TERM INT
        code=0
        wait "$pid" || code=$?
        [ "$code" -eq 75 ] || exit "$code"
        echo "Agent requested a restart (exit 75): starting it again"
    done
}

[ -d "$SAMPLE_DIR" ] || seed_sample_data
[ -f "$DUPLI_HOME/config/agent.json" ] || bootstrap

run_agent
