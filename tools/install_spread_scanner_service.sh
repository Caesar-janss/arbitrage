#!/usr/bin/env bash
# Install the spread scanner as a systemd service: starts now, restarts after a crash, survives a reboot.
#
#   bash tools/install_spread_scanner_service.sh            # data in $HOME/scan
#   bash tools/install_spread_scanner_service.sh /data/scan   # or elsewhere
#
# Live log: journalctl -u spread-scanner -f        Stop: sudo systemctl stop spread-scanner
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${1:-$HOME/scan}"
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

# 2 = bad arguments, 3 = disk full: restarting would not help, so systemd must not retry those.
sudo tee /etc/systemd/system/spread-scanner.service >/dev/null <<EOF
[Unit]
Description=ArbEngine spread scanner (8 venues, public REST tickers)
Wants=network-online.target time-sync.target
After=network-online.target time-sync.target

[Service]
User=$USER_NAME
WorkingDirectory=$REPO
ExecStart=$PY $REPO/tools/spread_scanner.py --out $OUT
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
sudo systemctl enable --now spread-scanner
sleep 5
systemctl --no-pager status spread-scanner | head -12 || true
echo
echo "Scanner running. Data: $OUT"
echo "  live log:  journalctl -u spread-scanner -f        (Ctrl+C only leaves the log; recording continues)"
echo "  report:    python3 $REPO/tools/spread_scanner.py --report $OUT --last-hours 24"
echo "  stop:      sudo systemctl stop spread-scanner"
