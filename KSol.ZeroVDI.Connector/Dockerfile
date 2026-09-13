# ZeroVDI connector agent. Holds an outbound control WebSocket to the gateway and tunnels TCP targets over
# per-request data channels. Runs stateless when configured via env vars (see below); persists an on-disk
# config only when self-enrolling from a registration token.
#
# Configuration (environment):
#   ZEROVDI_URL                 gateway base URL, e.g. https://gw.example.com  (required)
#   ZEROVDI_AUTH_TOKEN          long-lived auth token → fully stateless, no volume needed
#   ZEROVDI_REGISTRATION_TOKEN  one-time token → self-enroll on first boot (mount /data to persist)
#   ZEROVDI_CONNECTOR_ID        optional, informational
#   ZEROVDI_CONNECTOR_HOME      config dir (defaults to /data in this image)
#
# Examples:
#   docker run --rm -e ZEROVDI_URL=https://gw.example.com -e ZEROVDI_AUTH_TOKEN=… \
#     ghcr.io/mkenfenheuer/zerovdi/connector:latest
#   docker run -v zerovdi-connector:/data -e ZEROVDI_URL=… -e ZEROVDI_REGISTRATION_TOKEN=… \
#     ghcr.io/mkenfenheuer/zerovdi/connector:latest

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY KSol.ZeroVDI.Connector/KSol.ZeroVDI.Connector.csproj KSol.ZeroVDI.Connector/
RUN dotnet restore "KSol.ZeroVDI.Connector/KSol.ZeroVDI.Connector.csproj"
COPY KSol.ZeroVDI.Connector/ KSol.ZeroVDI.Connector/
# The one gateway source file the connector links (see the csproj).
COPY KSol.ZeroVDI/RDP/RdpPortProbe.cs KSol.ZeroVDI/RDP/
RUN dotnet publish "KSol.ZeroVDI.Connector/KSol.ZeroVDI.Connector.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final

LABEL org.opencontainers.image.title="ZeroVDI connector" \
      org.opencontainers.image.description="Outbound-only tunnel agent for ZeroVDI" \
      org.opencontainers.image.vendor="KSol.IT"

WORKDIR /app
COPY --from=build /app/publish .
# Self-enrollment persists the auth token here; mount a volume to keep it across restarts.
ENV ZEROVDI_CONNECTOR_HOME=/data
RUN mkdir -p /data && chown -R 1654:1654 /app /data
VOLUME ["/data"]

# Unprivileged: the agent only makes outbound connections and needs no capability. A host bind mount
# over /data must be owned by this UID (chown -R 1654:1654 <path>); a named volume needs nothing.
USER 1654

ENTRYPOINT ["dotnet", "KSol.ZeroVDI.Connector.dll", "run"]
