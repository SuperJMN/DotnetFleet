#!/usr/bin/env bash
# Levanta Coordinator + N Workers + GUI en local.
#
# Uso:
#   ./scripts/run-fleet.sh                    # 1 coordinator + 1 worker + GUI
#   WORKERS=3 ./scripts/run-fleet.sh          # 1 coordinator + 3 workers + GUI
#   NO_GUI=1 ./scripts/run-fleet.sh           # sin GUI (modo headless)
#   COORDINATOR_URL=http://localhost:5000 ... # override puertos / token
#
# Ctrl+C para parar todos los procesos.

set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
RUNTIME_DIR="${RUNTIME_DIR:-$ROOT/.runtime}"
LOG_DIR="$RUNTIME_DIR/logs"
mkdir -p "$LOG_DIR"

COORDINATOR_URL="${COORDINATOR_URL:-http://localhost:5000}"
REGISTRATION_TOKEN="${REGISTRATION_TOKEN:-local-dev-token}"
JWT_SECRET="${JWT_SECRET:-local-dev-jwt-secret-change-me-please-32+chars-long}"
WORKERS="${WORKERS:-1}"
NO_GUI="${NO_GUI:-0}"

PIDS=()
cleanup() {
  echo
  echo "==> Deteniendo procesos..."
  for pid in "${PIDS[@]:-}"; do
    if kill -0 "$pid" 2>/dev/null; then
      kill "$pid" 2>/dev/null || true
    fi
  done
  wait 2>/dev/null || true
  echo "==> Listo."
}
trap cleanup EXIT INT TERM

wait_for_url() {
  local url="$1"; local attempts="${2:-60}"
  for ((i=0; i<attempts; i++)); do
    # Cualquier respuesta HTTP (incluso 4xx) significa "vivo".
    if curl -s -o /dev/null -w "%{http_code}" --max-time 2 "$url" 2>/dev/null | grep -qE '^[1-5][0-9][0-9]$'; then
      return 0
    fi
    sleep 1
  done
  return 1
}

echo "==> Build (release)..."
dotnet build "$ROOT/DotnetFleet.slnx" -c Release --nologo -v minimal

COORD_DLL="$ROOT/src/DotnetFleet.Coordinator/bin/Release/net10.0/DotnetFleet.Coordinator.dll"
WORKER_DLL="$ROOT/src/DotnetFleet.Worker/bin/Release/net10.0/DotnetFleet.Worker.dll"
DESKTOP_DLL="$ROOT/src/DotnetFleet.Desktop/bin/Release/net10.0/DotnetFleet.Desktop.dll"
for d in "$COORD_DLL" "$WORKER_DLL" "$DESKTOP_DLL"; do
  [[ -f "$d" ]] || { echo "!! Falta $d (build falló?)"; exit 1; }
done

# --- Coordinator ---------------------------------------------------------
COORD_DIR="$RUNTIME_DIR/coordinator"
mkdir -p "$COORD_DIR"
COORD_PORT="${COORDINATOR_URL##*:}"
COORD_PORT="${COORD_PORT%%/*}"
if ss -ltn 2>/dev/null | grep -qE "[:.]${COORD_PORT}\b"; then
  echo "!! El puerto $COORD_PORT ya está en uso. Detén el proceso anterior antes de relanzar."
  echo "   Pista: pgrep -af DotnetFleet"
  exit 1
fi
echo "==> Coordinator → $COORDINATOR_URL  (workdir: $COORD_DIR, log: $LOG_DIR/coordinator.log)"
(
  cd "$COORD_DIR"
  ASPNETCORE_URLS="$COORDINATOR_URL" \
  Workers__RegistrationToken="$REGISTRATION_TOKEN" \
  Jwt__Secret="$JWT_SECRET" \
    dotnet "$COORD_DLL" >"$LOG_DIR/coordinator.log" 2>&1
) &
PIDS+=($!)

echo "    esperando readiness..."
if ! wait_for_url "$COORDINATOR_URL/api/auth/login" 60; then
  echo "!! Coordinator no respondió en 60s. Mira $LOG_DIR/coordinator.log"
  exit 1
fi
echo "    OK"

# --- Workers -------------------------------------------------------------
for i in $(seq 1 "$WORKERS"); do
  WDIR="$RUNTIME_DIR/worker-$i"
  mkdir -p "$WDIR"
  echo "==> Worker $i  (workdir: $WDIR, log: $LOG_DIR/worker-$i.log)"
  (
    cd "$WDIR"
    Worker__CoordinatorBaseUrl="$COORDINATOR_URL" \
    Worker__RegistrationToken="$REGISTRATION_TOKEN" \
    Worker__Name="worker-$i" \
      dotnet "$WORKER_DLL" >"$LOG_DIR/worker-$i.log" 2>&1
  ) &
  PIDS+=($!)
done

# --- GUI -----------------------------------------------------------------
if [[ "$NO_GUI" != "1" ]]; then
  echo "==> GUI (Desktop)  (log: $LOG_DIR/desktop.log)"
  (
    dotnet "$DESKTOP_DLL" >"$LOG_DIR/desktop.log" 2>&1
  ) &
  PIDS+=($!)
fi

cat <<EOF

==> Todo arriba.
    Coordinator: $COORDINATOR_URL  (admin / admin)
    Workers:     $WORKERS
    Logs:        $LOG_DIR/
    Tail logs:   tail -F $LOG_DIR/*.log

Pulsa Ctrl+C para detener.
EOF

wait
