-- First start of the local database container (docker-compose.yml). Same databases and roles as CI, plus the
-- development database. Migrations and data are applied by scripts/local/Reset-LocalDb.ps1.
CREATE DATABASE lifedrop_test_ci;
CREATE USER test_user WITH PASSWORD 'test_pass' NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
GRANT ALL PRIVILEGES ON DATABASE lifedrop_test_ci TO test_user;
\connect lifedrop_test_ci
CREATE EXTENSION IF NOT EXISTS postgis;
GRANT ALL ON SCHEMA public TO test_user;

-- The API's runtime role: no superuser, no BYPASSRLS, so tenant row-level security applies.
CREATE ROLE lifedrop_app WITH LOGIN PASSWORD 'lifedrop_app_local' NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
