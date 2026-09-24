# Linux test container for the Dupli agent (the supported agent platform is Windows; this image exists to
# exercise the server end to end without a VM). Build from the repository root:
#   docker build -f deploy/agent/Dockerfile -t dupli-agent-linux .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/Dupli.Contracts/ src/Dupli.Contracts/
COPY src/Dupli.Agent.Core/ src/Dupli.Agent.Core/
COPY src/Dupli.Agent/ src/Dupli.Agent/
RUN case "$TARGETARCH" in arm64) rid=linux-arm64 ;; *) rid=linux-x64 ;; esac \
 && dotnet publish src/Dupli.Agent/Dupli.Agent.csproj -c Release -r "$rid" --self-contained \
      -p:PublishSingleFile=true -o /out --nologo

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0
# curl + jq for the bootstrap script, pg_dump/pg_restore 18 from the PostgreSQL apt repository.
# libgssapi-krb5-2: Npgsql (Dupli.Agent.Core) loads it for GSS negotiation even without Integrated Security.
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl jq libgssapi-krb5-2 \
 && install -d /usr/share/postgresql-common/pgdg \
 && curl -fsSL -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc https://www.postgresql.org/media/keys/ACCC4CF8.asc \
 && . /etc/os-release \
 && echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://apt.postgresql.org/pub/repos/apt ${VERSION_CODENAME}-pgdg main" \
      > /etc/apt/sources.list.d/pgdg.list \
 && apt-get update \
 && apt-get install -y --no-install-recommends postgresql-client-18 \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /out/dupli-agent /usr/local/bin/dupli-agent
COPY deploy/agent/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh

# Runs as root, like the Windows service runs as LocalSystem.
ENV DUPLI_HOME=/var/lib/dupli-agent
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
