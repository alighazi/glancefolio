#!/usr/bin/env bash
# Toolchain environment for the Portfolio web app.
#
# The .NET SDK is not part of the repo (it is a ~250MB toolchain), so it lives
# under ~/.local - which also keeps it out of workspace snapshots. If your
# machine already has `dotnet` on PATH, this is a no-op.
#
# Usage:  source env.sh        (then: dotnet build / dotnet run)
if ! command -v dotnet >/dev/null 2>&1; then
    export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.local/dotnet}"
    export PATH="$DOTNET_ROOT:$PATH"
fi
# Keep the NuGet cache out of the workspace snapshot too.
export NUGET_PACKAGES="${NUGET_PACKAGES:-$HOME/.local/nuget}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
