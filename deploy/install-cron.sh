#!/usr/bin/env bash
# Makes the backups automatic (plan step 5.77): installs ONE cron file that runs, on this server,
#   every night   03:10  backup.sh          (a dump in BACKUP_DIR, old ones removed)
#   every night   04:15  backup-offsite.sh  (the encrypted copy on Google Drive; only when AGE_RECIPIENT and OFFSITE_RCLONE_REMOTE are set in deploy/.env)
#   every Monday  08:30  backup-status.sh   (is the last good copy recent? a problem is written to the log and to the system log)
# Times are the server's clock (UTC unless the server was set otherwise). Run it once as root, and again after the off-site copy is set up:
#   sudo /opt/lifedrop/deploy/install-cron.sh            install or refresh
#   sudo /opt/lifedrop/deploy/install-cron.sh --print    show what would be installed
#   sudo /opt/lifedrop/deploy/install-cron.sh --remove   take the automatic backups away
# Safe to run again: it rewrites the same file. Cron mails nothing; the logs are /var/log/lifedrop-*.log (rotated by a logrotate file this script also installs).
. "$(dirname "$0")/lib.sh"
CRON_FILE="${CRON_FILE:-/etc/cron.d/lifedrop-backup}"
LOGROTATE_FILE="${LOGROTATE_FILE:-/etc/logrotate.d/lifedrop-backup}"
mode="${1:-install}"

offsite=0
[ -n "${AGE_RECIPIENT:-}" ] && [ -n "${OFFSITE_RCLONE_REMOTE:-}" ] && offsite=1

render_cron() {
  cat <<CRON
# Managed by $DEPLOY_DIR/install-cron.sh - do not edit by hand; run the script again instead.
SHELL=/bin/bash
PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
10 3 * * * root $DEPLOY_DIR/backup.sh >> /var/log/lifedrop-backup.log 2>&1
CRON
  if [ "$offsite" = 1 ]; then
    echo "15 4 * * * root $DEPLOY_DIR/backup-offsite.sh >> /var/log/lifedrop-offsite.log 2>&1"
  else
    echo "# (no off-site copy: AGE_RECIPIENT and OFFSITE_RCLONE_REMOTE are not both set in $DEPLOY_DIR/.env; see docs/BACKUP-OFFSITE.md, then run install-cron.sh again)"
  fi
  echo "30 8 * * 1 root $DEPLOY_DIR/backup-status.sh >> /var/log/lifedrop-backup-status.log 2>&1 || logger -t lifedrop-backup \"PROBLEM with the backups: see /var/log/lifedrop-backup-status.log\""
}

render_logrotate() {
  cat <<'ROTATE'
/var/log/lifedrop-backup.log /var/log/lifedrop-offsite.log /var/log/lifedrop-backup-status.log {
    monthly
    rotate 6
    compress
    missingok
    notifempty
}
ROTATE
}

case "$mode" in
  --print) render_cron; echo; render_logrotate; exit 0 ;;
  --remove) rm -f "$CRON_FILE" "$LOGROTATE_FILE"; echo "Removed $CRON_FILE: the backups are no longer automatic."; exit 0 ;;
  install) ;;
  *) echo "Usage: $0 [--print|--remove]" >&2; exit 2 ;;
esac

if [ "$CRON_FILE" = /etc/cron.d/lifedrop-backup ] && [ "$(id -u)" != 0 ]; then echo "Run this as root (sudo $0)." >&2; exit 1; fi
mkdir -p "$(dirname "$CRON_FILE")" "$(dirname "$LOGROTATE_FILE")"
render_cron > "$CRON_FILE.new" && mv "$CRON_FILE.new" "$CRON_FILE"
chmod 644 "$CRON_FILE"
render_logrotate > "$LOGROTATE_FILE" && chmod 644 "$LOGROTATE_FILE"
chmod +x "$DEPLOY_DIR/backup.sh" "$DEPLOY_DIR/backup-offsite.sh" "$DEPLOY_DIR/backup-status.sh" 2>/dev/null || true
echo "Installed $CRON_FILE:"
sed 's/^/  /' "$CRON_FILE"
if [ "$offsite" = 1 ]; then
  for tool in age rclone; do command -v "$tool" >/dev/null 2>&1 || echo "WARNING: $tool is not installed (apt install -y age rclone); the off-site copy will fail until it is." >&2; done
else
  echo "NOTE: the off-site copy is not set up yet, so only the nightly copy ON THIS SERVER is automatic (it is lost with the server). See docs/BACKUP-OFFSITE.md."
fi
