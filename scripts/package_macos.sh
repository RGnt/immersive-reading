#!/usr/bin/env bash
# ==============================================================================
#   Immersive Reading — Standalone Release Packager for macOS (Apple Silicon ARM64)
# ==============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
PROJECT_FILE="${REPO_ROOT}/immersive-reader/src/ImmersiveReading.App/ImmersiveReading.App.csproj"
CLIENT_DIR="${REPO_ROOT}/immersive-reader/src/ImmersiveReading.App/client"
CONFIGURATION="${1:-Release}"
RUNTIME="${2:-osx-arm64}"
DIST_DIR="${REPO_ROOT}/dist/ImmersiveReading-${RUNTIME}"
ARCHIVE_FILE="${REPO_ROOT}/dist/ImmersiveReading-${RUNTIME}.tar.gz"

echo "=========================================================="
echo "  Immersive Reading - Standalone Release Packager (${RUNTIME})"
echo "=========================================================="

# 1. Clean previous builds
if [ -d "${DIST_DIR}" ]; then
  echo "[1/4] Cleaning existing build directory: ${DIST_DIR}..."
  rm -rf "${DIST_DIR}"
fi
rm -f "${ARCHIVE_FILE}"
mkdir -p "${DIST_DIR}"

# 2. Build React frontend assets
echo "[2/4] Building React frontend with Vite..."
(cd "${CLIENT_DIR}" && npm ci && npm run build)

# 3. Publish self-contained binary
echo "[3/4] Publishing self-contained ${CONFIGURATION} binary (${RUNTIME})..."
dotnet publish "${PROJECT_FILE}" \
  -c "${CONFIGURATION}" \
  -r "${RUNTIME}" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:BuildReactClient=false \
  -o "${DIST_DIR}"

# Clean symbols
rm -f "${DIST_DIR}"/*.pdb

# Add 1-click launcher and set executable permissions
chmod +x "${DIST_DIR}/ImmersiveReading.App"
cat << 'EOF' > "${DIST_DIR}/START-READER.command"
#!/bin/bash
cd "$(dirname "$0")"
exec ./ImmersiveReading.App "$@"
EOF
chmod +x "${DIST_DIR}/START-READER.command"

# 4. Create tar.gz archive
echo "[4/4] Creating tar.gz release package..."
mkdir -p "$(dirname "${ARCHIVE_FILE}")"
tar -czvf "${ARCHIVE_FILE}" -C "$(dirname "${DIST_DIR}")" "ImmersiveReading-${RUNTIME}"

echo "=========================================================="
echo "  Build & Packaging Complete!"
echo "  Archive: ${ARCHIVE_FILE}"
echo "=========================================================="

