#!/usr/bin/env bash
# When was the last good backup, and did the last try fail? (plan step 5.68). Prints one line per kind and ends with exit code 1 when something needs a look:
# no good copy yet, a copy older than BACKUP_MAX_AGE_HOURS (default 48), or a failure newer than the last good copy. The off-site line is checked only when it is set up
# (OFFSITE_RCLONE_REMOTE in deploy/.env, or it has run once). Use it by hand, or from cron with mail:  30 8 * * * /opt/lifedrop/deploy/backup-status.sh || echo "backup problem" | mail ...
. "$(dirname "$0")/lib.sh"
MAX_HOURS="${BACKUP_MAX_AGE_HOURS:-48}"
now="$(date +%s)"
problems=0
field() { cut -d' ' -f"$2" "$STATUS_DIR/$1" 2>/dev/null || true; }

check() {   # $1 = label, $2 = file prefix (last-local / last-offsite)
  local label="$1" ok="$STATUS_DIR/$2-ok" failed="$STATUS_DIR/$2-failed" age_h
  if [ -f "$ok" ]; then
    age_h=$(( (now - $(field "$2-ok" 1)) / 3600 ))
    if [ "$age_h" -gt "$MAX_HOURS" ]; then echo "PROBLEM  $label: last good copy is $age_h hours old (limit $MAX_HOURS): $(field "$2-ok" 3-)"; problems=1
    else echo "ok       $label: last good copy $age_h hours ago ($(field "$2-ok" 3-))"; fi
  else
    echo "PROBLEM  $label: no good copy has been made yet"; problems=1
  fi
  if [ -f "$failed" ] && { [ ! -f "$ok" ] || [ "$(field "$2-failed" 1)" -ge "$(field "$2-ok" 1)" ]; }; then
    echo "PROBLEM  $label: the last try failed at $(field "$2-failed" 2): $(field "$2-failed" 3-)"; problems=1
  fi
}

check "local dump " last-local
if [ -n "${OFFSITE_RCLONE_REMOTE:-}" ] || [ -f "$STATUS_DIR/last-offsite-ok" ] || [ -f "$STATUS_DIR/last-offsite-failed" ]; then check "off-site   " last-offsite
else echo "note     off-site copy is not set up (docs/BACKUP-OFFSITE.md): a lost server loses the backups on it too"; fi
exit "$problems"
