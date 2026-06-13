#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app

EXPOSE 80

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["KSol.RDPGateway.csproj", "./"]
RUN dotnet restore "KSol.RDPGateway.csproj"
COPY . .
WORKDIR "/src/"
RUN dotnet build "KSol.RDPGateway.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "KSol.RDPGateway.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENV ASPNETCORE_HTTP_PORTS=80
# Persist the SQLite database and the generated OAuth/OIDC signing & encryption certificates.
# Both live in /app/Data so issued tokens survive container restarts/recreation.
VOLUME ["/app/Data"]
ENTRYPOINT ["dotnet", "KSol.RDPGateway.dll"]