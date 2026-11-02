#!/usr/bin/env bash
# Build the mod and run the headless tests on Linux without NuGet access (e.g. a Claude
# workspace), using Ubuntu's .NET 8 SDK and Mono's .NET 4.7.2 reference assemblies.
#
# Needs: the game DLLs the csproj references, copied read-only from a real install into
#   <dir>/BattleTech_Data/Managed/ (never commit them), and GameDir.props pointing at <dir>.
#
# Usage: tools/build-linux.sh [--install] [--tests]
#   --install  apt-get the .NET 8 SDK and mono-devel first (needs root)
#   --tests    also run tests/MovementTests, tests/SettingsTests and tests/NetTests
set -euo pipefail
cd "$(dirname "$0")/.."

install=0; tests=0
for arg in "$@"; do
  case "$arg" in
    --install) install=1 ;;
    --tests) tests=1 ;;
    *) echo "unknown option $arg" >&2; exit 2 ;;
  esac
done

if [ "$install" = 1 ]; then
  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq dotnet-sdk-8.0 mono-devel
fi

refs=/usr/lib/mono/4.7.2-api
[ -d "$refs" ] || { echo "missing $refs (install mono-devel)" >&2; exit 1; }
[ -f GameDir.props ] || { echo "missing GameDir.props (copy GameDir.props.example, set BattleTechDir)" >&2; exit 1; }

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
# Nothing here uses NuGet packages; an empty local feed keeps restore from calling nuget.org.
feed="$(mktemp -d)"
trap 'rm -rf "$feed"' EXIT

dotnet build src/BattletechCoop -c Release --source "$feed" \
  -p:FrameworkPathOverride="$refs" -p:AutomaticallyUseReferenceAssemblyPackages=false

if [ "$tests" = 1 ]; then
  for t in MovementTests SettingsTests NetTests; do
    dotnet restore "tests/$t" --source "$feed" >/dev/null
    dotnet run --no-restore --project "tests/$t"
  done
fi
