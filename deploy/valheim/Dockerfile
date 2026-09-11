# Valheim dedicated server for astvard.online.
#
# The image carries SteamCMD and the libraries the server links against; the game
# itself is not in it. `docker compose ... run --rm valheim update` installs it into
# /srv/valheim/server on the host, so an update is something someone decides to do,
# never a side effect of a restart: the client, the server and the mod DLL have to
# agree on the game version, and a restart that quietly pulled a new build would
# break that with nobody watching.
FROM ubuntu:24.04

# lib32gcc-s1 (it brings libc6-i386) is for SteamCMD, a 32-bit program. libatomic1
# is for the server itself: ldd on valheim_server.x86_64 1.0.12 finds everything else
# in the base image.
RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
      ca-certificates curl lib32gcc-s1 libatomic1 \
 && rm -rf /var/lib/apt/lists/*

# The files under /srv/valheim belong to the host's valheim user, and the container
# runs as the same numbers so both sides can read and write them. install.sh passes
# them in; the base image's own ubuntu user would otherwise sit on uid 1000.
ARG VALHEIM_UID=1000
ARG VALHEIM_GID=1000
RUN (userdel -r ubuntu 2>/dev/null || true) \
 && groupadd -g "$VALHEIM_GID" valheim \
 && useradd -m -u "$VALHEIM_UID" -g "$VALHEIM_GID" -s /bin/bash valheim

RUN mkdir -p /opt/steamcmd \
 && curl -fsSL https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz \
    | tar -xz -C /opt/steamcmd \
 && chown -R valheim:valheim /opt/steamcmd

USER valheim
# SteamCMD updates itself on its first run; doing it here keeps that out of every
# game update.
RUN /opt/steamcmd/steamcmd.sh +quit

COPY --chown=valheim:valheim server.sh /opt/astvard/server.sh
ENTRYPOINT ["/opt/astvard/server.sh"]
CMD ["start"]
