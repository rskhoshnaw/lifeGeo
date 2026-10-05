# Shared by the local scripts: port of the local database (docker-compose.yml). Default 55433; set
# LIFEDROP_DB_PORT to use another free port (and use the same value for docker compose up).
$LocalDbPort = if ($env:LIFEDROP_DB_PORT) { $env:LIFEDROP_DB_PORT } else { '55433' }
