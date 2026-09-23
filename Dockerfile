# Dupli management server (control plane + web UI). Build from the repository root:
#   docker build -t dupli-server .
FROM node:24-alpine AS web
WORKDIR /src/src/Dupli.Web
COPY src/Dupli.Web/package.json src/Dupli.Web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY src/Dupli.Web/ ./
# angular.json writes the build into ../Dupli.Server/wwwroot
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/ src/
COPY --from=web /src/src/Dupli.Server/wwwroot src/Dupli.Server/wwwroot
ARG VERSION=0.1.0
RUN dotnet publish src/Dupli.Server/Dupli.Server.csproj -c Release -p:Version=$VERSION -o /app --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# Data Protection key ring and restic mirror: mounted volumes, separate from the database volume.
RUN mkdir -p /var/lib/dupli/keys /var/lib/dupli/tools && chown -R app:app /var/lib/dupli
VOLUME ["/var/lib/dupli/keys", "/var/lib/dupli/tools"]

USER app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Dupli.Server.dll"]
