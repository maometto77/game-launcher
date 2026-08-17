#!/usr/bin/env bash
#
# Sets up the relay on a fresh Debian or Ubuntu VPS.
#
#   sudo ./deploy-vps.sh relay.example.com you@example.com
#
# Installs Docker if it is missing, writes .env, brings the stack up behind
# Caddy, and waits for the relay to report healthy through TLS.
#
# Safe to re-run: it updates the running stack rather than starting a second one.

set -euo pipefail

DOMAIN="${1:-}"
ACME_EMAIL="${2:-}"

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/.." && pwd)"

log()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m!!\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31mxx\033[0m %s\n' "$*" >&2; exit 1; }

# ---------------------------------------------------------------------------
# Arguments
# ---------------------------------------------------------------------------
[[ -n "${DOMAIN}" ]]     || die "usage: $0 <domain> <acme-email>"
[[ -n "${ACME_EMAIL}" ]] || die "usage: $0 <domain> <acme-email>"

[[ "${DOMAIN}" == *.* ]] || die "'${DOMAIN}' is not a domain name."
[[ "${ACME_EMAIL}" == *@*.* ]] || die "'${ACME_EMAIL}' is not an email address."

# ---------------------------------------------------------------------------
# Preconditions
#
# Checked up front rather than discovered halfway through. A certificate request
# that fails because DNS is not ready counts against the Let's Encrypt rate
# limit, and five failures locks the name out for an hour.
# ---------------------------------------------------------------------------
[[ "${EUID}" -eq 0 ]] || die "run this with sudo — it installs packages and manages services."

log "Checking that ${DOMAIN} resolves to this machine"

resolved="$(getent hosts "${DOMAIN}" | awk '{print $1}' | head -1 || true)"

if [[ -z "${resolved}" ]]; then
    die "${DOMAIN} does not resolve. Point its A record at this VPS first, and wait for it to propagate."
fi

public_ip="$(curl --fail --silent --max-time 10 https://api.ipify.org || true)"

if [[ -n "${public_ip}" && "${resolved}" != "${public_ip}" ]]; then
    warn "${DOMAIN} resolves to ${resolved}, but this machine appears to be ${public_ip}."
    warn "If that is wrong, Caddy will fail to obtain a certificate."
    read -r -p "Continue anyway? [y/N] " reply
    [[ "${reply}" =~ ^[Yy]$ ]] || die "Stopped."
fi

for port in 80 443; do
    if ss -lnt "sport = :${port}" 2>/dev/null | grep -q LISTEN; then
        die "Port ${port} is already in use. Stop whatever is bound to it (often Apache or Nginx) and re-run."
    fi
done

# ---------------------------------------------------------------------------
# Docker
# ---------------------------------------------------------------------------
if ! command -v docker >/dev/null 2>&1; then
    log "Installing Docker"

    # The convenience script from Docker themselves. Pinned to nothing on
    # purpose: it resolves the right repository for whatever this distribution
    # turns out to be, which a hand-written apt stanza would get wrong on half
    # the images a VPS provider offers.
    curl --fail --silent --show-error --location https://get.docker.com | sh
else
    log "Docker is already installed"
fi

docker compose version >/dev/null 2>&1 || die "The Docker Compose plugin is missing. Install docker-compose-plugin."

systemctl enable --now docker >/dev/null 2>&1 || true

# ---------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------
ENV_FILE="${SCRIPT_DIR}/.env"

log "Writing ${ENV_FILE}"

if [[ -f "${ENV_FILE}" ]]; then
    cp "${ENV_FILE}" "${ENV_FILE}.bak.$(date +%Y%m%d%H%M%S)"
fi

cat > "${ENV_FILE}" <<EOF
RELAY_DOMAIN=${DOMAIN}
RELAY_ACME_EMAIL=${ACME_EMAIL}
RELAY_ALLOWED_ORIGIN=
EOF

chmod 600 "${ENV_FILE}"

# ---------------------------------------------------------------------------
# Firewall
#
# Only if ufw is both installed and already active. Enabling a firewall on a
# machine reached over SSH, from a script, is how people lock themselves out.
# ---------------------------------------------------------------------------
if command -v ufw >/dev/null 2>&1 && ufw status | grep -q "Status: active"; then
    log "Opening 80 and 443 in ufw"
    ufw allow 80/tcp  >/dev/null
    ufw allow 443/tcp >/dev/null
else
    warn "ufw is not active; no firewall rules were changed. Ensure 80 and 443 are reachable."
fi

# ---------------------------------------------------------------------------
# Build and start
# ---------------------------------------------------------------------------
log "Building the relay image"
docker compose --project-directory "${SCRIPT_DIR}" -f "${SCRIPT_DIR}/compose.yml" build

log "Starting the stack"
docker compose --project-directory "${SCRIPT_DIR}" -f "${SCRIPT_DIR}/compose.yml" up -d

# ---------------------------------------------------------------------------
# Verify
#
# Through the public name over TLS, which is the only check that proves the
# whole path works: DNS, the certificate, the proxy and the relay behind it.
# ---------------------------------------------------------------------------
log "Waiting for https://${DOMAIN}/health"

for attempt in $(seq 1 60); do
    if curl --fail --silent --max-time 5 "https://${DOMAIN}/health" >/dev/null 2>&1; then
        log "Relay is healthy at https://${DOMAIN}"
        echo
        echo "  Point the launcher at it:  Settings → Relay → https://${DOMAIN}"
        echo "  Logs:                      docker compose -f ${SCRIPT_DIR}/compose.yml logs -f"
        echo "  Update after a git pull:   ${SCRIPT_DIR}/deploy-vps.sh ${DOMAIN} ${ACME_EMAIL}"
        echo
        exit 0
    fi

    # The first attempts are expected to fail: Caddy is still obtaining the
    # certificate, which takes a few seconds against Let's Encrypt.
    [[ $((attempt % 10)) -eq 0 ]] && log "still waiting (${attempt}/60)"
    sleep 2
done

warn "The relay did not become healthy within two minutes."
warn "Recent logs:"
docker compose --project-directory "${SCRIPT_DIR}" -f "${SCRIPT_DIR}/compose.yml" logs --tail 40
die "Deployment did not verify."
