#!/usr/bin/env bash
# Build and install Glancefolio as a systemd service on Ubuntu Server 24.04.
# Run from the root of a fresh or updated checkout: sudo ./deploy-ubuntu24.sh
set -Eeuo pipefail

APP_NAME="glancefolio"
APP_USER="${APP_USER:-glancefolio}"
APP_GROUP="${APP_GROUP:-glancefolio}"
INSTALL_ROOT="${INSTALL_ROOT:-/opt/glancefolio}"
DATA_DIR="${DATA_DIR:-/var/lib/glancefolio}"
SERVICE_NAME="${SERVICE_NAME:-glancefolio}"
PORT="${PORT:-5000}"
ROOT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
CLIENT_DIR="$ROOT_DIR/src/Client"
SERVER_PROJECT="$ROOT_DIR/src/Server/Server.csproj"
RELEASES_DIR="$INSTALL_ROOT/releases"

log() { printf '\n[%s] %s\n' "$APP_NAME" "$*"; }
die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

[[ $EUID -eq 0 ]] || die "Run as root: sudo $0"
[[ -f /etc/os-release ]] || die "Cannot identify this operating system."
# shellcheck disable=SC1091
source /etc/os-release
[[ "${ID:-}" == ubuntu && "${VERSION_ID:-}" == 24.04 ]] || die "This script supports Ubuntu 24.04 only (detected ${PRETTY_NAME:-unknown})."
[[ -f "$CLIENT_DIR/package-lock.json" && -f "$SERVER_PROJECT" ]] || die "Run this script from the Glancefolio repository checkout."
[[ "$PORT" =~ ^[0-9]+$ ]] && (( PORT >= 1 && PORT <= 65535 )) || die "PORT must be a valid TCP port."

install_build_tools() {
  local need_dotnet=false
  local need_bun=false
  if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
    need_dotnet=true
  fi
  command -v bun >/dev/null 2>&1 || need_bun=true

  if [[ "$need_dotnet" == true || "$need_bun" == true ]]; then
    log "Installing required build tools"
    apt-get update
    DEBIAN_FRONTEND=noninteractive apt-get install -y ca-certificates curl gnupg unzip python3
  fi

  if [[ "$need_dotnet" == true ]]; then
    # Microsoft packages for .NET 10 on Ubuntu 24.04.
    local ms_deb
    ms_deb="$(mktemp --suffix=.deb)"
    curl -fsSL https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb -o "$ms_deb"
    dpkg -i "$ms_deb"
    rm -f "$ms_deb"
    apt-get update
    DEBIAN_FRONTEND=noninteractive apt-get install -y dotnet-sdk-10.0
  fi

  if [[ "$need_bun" == true ]]; then
    # Download official Bun for this architecture and verify the SHA-256 digest
    # published in the corresponding GitHub release metadata.
    local bun_arch bun_name release_json bun_zip bun_extract bun_asset bun_url bun_sha
    case "$(dpkg --print-architecture)" in
      amd64) bun_arch=x64 ;;
      arm64) bun_arch=aarch64 ;;
      *) die "Bun deployment supports Ubuntu amd64 and arm64 servers only." ;;
    esac
    bun_name="bun-linux-$bun_arch.zip"
    release_json="$(mktemp)"
    bun_zip="$(mktemp --suffix=.zip)"
    bun_extract="$(mktemp -d)"
    curl -fsSL https://api.github.com/repos/oven-sh/bun/releases/latest -o "$release_json"
    bun_asset="$(python3 - "$release_json" "$bun_name" <<'PY'
import json, sys
with open(sys.argv[1], encoding="utf-8") as file:
    release = json.load(file)
asset = next((item for item in release.get("assets", []) if item.get("name") == sys.argv[2]), None)
if not asset or not asset.get("digest", "").startswith("sha256:"):
    raise SystemExit("Bun release asset or its published SHA-256 digest is missing")
print(asset["browser_download_url"], asset["digest"].split(":", 1)[1])
PY
    )" || die "Could not resolve the verified Bun release asset."
    read -r bun_url bun_sha <<< "$bun_asset"
    curl -fsSL "$bun_url" -o "$bun_zip"
    printf '%s  %s\n' "$bun_sha" "$bun_zip" | sha256sum --check --status || die "Bun archive checksum verification failed."
    unzip -q "$bun_zip" -d "$bun_extract"
    install -m 0755 "$bun_extract/bun-linux-$bun_arch/bun" /usr/local/bin/bun
    rm -rf "$release_json" "$bun_zip" "$bun_extract"
  fi
}

