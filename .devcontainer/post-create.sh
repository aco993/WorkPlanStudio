#!/usr/bin/env bash
# Runs once, when the container is created. Restores and builds everything so
# the first `dotnet run` or `dotnet test` does not pay for it, then says what to
# type next. Failing here is deliberate: a container that comes up with a broken
# build would only move the surprise to the reviewer's first command.
set -euo pipefail

dotnet --info | sed -n '1,3p'
dotnet restore WorkPlanStudio.slnx
dotnet build WorkPlanStudio.slnx -c Release --no-restore

cat <<'NEXT'

Ready. Try:
  dotnet run --project src/WorkPlanStudio/WorkPlanStudio.csproj -c Release --launch-profile http
      the app, on the forwarded port 5235
  ./tests/WorkPlanStudio.Scheduling.Tests/bin/Release/net10.0/WorkPlanStudio.Scheduling.Tests
      the engine's tests, in seconds
  python3 -m unittest discover -s .github/scripts -p 'test_*.py'
      the repository checks

The browser suite (Playwright) needs a Chromium download first; see docs/TESTING.md.
NEXT
