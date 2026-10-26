#!/usr/bin/env bash
# Usage: PGHOST=localhost PGPORT=5432 PGUSER=postgres ./db/tests/run_tests.sh
# Membuat DB baru (arb_test), menjalankan semua migrasi berurutan, lalu test suite.
set -euo pipefail

DB="${ARB_TEST_DB:-arb_test}"
HERE="$(cd "$(dirname "$0")" && pwd)"
MIG="$HERE/../migrations"

psql -v ON_ERROR_STOP=1 -q -d postgres -c "DROP DATABASE IF EXISTS $DB" -c "CREATE DATABASE $DB"

for f in "$MIG"/V*.sql; do
  echo "migrate: $(basename "$f")"
  psql -v ON_ERROR_STOP=1 -q -d "$DB" -f "$f"
done

echo "test: test_schema.sql"
psql -v ON_ERROR_STOP=1 -q -d "$DB" -f "$HERE/test_schema.sql"
