# syntax=docker/dockerfile:1
#
# Build context is the repository root, because the API compiles shared source
# from src/WorkPlanStudio and references two sibling libraries:
#
#   docker build -f src/WorkPlanStudio.Api/Dockerfile -t workplan-api .
#
# No secret is baked in. The signing key, the connection string and the allowed
# origins all arrive as environment variables at run time, and the application
# refuses to start without a usable key.

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, from the project files alone, so a source-only change does not
# invalidate the package layer.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/WorkPlanStudio.Api/WorkPlanStudio.Api.csproj src/WorkPlanStudio.Api/
COPY src/WorkPlanStudio.Contracts/WorkPlanStudio.Contracts.csproj src/WorkPlanStudio.Contracts/
COPY src/WorkPlanStudio.Scheduling/WorkPlanStudio.Scheduling.csproj src/WorkPlanStudio.Scheduling/
COPY src/WorkPlanStudio.WorkingTime/WorkPlanStudio.WorkingTime.csproj src/WorkPlanStudio.WorkingTime/
RUN dotnet restore src/WorkPlanStudio.Api/WorkPlanStudio.Api.csproj

COPY src/ src/
RUN dotnet publish src/WorkPlanStudio.Api/WorkPlanStudio.Api.csproj \
    -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ---------- run ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# The images ship a non-root "app" user (uid 1654). The database file lives in a
# directory that user owns, so the container needs no write access anywhere else.
WORKDIR /app
RUN mkdir -p /data && chown app:app /data
USER app

COPY --from=build --chown=app:app /app/publish .

# No port is hard-coded anywhere in the application; this is the container's
# default and a deployment overrides it.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ConnectionStrings__Default="Data Source=/data/workplan-api.db"
EXPOSE 8080

# No HEALTHCHECK instruction on purpose. The aspnet runtime image ships neither
# curl nor wget, and adding one to run a probe would enlarge the attack surface
# of the image for something the orchestrator already does better from outside:
# point its probes at /health (liveness) and /health/ready (readiness).

ENTRYPOINT ["dotnet", "WorkPlanStudio.Api.dll"]
