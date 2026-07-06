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
#   docker run --rm -e ZEROVDI_URL=https://gw.example.com -e ZEROVDI_AUTH_TOKEN=… ghcr.io/…/connector
#   docker run -v zerovdi-connector:/data -e ZEROVDI_URL=… -e ZEROVDI_REGISTRATION_TOKEN=… ghcr.io/…/connector

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY KSol.ZeroVDI.Connector/KSol.ZeroVDI.Connector.csproj KSol.ZeroVDI.Connector/
RUN dotnet restore "KSol.ZeroVDI.Connector/KSol.ZeroVDI.Connector.csproj"
COPY KSol.ZeroVDI.Connector/ KSol.ZeroVDI.Connector/
RUN dotnet publish "KSol.ZeroVDI.Connector/KSol.ZeroVDI.Connector.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
# Self-enrollment persists the auth token here; mount a volume to keep it across restarts.
ENV ZEROVDI_CONNECTOR_HOME=/data
VOLUME ["/data"]
ENTRYPOINT ["dotnet", "KSol.ZeroVDI.Connector.dll", "run"]
