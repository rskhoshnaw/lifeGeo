#!/usr/bin/env bash
# Tests of deploy/install-cron.sh (plan step 5.77): what it writes, that it can be run again, and that it can take the automatic backups away. No root and no real cron needed.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEPLOY="$(dirname "$HERE")"
[ ! -f "$DEPLOY/.env" ] || { echo "deploy/.env exists: this test must not run on a real server" >&2; exit 2; }
WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT
export CRON_FILE="$WORK/cron.d/lifedrop-backup" LOGROTATE_FILE="$WORK/logrotate.d/lifedrop-backup"
pass=0
ok() { pass=$((pass + 1)); echo "ok   $*"; }
die() { echo "FAIL $*" >&2; exit 1; }
unset AGE_RECIPIENT OFFSITE_RCLONE_REMOTE

# Only the local backup and the weekly check when the off-site copy is not set up; the note says what to do
"$DEPLOY/install-cron.sh" > "$WORK/out" 2>&1 || { cat "$WORK/out" >&2; die "install failed"; }
grep -q "backup.sh >> /var/log/lifedrop-backup.log" "$CRON_FILE" || die "no nightly local backup"
! grep -q "^15 4" "$CRON_FILE" || die "an off-site line without the off-site settings"
grep -q "^30 8 \* \* 1 root .*backup-status.sh" "$CRON_FILE" || die "no weekly status check"
grep -q "NOTE: the off-site copy is not set up" "$WORK/out" || die "no note about the missing off-site copy"
ok "without the off-site settings: nightly local backup + weekly check, and a note"

# Every line of a cron.d file has the user as its sixth field; the file is not writable by others
bad="$(grep -vE '^\s*(#|$|[A-Z]+=)' "$CRON_FILE" | awk '$6 != "root" || NF < 7' | wc -l)"
[ "$bad" = 0 ] || die "a line of the cron file is not 'minute hour day month weekday root command'"
[ "$(stat -c %a "$CRON_FILE")" = 644 ] || die "the cron file is not 644"
grep -q "lifedrop-backup-status.log" "$LOGROTATE_FILE" || die "no logrotate file"
ok "the lines are valid cron.d lines and the files have the right mode"

# With the off-site settings: the 04:15 line is there, and running again gives the very same file
export AGE_RECIPIENT="age1aaaaaaaaaa age1bbbbbbbbbb" OFFSITE_RCLONE_REMOTE="gdrive:lifedrop-backups"
"$DEPLOY/install-cron.sh" > "$WORK/out" 2>&1 || { cat "$WORK/out" >&2; die "second install failed"; }
grep -q "^15 4 \* \* \* root .*backup-offsite.sh >> /var/log/lifedrop-offsite.log" "$CRON_FILE" || die "no nightly off-site line"
cp "$CRON_FILE" "$WORK/first"
"$DEPLOY/install-cron.sh" > /dev/null 2>&1
cmp -s "$WORK/first" "$CRON_FILE" || die "running again changed the file"
[ "$(grep -c 'backup.sh >> ' "$CRON_FILE")" = 1 ] || die "the nightly line is there more than once"
ok "with the off-site settings the off-site line is added, and running again changes nothing"

# --print writes nothing; --remove takes the automatic backups away; a wrong option is refused
rm -f "$CRON_FILE"; printed="$("$DEPLOY/install-cron.sh" --print)"
grep -q "^10 3 " <<<"$printed" || die "--print shows nothing"
[ ! -e "$CRON_FILE" ] || die "--print wrote the file"
"$DEPLOY/install-cron.sh" > /dev/null 2>&1; "$DEPLOY/install-cron.sh" --remove > /dev/null
[ ! -e "$CRON_FILE" ] && [ ! -e "$LOGROTATE_FILE" ] || die "--remove left files"
if "$DEPLOY/install-cron.sh" --nonsense > /dev/null 2>&1; then die "a wrong option was accepted"; fi
ok "--print writes nothing, --remove removes, a wrong option is refused"
echo "All $pass checks passed."
