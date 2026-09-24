#!/usr/bin/env bash
# Compile lprdv, le pose sur le VPS, et vérifie qu'il répond.
#
#   LPRDV_HOST=debian@203.0.113.7 ./deploy/deploy.sh
#   LPRDV_KEY=~/.ssh/ma_cle        clé SSH, facultative
#
# Le compte distant a besoin de sudo. Le script est rejouable : il crée le
# compte lprdv et installe l'unité au premier passage, et se contente ensuite
# de remplacer le binaire et de redémarrer.
set -euo pipefail

: "${LPRDV_HOST:?LPRDV_HOST=utilisateur@hôte manquant}"
cd "$(dirname "$0")/.."

ssh_opts=(-o BatchMode=yes -o ConnectTimeout=10)
if [ -n "${LPRDV_KEY:-}" ]; then ssh_opts+=(-i "$LPRDV_KEY" -o IdentitiesOnly=yes); fi

version=$(git describe --tags --always --dirty)
out=$(mktemp -d)
trap 'rm -rf "$out"' EXIT

echo "==> Compilation de $version"
dotnet publish Linkpearl.Rendezvous -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
  -p:Version="${version#v}" -o "$out" --nologo -v quiet

echo "==> Envoi vers $LPRDV_HOST"
scp "${ssh_opts[@]}" -q "$out/lprdv" deploy/lprdv.service "$LPRDV_HOST:/tmp/"

echo "==> Installation et redémarrage"
ssh "${ssh_opts[@]}" "$LPRDV_HOST" sudo bash -s <<'EOF'
set -euo pipefail
id lprdv >/dev/null 2>&1 || useradd --system --home-dir /var/lib/lprdv --shell /usr/sbin/nologin lprdv
install -d -m 0755 /opt/lprdv
# Remplacé par un renommage : le binaire en cours reste intact jusqu'au redémarrage.
install -m 0755 /tmp/lprdv /opt/lprdv/lprdv.new
mv -f /opt/lprdv/lprdv.new /opt/lprdv/lprdv
install -m 0644 /tmp/lprdv.service /etc/systemd/system/lprdv.service
rm -f /tmp/lprdv /tmp/lprdv.service
systemctl daemon-reload
systemctl enable --quiet lprdv
systemctl restart lprdv
# La console n'écoute qu'en local : /healthz s'interroge depuis le VPS.
for _ in $(seq 20); do
  if curl -fs -o /dev/null http://127.0.0.1:47901/healthz; then
    echo "==> En ligne"
    exit 0
  fi
  sleep 1
done
echo "!! /healthz ne répond pas" >&2
journalctl -u lprdv -n 30 --no-pager >&2
exit 1
EOF