install_build_tools

if ! getent group "$APP_GROUP" >/dev/null; then
  groupadd --system "$APP_GROUP"
fi
if ! id "$APP_USER" >/dev/null 2>&1; then
  useradd --system --gid "$APP_GROUP" --home-dir "$DATA_DIR" --shell /usr/sbin/nologin "$APP_USER"
fi
install -d -o "$APP_USER" -g "$APP_GROUP" -m 0750 "$DATA_DIR"
install -d -o root -g root -m 0755 "$INSTALL_ROOT" "$RELEASES_DIR"

BUILD_DIR="$(mktemp -d /tmp/glancefolio-deploy.XXXXXX)"
RELEASE_ID="$(date -u +%Y%m%d%H%M%S)-$$"
RELEASE_DIR="$RELEASES_DIR/$RELEASE_ID"
cleanup() { rm -rf "$BUILD_DIR"; }
trap cleanup EXIT

log "Building the React client with Bun"
bun install --cwd "$CLIENT_DIR" --no-save
bun run --cwd "$CLIENT_DIR" build

log "Publishing the .NET server"
dotnet publish "$SERVER_PROJECT" --configuration Release --output "$BUILD_DIR/publish" --nologo

log "Installing release $RELEASE_ID"
install -d -o root -g root -m 0755 "$RELEASE_DIR"
cp -a "$BUILD_DIR/publish/." "$RELEASE_DIR/"
chmod -R a+rX "$RELEASE_DIR"

# Install a least-privilege service. User data stays outside the release so it
# survives upgrades and cleanup of old published binaries.
cat >"/etc/systemd/system/$SERVICE_NAME.service" <<UNIT
[Unit]
Description=Glancefolio portfolio tracker
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$APP_USER
Group=$APP_GROUP
WorkingDirectory=$INSTALL_ROOT/current
ExecStart=/usr/bin/dotnet $INSTALL_ROOT/current/Server.dll
Restart=on-failure
RestartSec=5
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=PORT=$PORT
Environment=PORTFOLIO_DATA_DIR=$DATA_DIR
NoNewPrivileges=true
PrivateTmp=true
ProtectHome=true
ProtectSystem=strict
ReadWritePaths=$DATA_DIR

[Install]
WantedBy=multi-user.target
UNIT

PREVIOUS_TARGET="$(readlink "$INSTALL_ROOT/current" 2>/dev/null || true)"
rm -f "$INSTALL_ROOT/current.next"
ln -s "$RELEASE_DIR" "$INSTALL_ROOT/current.next"
mv -Tf "$INSTALL_ROOT/current.next" "$INSTALL_ROOT/current"
systemctl daemon-reload
systemctl enable "$SERVICE_NAME.service" >/dev/null
systemctl restart "$SERVICE_NAME.service"

log "Waiting for the health endpoint"
ready=false
for _ in $(seq 1 30); do
  if curl -fsS --max-time 2 "http://127.0.0.1:$PORT/api/health" >/dev/null; then
    ready=true
    break
  fi
  sleep 2
done

if [[ "$ready" != true ]]; then
  log "Service health check failed; restoring the previous release"
  if [[ -n "$PREVIOUS_TARGET" ]]; then
    rm -f "$INSTALL_ROOT/current.rollback"
    ln -s "$PREVIOUS_TARGET" "$INSTALL_ROOT/current.rollback"
    mv -Tf "$INSTALL_ROOT/current.rollback" "$INSTALL_ROOT/current"
    systemctl restart "$SERVICE_NAME.service" || true
  else
    systemctl stop "$SERVICE_NAME.service" || true
  fi
  journalctl -u "$SERVICE_NAME.service" -n 80 --no-pager >&2 || true
  die "Deployment did not become healthy."
fi

log "Deployment succeeded"
printf 'Service:     %s.service\n' "$SERVICE_NAME"
printf 'Local URL:   http://127.0.0.1:%s\n' "$PORT"
printf 'Data dir:    %s\n' "$DATA_DIR"
printf 'Release:     %s\n' "$RELEASE_DIR"
printf '\nThe app listens on all interfaces at port %s. Put it behind HTTPS (for example, Nginx + Let\x27s Encrypt) before exposing it publicly.\n' "$PORT"
printf 'Logs: journalctl -u %s.service -f\n' "$SERVICE_NAME"
