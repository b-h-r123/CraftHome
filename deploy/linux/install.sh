#!/usr/bin/env bash
set -euo pipefail
if [[ $EUID -ne 0 ]]; then
  echo '请使用 sudo bash deploy/linux/install.sh <Linux 发行包路径>' >&2
  exit 1
fi
archive=$(realpath "${1:?请提供 crafthome-v0.1-linux-x64.tar.gz 路径}")
if [[ $(uname -m) != x86_64 ]]; then
  echo '此发行包仅支持 Linux x86_64。' >&2
  exit 1
fi
if [[ -e /opt/crafthome/CraftHome.Web ]]; then
  echo '/opt/crafthome 已有安装。此脚本仅用于首次安装，不会覆盖。' >&2
  exit 1
fi
if ! id crafthome >/dev/null 2>&1; then
  useradd --system --home /var/lib/crafthome --shell /usr/sbin/nologin crafthome
fi
install -d -m 0755 /opt/crafthome
install -d -m 0700 -o crafthome -g crafthome /var/lib/crafthome
tar -xzf "$archive" -C /opt/crafthome
chmod +x /opt/crafthome/CraftHome.Web
chown -R root:root /opt/crafthome
cat > /etc/systemd/system/crafthome.service <<'EOF'
[Unit]
Description=CraftHome Minecraft console
After=network.target
[Service]
Type=simple
User=crafthome
Group=crafthome
WorkingDirectory=/opt/crafthome
ExecStart=/opt/crafthome/CraftHome.Web --urls http://127.0.0.1:5080
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=CraftHome__DataDirectory=/var/lib/crafthome
Restart=on-failure
RestartSec=5
UMask=0077
NoNewPrivileges=true
ProtectSystem=full
ProtectHome=true
TimeoutStopSec=30
[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload
systemctl enable --now crafthome.service
echo '已安装。使用 SSH 隧道访问 http://127.0.0.1:5080。'
echo '首次登录信息：sudo cat /var/lib/crafthome/first-login.txt'
echo '此脚本安装控制台；MCSManager 和 Minecraft 实例需按 README 配置。'
