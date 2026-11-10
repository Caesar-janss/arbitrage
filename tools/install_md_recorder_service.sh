#!/usr/bin/env bash
# Install the market-data recorder as a systemd service: starts now, restarts after a crash, survives a reboot.
#
#   bash tools/install_md_recorder_service.sh            # data in $HOME/md
#   bash tools/install_md_recorder_service.sh /data/md   # or elsewhere
#
# Live log: journalctl -u md-recorder -f        Stop: sudo systemctl stop md-recorder
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${1:-$HOME/md}"
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
sudo tee /etc/systemd/system/md-recorder.service >/dev/null <<EOF
[Unit]
Description=ArbEngine market-data recorder (Binance + Bitget public streams)
Wants=network-online.target time-sync.target
After=network-online.target time-sync.target

[Service]
User=$USER_NAME
WorkingDirectory=$REPO
ExecStart=$PY $REPO/tools/md_recorder.py --out $OUT
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
sudo systemctl enable --now md-recorder
sleep 5
systemctl --no-pager status md-recorder | head -12 || true
echo
echo "Recorder running. Data: $OUT"
echo "  live log:  journalctl -u md-recorder -f        (Ctrl+C only leaves the log; recording continues)"
echo "  check:     python3 $REPO/tools/md_recorder.py --verify $OUT"
echo "  stop:      sudo systemctl stop md-recorder"
