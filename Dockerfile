# Dupli management server (control plane). Build from the repository root:
#   docker build -t dupli-server .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/ src/
RUN dotnet publish src/Dupli.Server/Dupli.Server.csproj -c Release -o /app --nologo

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
