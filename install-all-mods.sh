#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
ENV_FILE="${ENV_FILE:-$SCRIPT_DIR/.env}"

if [[ -f "$ENV_FILE" ]]; then
  # shellcheck disable=SC1090
  set -a
  source "$ENV_FILE"
  set +a
fi

export ENV_FILE

MOD_DIRS=(
  "NuclearConnectivity"
  "NuclearControllerGestures"
  "NuclearDeathBlackout"
  "NuclearDelivery"
  "NuclearMapPlayerNames"
  "NuclearMapTrails"
  "NuclearMinimap"
  "NuclearMfdWeapons"
  "NuclearSatelliteMap"
  "NuclearTargeting"
  "NuclearSounds"
  "NuclearTrackAge"
  "NuclearUnits"
)

declare -a pids=()
declare -a names=()

for mod_dir in "${MOD_DIRS[@]}"; do
  install_script="$SCRIPT_DIR/$mod_dir/scripts/install-mod.sh"

  if [[ ! -x "$install_script" && ! -f "$install_script" ]]; then
    echo "Missing install script: $install_script" >&2
    exit 1
  fi

  echo "==> Installing $mod_dir"
  ENV_FILE="$ENV_FILE" bash "$install_script" "$@" &
  pids+=("$!")
  names+=("$mod_dir")
done

failed=0

for i in "${!pids[@]}"; do
  if ! wait "${pids[$i]}"; then
    echo "Install failed: ${names[$i]}" >&2
    failed=1
  fi
done

if [[ "$failed" -ne 0 ]]; then
  exit 1
fi

echo "All requested mods installed."
