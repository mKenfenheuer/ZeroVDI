# ZeroVDI gateway image.
#
# Multi-stage: the SDK stage restores and publishes, the runtime stage carries only the published
# output plus the few external tools the recording pipeline shells out to. The final image runs as a
# non-root user (UID 1654) — a gateway that terminates RDP for a whole organisation has no business
# running as root, and nothing it does needs it.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore first, against the project files alone, so a source-only change reuses the restore layer.
COPY KSol.ZeroVDI/KSol.ZeroVDI.csproj KSol.ZeroVDI/
RUN dotnet restore "KSol.ZeroVDI/KSol.ZeroVDI.csproj"

COPY . .
# SkipTailwindBuild: the compiled wwwroot/css/app.css is committed, and the build agent has no
# node_modules — the csproj target skips itself, this makes the intent explicit.
RUN dotnet publish "KSol.ZeroVDI/KSol.ZeroVDI.csproj" \
      -c Release -o /app/publish \
      -p:SkipTailwindBuild=true \
      --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final

LABEL org.opencontainers.image.title="ZeroVDI" \
      org.opencontainers.image.description="Browser-based VDI gateway for Proxmox VE" \
      org.opencontainers.image.vendor="KSol.IT" \
      org.opencontainers.image.licenses="See LICENSE"

# Server-side session recording (background RecordingMuxService): ffmpeg remuxes the captured raw H.264
# + PCM into MP4 and encodes the redirected camera NV12; mkvmerge (mkvtoolnix) applies the per-frame
# timestamps to the copied H.264 for true variable-frame-rate output (ffmpeg can't take a per-frame PTS
# sidecar for -c copy). ipmitool and samba-common-bin back the IPMI and Windows shutdown methods.
# Slim install, apt lists pruned to keep the image small.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg mkvtoolnix ipmitool samba-common-bin curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

# Persist the SQLite database, the DataProtection keyring (without which every stored VM credential and
# encrypted recording is unrecoverable) and session recordings — all under /app/Data so they survive
# container restarts and recreation.
#
# The directory is created and handed to the runtime user here. A HOST bind mount brings its own
# ownership, so an existing deployment must chown it once:  chown -R 1654:1654 /mnt/data/rdpgw
RUN mkdir -p /app/Data && chown -R 1654:1654 /app
VOLUME ["/app/Data"]

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080

# Unprivileged. The app binds 8080 (not 80) precisely so it needs no capability to do it.
USER 1654

# /healthz is anonymous and touches nothing but the process itself, so an unhealthy container is a
# genuinely unhealthy process rather than a database or Proxmox hiccup.
HEALTHCHECK --interval=30s --timeout=5s --start-period=40s --retries=3 \
    CMD curl --fail --silent --show-error http://127.0.0.1:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "KSol.ZeroVDI.dll"]
