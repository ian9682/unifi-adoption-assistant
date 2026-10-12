#!/bin/sh
# Starts a simulated UniFi access point for the integration tests, on a disposable Linux
# machine or container (it creates a root-equivalent user "ubnt" and edits /etc/hosts).
# Needs: dropbear-bin, iputils-ping. Then run:
#   UNIFI_IT_SSH_PORT=2222 dotnet run --project tests/UniFiAdoption.Tests -c Release -- --integration
set -e
HERE=$(cd "$(dirname "$0")" && pwd)
S=/tmp/fake-unifi
mkdir -p $S
printf '80:2a:a8:12:ab:cd' > $S/mac
printf 'Not Adopted' > $S/state
printf 'http://unifi:8080/inform' > $S/inform
install -m 755 "$HERE/mca-cli-op" /usr/bin/mca-cli-op
install -m 755 "$HERE/mca-cli" /usr/bin/mca-cli
id ubnt >/dev/null 2>&1 || useradd -o -u 0 -g 0 -M -d /root -s /bin/sh ubnt
echo 'ubnt:ubnt' | chpasswd
grep -q controller.test /etc/hosts || echo '127.0.0.2 controller.test' >> /etc/hosts
[ -f $S/hostkey ] || dropbearkey -t ed25519 -f $S/hostkey >/dev/null
pkill -x dropbear || true
setsid dropbear -r $S/hostkey -p 127.0.0.1:2222 -E -F > $S/dropbear.log 2>&1 < /dev/null &
sleep 1
echo "Simulated access point listening on 127.0.0.1:2222"
