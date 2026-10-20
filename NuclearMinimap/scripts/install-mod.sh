#!/usr/bin/env bash

set -euo pipefail

VERBOSE=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    -v|--verbose)
      VERBOSE=1
      shift
      ;;
    *)
      echo "Unknown option: $1" >&2
      echo "Usage: $0 [-v|--verbose]" >&2
      exit 1
      ;;
  esac
done

log_verbose() {
  if [[ "$VERBOSE" -eq 1 ]]; then
    echo "$@"
  fi
}

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
MOD_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"
WORKSPACE_ROOT="$(cd -- "$SCRIPT_DIR/../.." && pwd)"

load_env_file() {
  local env_file="$1"
  if [[ -f "$env_file" ]]; then
    log_verbose "Loading environment from $env_file"
    # shellcheck disable=SC1090
    source "$env_file"
  fi
}

if [[ -n "${ENV_FILE:-}" ]]; then
  load_env_file "$ENV_FILE"
else
  load_env_file "$WORKSPACE_ROOT/.env"
  load_env_file "$MOD_ROOT/.env"
fi

: "${STEAMAPPS_DIR:?Set STEAMAPPS_DIR in the workspace .env, mod .env, or environment}"

BUILD_CONFIGURATION="${BUILD_CONFIGURATION:-Release}"
GAME_DIR="${GAME_DIR:-$STEAMAPPS_DIR/common/Nuclear Option}"
GAME_BEPINEX_DIR="${GAME_BEPINEX_DIR:-$GAME_DIR/BepInEx}"
GAME_BEPINEX_PLUGINS_DIR="${GAME_BEPINEX_PLUGINS_DIR:-$GAME_BEPINEX_DIR/plugins}"
NO_MANAGED_DIR="${NO_MANAGED_DIR:-$GAME_DIR/NuclearOption_Data/Managed}"
NO_BEPINEX_CORE_DIR="${NO_BEPINEX_CORE_DIR:-$GAME_BEPINEX_DIR/core}"
MOD_INSTALL_DIR="${MOD_INSTALL_DIR:-$GAME_BEPINEX_PLUGINS_DIR/NuclearMinimap}"
BUILD_OUTPUT_DIR="$MOD_ROOT/bin/$BUILD_CONFIGURATION"

echo "Building NuclearMinimap ($BUILD_CONFIGURATION)..."
dotnet build "$MOD_ROOT/NuclearMinimap.csproj" -c "$BUILD_CONFIGURATION" \
  /p:NuclearOptionManagedDir="$NO_MANAGED_DIR" \
  /p:BepInExCoreDir="$NO_BEPINEX_CORE_DIR"

mkdir -p "$MOD_INSTALL_DIR"

cp "$BUILD_OUTPUT_DIR/NuclearMinimap.dll" "$MOD_INSTALL_DIR/"

if [[ -f "$BUILD_OUTPUT_DIR/NuclearMinimap.pdb" ]]; then
  cp "$BUILD_OUTPUT_DIR/NuclearMinimap.pdb" "$MOD_INSTALL_DIR/"
fi

cp "$MOD_ROOT/README.md" "$MOD_ROOT/THIRD-PARTY-NOTICES.txt" "$MOD_INSTALL_DIR/"
echo "Installed to: $MOD_INSTALL_DIR"

