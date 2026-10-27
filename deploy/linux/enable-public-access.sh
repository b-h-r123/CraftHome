#!/usr/bin/env bash
set -euo pipefail
if [[ $EUID -ne 0 ]]; then
  echo '请使用 sudo bash enable-public-access.sh' >&2
  exit 1
fi
test -x /opt/crafthome/CraftHome.Web
systemctl cat crafthome.service >/dev/null
install -d -m 0755 /etc/systemd/system/crafthome.service.d
cat > /etc/systemd/system/crafthome.service.d/public-access.conf <<'EOF'
[Service]
ExecStart=
ExecStart=/opt/crafthome/CraftHome.Web --urls http://0.0.0.0:5080
EOF
systemctl daemon-reload
systemctl restart crafthome.service
if command -v ufw >/dev/null 2>&1; then
  ufw allow 5080/tcp comment 'CraftHome WebUI'
fi
for attempt in {1..30}; do
  if curl --fail --silent http://127.0.0.1:5080/healthz; then
    echo
    echo '控制台现监听所有 IPv4 接口的 5080 端口。'
    echo '还需确认云服务器安全组允许 TCP 5080；外部访问使用 http://服务器公网IP:5080。'
    echo '此入口使用 HTTP；面板密码与 API Key 必须保持保密。'
    exit 0
  fi
  sleep 1
done
echo '健康检查未通过，请检查 journalctl -u crafthome.service。' >&2
exit 1
