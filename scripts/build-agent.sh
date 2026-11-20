#!/usr/bin/env bash
# ==============================================================================
# SEAM Android Agent Zero-Gradle DEX Build Script
# Compiles android-agent Java 17 sources against Android 15 (API 35) android.jar
# and packages classes.dex into dist/seam-agent.jar using Android SDK d8.
# ==============================================================================
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if [[ -z "${ANDROID_SDK_ROOT:-}" ]]; then
    for candidate in \
        "${ANDROID_HOME:-}" \
        "$HOME/Library/Android/sdk" \
        "$HOME/Android/Sdk" \
        "/usr/local/lib/android/sdk"; do
        if [[ -n "$candidate" && -d "$candidate/platforms" ]]; then
            ANDROID_SDK_ROOT="$candidate"
            break
        fi
    done
fi
ANDROID_SDK_ROOT="${ANDROID_SDK_ROOT:-$HOME/Library/Android/sdk}"

# Locate Android platform jar (prefer API 35 / Android 15)
if [[ -f "$ANDROID_SDK_ROOT/platforms/android-35/android.jar" ]]; then
    ANDROID_JAR="$ANDROID_SDK_ROOT/platforms/android-35/android.jar"
else
    ANDROID_JAR="$(find "$ANDROID_SDK_ROOT/platforms" -name "android.jar" | sort -V | tail -n 1)"
fi

# Locate d8 compiler (prefer build-tools 35.0.0)
if [[ -x "$ANDROID_SDK_ROOT/build-tools/35.0.0/d8" ]]; then
    D8_BIN="$ANDROID_SDK_ROOT/build-tools/35.0.0/d8"
else
    D8_BIN="$(find "$ANDROID_SDK_ROOT/build-tools" -name "d8" | sort -V | tail -n 1)"
fi

if [[ ! -f "$ANDROID_JAR" ]]; then
    echo "ERROR: android.jar not found under $ANDROID_SDK_ROOT/platforms" >&2
    exit 1
fi

if [[ ! -x "$D8_BIN" ]]; then
    echo "ERROR: d8 not found under $ANDROID_SDK_ROOT/build-tools" >&2
    exit 1
fi

BUILD_CLASSES_DIR="$ROOT_DIR/build/agent-classes"
DIST_DIR="$ROOT_DIR/dist"
OUT_JAR="$DIST_DIR/seam-agent.jar"

rm -rf "$BUILD_CLASSES_DIR"
mkdir -p "$BUILD_CLASSES_DIR" "$DIST_DIR"

echo "==> [1/2] Compiling SEAM Android Agent (Java 17 -> bytecode) against $(basename "$(dirname "$ANDROID_JAR")")..."
JAVA_SOURCES=()
while IFS= read -r -d '' file; do
    JAVA_SOURCES+=("$file")
done < <(find "$ROOT_DIR/android-agent/src/main/java" -name "*.java" -print0)

javac \
    --release 17 \
    -Xlint:all,-serial \
    -cp "$ANDROID_JAR" \
    -d "$BUILD_CLASSES_DIR" \
    "${JAVA_SOURCES[@]}"

echo "==> [2/2] Packaging DEX archive ($OUT_JAR) with d8..."
CLASS_FILES=()
while IFS= read -r -d '' file; do
    CLASS_FILES+=("$file")
done < <(find "$BUILD_CLASSES_DIR" -name "*.class" -print0)

"$D8_BIN" \
    --min-api 30 \
    --lib "$ANDROID_JAR" \
    --output "$OUT_JAR" \
    "${CLASS_FILES[@]}"

JAR_SIZE="$(du -h "$OUT_JAR" | awk '{print $1}')"
echo "==> Build complete: $OUT_JAR ($JAR_SIZE)"
