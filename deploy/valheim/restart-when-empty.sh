#!/bin/bash
# Restarts the Valheim server with a new mod DLL once nobody is playing on it.
#
# Put the new build beside the live one - that is
# /srv/valheim/server/BepInEx/plugins/AstvardServerMod.dll.new, not /srv/valheim - and
# chown it to valheim, then:
#   systemd-run --unit=astvard-restart --collect /bin/bash /srv/astvard/deploy/valheim/restart-when-empty.sh
# A transient unit, so it outlives the SSH session that started it. Progress goes to
# /root/astvard-restart-status.txt and ends with DONE, GAVE UP or STOP. The previous build
# stays in /srv/valheim as AstvardServerMod.dll.<md5>.bak.

cd /srv/astvard || exit 1
LOG=/srv/valheim/logs/server.log
BEP=/srv/valheim/server/BepInEx/LogOutput.log
P=/srv/valheim/server/BepInEx/plugins
STATUS=/root/astvard-restart-status.txt
: > "$STATUS"

say() { echo "$(date -u +%H:%M:%S) $*" >> "$STATUS"; }

# Players by the game's own log: a join without a close since the server started.
count_online() {
  awk '/Got connection SteamID/{on[$NF]=1} /Closing socket/{delete on[$NF]} END{n=0; for (k in on) n++; print n}' "$LOG"
}

# Packets from outside to the game ports in ten seconds. A client in the world sends
# hundreds a second; a stray scanner or Steam's own chatter is a handful.
remote_packets() {
  timeout 10 tcpdump -ni any -q "udp and (port 2456 or port 2457)" 2>/dev/null \
    | awk '{for (i=1;i<=NF;i++) if ($i ~ /^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+:?$/) { a=$i; sub(/\.[0-9]+:?$/, "", a); print a } }' \
    | grep -v '^172\.\|^127\.\|^72\.61\.139\.115$' | wc -l
}

# Whether the game log says "Game server connected" at or after $1 (YYYYmmddHHMMSS read
# from the container's own clock). Until the new run writes, the file still holds the last
# run's lines - a wait that only looked for the line was satisfied at once - and the stamps
# in it are the container's local time, not the host's.
connected_since() {
  awk -v since="$1" '/Game server connected/ { split($1, d, "/"); t = $2; gsub(":", "", t); if (d[3] d[1] d[2] t >= since) found = 1 } END { exit !found }' "$LOG"
}

say "waiting for the server to empty"
deadline=$(( $(date +%s) + 12 * 3600 ))
quiet=0
while :; do
  if [ "$(date +%s)" -gt "$deadline" ]; then say "GAVE UP: still occupied after 12 h, nothing touched"; exit 2; fi

  online=$(count_online)
  packets=$(remote_packets)
  if [ "$packets" -lt 50 ]; then quiet=$((quiet + 1)); else quiet=0; fi

  # The log says everyone left and nothing is coming in: empty.
  if [ "$online" = "0" ] && [ "$quiet" -ge 2 ]; then break; fi
  # The log still lists someone, but no client has sent anything for about two minutes:
  # a crashed game whose close the log never got.
  if [ "$online" != "0" ] && [ "$quiet" -ge 5 ]; then
    say "log still lists $online online, but no traffic for ~2 min - treating as gone"
    break
  fi

  sleep 20
done

say "empty (log online=$online, packets in 10 s=$packets) - stopping"
if [ ! -f "$P/AstvardServerMod.dll.new" ]; then say "STOP: no staged DLL, nothing touched"; exit 4; fi

t0=$(date +%s)
docker compose -f deploy/valheim/compose.yml stop >> "$STATUS" 2>&1
say "stopped in $(( $(date +%s) - t0 )) s"
grep "OnApplicationQuit\|World save (5/5) done" "$LOG" | tail -2 >> "$STATUS"

old=$(md5sum "$P/AstvardServerMod.dll" | cut -c1-8)
cp -p "$P/AstvardServerMod.dll" "/srv/valheim/AstvardServerMod.dll.$old.bak"
mv -f "$P/AstvardServerMod.dll.new" "$P/AstvardServerMod.dll"
chown valheim:valheim "$P/AstvardServerMod.dll"
say "previous build kept as /srv/valheim/AstvardServerMod.dll.$old.bak"
md5sum "$P/AstvardServerMod.dll" >> "$STATUS"

t1=$(date +%s)
docker compose -f deploy/valheim/compose.yml start >> "$STATUS" 2>&1
since=$(docker exec astvard-valheim date +%Y%m%d%H%M%S 2>/dev/null)
[ -n "$since" ] || say "could not read the container clock - waiting out the full 6 min"
for i in $(seq 1 72); do
  if [ -n "$since" ] && connected_since "$since" \
     && [ "$(stat -c %Y "$BEP")" -ge "$t1" ] && grep -q "Kept zones" "$BEP"; then break; fi
  sleep 5
done
say "started, waited $((i * 5)) s (container clock at start: $since)"
docker ps --format '{{.Names}} {{.Status}}' | grep astvard-valheim >> "$STATUS"
grep -h "Valheim version\|Game server connected" "$LOG" | tail -2 >> "$STATUS"
grep -h "Loading \[AstvardServerMod\|Kept zones\|Resource rate\|Site builds\|site builds\|Exception\|\[Error" "$BEP" \
  | grep -v Ambiguous | tail -12 >> "$STATUS"
say "DONE"
