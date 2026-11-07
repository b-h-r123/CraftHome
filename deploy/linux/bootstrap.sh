#!/usr/bin/env bash
set -euo pipefail
umask 077
[[ $EUID -eq 0 ]] || { echo '请使用 sudo bash bootstrap.sh [发行包] [--upgrade|--check]' >&2; exit 1; }
upgrade=false
check=false
public=false
archive=''
for argument in "$@"; do
  case "$argument" in
    --upgrade) upgrade=true ;;
    --check) check=true ;;
    --public) public=true ;;
    --*) echo "未知选项：$argument" >&2; exit 1 ;;
    *) archive=$(realpath "$argument") ;;
  esac
done
. /etc/os-release
[[ ${ID:-} == ubuntu && ${VERSION_ID:-} == 24.04 && $(uname -m) == x86_64 ]] || {
  echo '本轮一键安装支持 Ubuntu 24.04 x86_64。' >&2; exit 1;
}
echo "系统：$PRETTY_NAME / $(uname -m)"
free -m
df -h /
java_binary=/usr/bin/java
if [[ -x /usr/lib/jvm/java-21-openjdk-amd64/bin/java ]]; then
  java_binary=/usr/lib/jvm/java-21-openjdk-amd64/bin/java
fi
if [[ $check == true ]]; then
  [[ -x $java_binary ]] && "$java_binary" -version 2>&1 | head -n 3 || true
  systemctl is-active crafthome mcsm-web mcsm-daemon || true
  echo '检测完成；未修改服务或文件。'
  exit 0
fi
if [[ -e /opt/crafthome/CraftHome.Web && $upgrade != true ]]; then
  echo '控制台已安装。请添加 --upgrade 升级并保留数据，或使用 --check 仅检查。' >&2
  exit 1
fi
if [[ -e /opt/mcsmanager && ! -f /opt/mcsmanager/web/app.js ]]; then
  echo '/opt/mcsmanager 已存在但不是支持的布局，请先检查，安装器没有覆盖它。' >&2; exit 1
fi
stage=$(mktemp -d /tmp/crafthome-install.XXXXXXXX)
cleanup() { case "$stage" in /tmp/crafthome-install.*) rm -rf -- "$stage" ;; esac; }
trap cleanup EXIT
packages=()
for tool in curl tar python3; do command -v "$tool" >/dev/null || packages+=("$tool"); done
command -v xz >/dev/null || packages+=(xz-utils)
[[ -s /etc/ssl/certs/ca-certificates.crt ]] || packages+=(ca-certificates)
if [[ ! -x $java_binary ]] || ! "$java_binary" -version 2>&1 | grep -Eq 'version "(2[1-9]|[3-9][0-9])'; then
  packages+=(openjdk-21-jre-headless)
