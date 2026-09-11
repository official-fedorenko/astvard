# Sourced by install-site.sh and update-site.sh: where our database lives and how a
# root shell reaches it, decided in one place. Two copies of this logic drifted apart
# once already — the update learned a second way to the database, the install never did.
#
# Two layouts, told apart by POSTGRES_CONTAINER in .env:
#
#   set    — a Postgres that already runs in that container and serves other projects
#            too. On the VPS it is postgres-shared, bound to 127.0.0.1:5432; the
#            decision from 2026-09-01 is one role and one database per project inside
#            it. Starting our own compose Postgres there would fight it for the port.
#   empty  — this repo's docker-compose.yml, as on a development machine.
#
# Expects POSTGRES_* exported from .env and DIR pointing at the checkout.

db_container_running() {
  [ "$(docker inspect -f '{{.State.Running}}' "$1" 2>/dev/null)" = true ]
}

db_compose_running() {
  docker compose -f "$DIR/docker-compose.yml" ps --status running 2>/dev/null | grep -q postgres
}

# Prints how the schema can be applied right now, or nothing when there is no way.
db_mode() {
  if [ -n "${POSTGRES_CONTAINER:-}" ]; then
    db_container_running "$POSTGRES_CONTAINER" && echo container
  elif command -v docker >/dev/null 2>&1 && db_compose_running; then
    echo compose
  elif command -v psql >/dev/null 2>&1; then
    echo psql
  fi
}

# Our role and database inside the shared instance. Safe to re-run: the password is
# set from .env every time, so .env stays the only place it is written down.
db_ensure_in_container() {
  docker exec -i "$POSTGRES_CONTAINER" psql -v ON_ERROR_STOP=1 -q \
    -U "${POSTGRES_ADMIN_USER:-postgres}" \
    -v user="$POSTGRES_USER" -v pw="$POSTGRES_PASSWORD" -v db="$POSTGRES_DB" <<'SQL'
SELECT format('CREATE ROLE %I LOGIN', :'user')
 WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'user') \gexec
ALTER ROLE :"user" WITH LOGIN PASSWORD :'pw';
SELECT format('CREATE DATABASE %I OWNER %I', :'db', :'user')
 WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = :'db') \gexec
-- Every role on the instance may connect to every database by default. Our tables
-- would still be closed to the other projects' roles, but the door need not be open.
REVOKE ALL ON DATABASE :"db" FROM PUBLIC;
SQL
}

db_apply_schema() {
  case "$(db_mode)" in
    container)
      # As our own role, so the tables belong to it and not to the superuser.
      docker exec -i -e PGPASSWORD="$POSTGRES_PASSWORD" \
        -e PGOPTIONS='-c client_min_messages=warning' "$POSTGRES_CONTAINER" \
        psql -v ON_ERROR_STOP=1 -q -U "$POSTGRES_USER" -d "$POSTGRES_DB" \
        < "$DIR/db/schema.sql" >/dev/null
      ;;
    compose)
      docker compose -f "$DIR/docker-compose.yml" exec -T \
        -e PGOPTIONS='-c client_min_messages=warning' postgres \
        psql -v ON_ERROR_STOP=1 -q -U "$POSTGRES_USER" -d "$POSTGRES_DB" \
        < "$DIR/db/schema.sql" >/dev/null
      ;;
    psql)
      PGPASSWORD="$POSTGRES_PASSWORD" PGOPTIONS='-c client_min_messages=warning' \
        psql -v ON_ERROR_STOP=1 -q \
        -h "$POSTGRES_HOST" -p "$POSTGRES_PORT" -U "$POSTGRES_USER" -d "$POSTGRES_DB" \
        -f "$DIR/db/schema.sql" >/dev/null
      ;;
    *)
      return 1
      ;;
  esac
}
