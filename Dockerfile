#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app

EXPOSE 80

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
WORKDIR "/src/"
RUN dotnet restore "KSol.RDPGateway/KSol.RDPGateway.csproj"
RUN dotnet build "KSol.RDPGateway/KSol.RDPGateway.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "KSol.RDPGateway/KSol.RDPGateway.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
# Server-side session recording (background RecordingMuxService): ffmpeg remuxes the captured raw H.264
# + PCM into MP4 and encodes the redirected camera NV12; mkvmerge (mkvtoolnix) applies the per-frame
# timestamps to the copied H.264 for true variable-frame-rate output (ffmpeg can't take a per-frame PTS
# sidecar for -c copy). Slim install, apt lists pruned to keep the image small.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg mkvtoolnix \
    && rm -rf /var/lib/apt/lists/*
COPY --from=publish /app/publish .
ENV ASPNETCORE_HTTP_PORTS=80
# Persist the SQLite database, the generated OAuth/OIDC signing & encryption certificates, the
# DataProtection keyring (stored VM credentials), and session recordings — all under /app/Data so they
# survive container restarts/recreation.
VOLUME ["/app/Data"]
ENTRYPOINT ["dotnet", "KSol.RDPGateway.dll"]