fi
if [[ ${#packages[@]} -gt 0 ]]; then
  apt-get update
  DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends "${packages[@]}"
fi
if [[ -z $archive ]]; then
  release='https://github.com/b-h-r123/CraftHome/releases/latest/download'
  curl --fail --location --retry 3 "$release/crafthome-linux-x64.tar.gz" -o "$stage/crafthome-linux-x64.tar.gz"
  curl --fail --location --retry 3 "$release/SHA256SUMS" -o "$stage/SHA256SUMS"
  expected=$(awk '$2 == "crafthome-linux-x64.tar.gz" {print $1}' "$stage/SHA256SUMS")
  [[ $expected =~ ^[a-f0-9]{64}$ ]] || { echo '发行包缺少有效 SHA-256。' >&2; exit 1; }
  printf '%s  %s\n' "$expected" "$stage/crafthome-linux-x64.tar.gz" | sha256sum -c -
  archive="$stage/crafthome-linux-x64.tar.gz"
fi
python3 - "$archive" "$stage/package" <<'PY'
import pathlib,sys,tarfile
target=pathlib.Path(sys.argv[2]);target.mkdir()
with tarfile.open(sys.argv[1]) as archive:
    archive.extractall(target,filter='data')
PY
test -f "$stage/package/deploy/linux/initialize-engine.py"
test -f "$stage/package/CraftHome.Web.dll"
if [[ ! -f /opt/mcsmanager/web/app.js ]]; then
  for port in 23333 24444; do
    if ss -lntH | awk '{print $4}' | grep -Eq ":${port}$"; then
      echo "端口 $port 已占用，未安装第二套引擎。" >&2; exit 1
    fi
  done
  node_version='22.23.4'
  curl --fail --location --retry 3 "https://nodejs.org/dist/v${node_version}/node-v${node_version}-linux-x64.tar.xz" -o "$stage/node.tar.xz"
  curl --fail --location --retry 3 'https://github.com/MCSManager/MCSManager/releases/download/v10.22.0/mcsmanager_linux_release.tar.gz' -o "$stage/mcsm.tar.gz"
  printf '%s  %s\n' '1805bcd35e6815559bd2eb97db438d3fdb0eb3a5e6a977b7c1badca76337e9ce' "$stage/node.tar.xz" '8023de68b6f4d0e2a8856fed91f329a5dfeeff072da0bf721371d8e0d1ee6052' "$stage/mcsm.tar.gz" | sha256sum -c -
  id mcsm >/dev/null 2>&1 || useradd --system --home /opt/mcsmanager --shell /usr/sbin/nologin mcsm
  install -d -m 0755 /opt/mcsmanager/runtime
  tar -xf "$stage/node.tar.xz" -C /opt/mcsmanager/runtime
  tar -xzf "$stage/mcsm.tar.gz" -C /opt/mcsmanager --strip-components=1
  mkdir -p /opt/mcsmanager/web/data/SystemConfig /opt/mcsmanager/daemon/data/Config
  python3 - <<'PY'
import json,pathlib,secrets
root=pathlib.Path('/opt/mcsmanager')
(root/'web/data/SystemConfig/config.json').write_text(json.dumps({'httpIp':'127.0.0.1','httpPort':23333,'enableApiKey':'ONLY_ADMIN','language':'zh_cn','dataPlane':'proxy'}))
(root/'daemon/data/Config/global.json').write_text(json.dumps({'ip':'127.0.0.1','port':24444,'key':secrets.token_hex(24),'language':'zh_cn'}))
PY
  chmod +x /opt/mcsmanager/daemon/lib/pty_linux_x64 /opt/mcsmanager/daemon/lib/file_zip_linux_x64 /opt/mcsmanager/daemon/lib/7z_linux_x64
  chown -R mcsm:mcsm /opt/mcsmanager
  for component in web daemon; do
    cat > "/etc/systemd/system/mcsm-${component}.service" <<EOF
[Unit]
Description=MCSManager ${component} for CraftHome
After=network.target
[Service]
Type=simple
User=mcsm
Group=mcsm
WorkingDirectory=/opt/mcsmanager/${component}
ExecStart=/opt/mcsmanager/runtime/node-v${node_version}-linux-x64/bin/node --max-old-space-size=192 app.js
Restart=on-failure
RestartSec=5
TimeoutStopSec=60
UMask=0077
NoNewPrivileges=true
ProtectSystem=full
ProtectHome=true
[Install]
WantedBy=multi-user.target
EOF
  done
  systemctl daemon-reload
  systemctl enable --now mcsm-daemon mcsm-web
else
  echo '检测到已有 MCSManager，保留程序、账号与实例。'
fi
if [[ -e /opt/crafthome/CraftHome.Web ]]; then
  install -d -m 0700 /var/backups/crafthome
  tar -czf "/var/backups/crafthome/program-$(date +%Y%m%d%H%M%S).tar.gz" -C /opt/crafthome .
  systemctl stop crafthome
  tar -xzf "$archive" -C /opt/crafthome
  chmod 0755 /opt/crafthome/CraftHome.Web
  chown -R root:root /opt/crafthome
  systemctl start crafthome
else
  bash "$stage/package/deploy/linux/install.sh" "$archive"
fi
python3 "$stage/package/deploy/linux/initialize-engine.py"
systemctl restart crafthome
if [[ $public == true ]]; then bash "$stage/package/deploy/linux/enable-public-access.sh"; fi
if command -v ufw >/dev/null 2>&1; then ufw allow 25565/tcp comment 'Minecraft default game port'; fi
for attempt in {1..60}; do
  if curl --fail --silent http://127.0.0.1:5080/healthz; then
    echo
    echo '安装完成。通过现有公网入口或 SSH 隧道登录，点击“新手开服”。'
    echo '首次账号信息：sudo cat /var/lib/crafthome/first-login.txt'
    echo '新安装默认只监听本机；公网访问请按 README 配置，云安全组需另行放行。'
    exit 0
  fi
  sleep 1
done
echo '控制台健康检查未通过。请检查 journalctl -u crafthome。程序备份和数据均已保留。' >&2
exit 1
