#!/usr/bin/env bash
# Install the market-data recorder as a systemd service: starts now, restarts after a crash, survives a reboot.
#
#   bash tools/install_md_recorder_service.sh                       # spot, data in $HOME/md, unit md-recorder
#   bash tools/install_md_recorder_service.sh /data/md              # spot, elsewhere
#   bash tools/install_md_recorder_service.sh $HOME/md_perp perp    # perpetual futures, unit md-recorder-perp
#
# Live log: journalctl -u <unit> -f        Stop: sudo systemctl stop <unit>
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${1:-$HOME/md}"
MARKET="${2:-spot}"
case "$MARKET" in
    spot) UNIT=md-recorder ;;
    perp) UNIT=md-recorder-perp ;;
    *) echo "market must be spot or perp, got: $MARKET" >&2; exit 1 ;;
esac
USER_NAME="$(id -un)"
PY="$(command -v python3 || true)"

if [ -z "$PY" ] || ! "$PY" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 8) else 1)'; then
    echo "python3 >= 3.8 is needed" >&2
    exit 1
fi
case "$REPO$OUT" in
    *" "*) echo "paths with spaces are not supported: $REPO / $OUT" >&2; exit 1 ;;
esac
mkdir -p "$OUT"

# 2 = no symbol trades on both venues, 3 = disk full: restarting would not help, so systemd must not retry those.
sudo tee "/etc/systemd/system/$UNIT.service" >/dev/null <<EOF
[Unit]
Description=ArbEngine market-data recorder, $MARKET (Binance + Bitget public streams)
Wants=network-online.target time-sync.target
After=network-online.target time-sync.target

[Service]
User=$USER_NAME
WorkingDirectory=$REPO
ExecStart=$PY $REPO/tools/md_recorder.py --market $MARKET --out $OUT
Environment=PYTHONUNBUFFERED=1
Restart=on-failure
RestartSec=10
RestartPreventExitStatus=2 3
KillSignal=SIGTERM
TimeoutStopSec=30

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl daemon-reload
sudo systemctl enable --now "$UNIT"
sleep 5
systemctl --no-pager status "$UNIT" | head -12 || true
echo
echo "Recorder ($MARKET) running. Data: $OUT"
echo "  live log:  journalctl -u $UNIT -f        (Ctrl+C only leaves the log; recording continues)"
echo "  check:     python3 $REPO/tools/md_recorder.py --verify $OUT --last-hours 1"
echo "  stop:      sudo systemctl stop $UNIT"
