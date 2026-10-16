#!/usr/bin/env bash
# PojavLauncher APK'sındaki jre-21 bileşenlerini Android projesinin assets
# dizinine çıkarır. Böylece JRE tek APK içinde gömülü gelir (ilk açılışta
# uygulama bunları kendi veri dizinine açar — JreKurucu).
#
#   Kullanım: tools/jre-hazirla.sh [PojavLauncher.apk]
set -euo pipefail

KOK="$(cd "$(dirname "$0")/.." && pwd)"
HEDEF="$KOK/Bridger.Android/Assets/components/jre21"
APK="${1:-/tmp/opencode/jre/PojavLauncher.apk}"

if [[ ! -f "$APK" ]]; then
  cat >&2 <<'EOF'
APK bulunamadı. İndir:
  curl -L -o PojavLauncher.apk \
    https://github.com/PojavLauncherTeam/PojavLauncher/releases/download/gladiolus/PojavLauncher.apk
Sonra: tools/jre-hazirla.sh PojavLauncher.apk
EOF
  exit 1
fi

mkdir -p "$HEDEF"
for f in universal.tar.xz bin-arm64.tar.xz bin-x86_64.tar.xz; do
  unzip -o -j "$APK" "assets/components/jre-21/$f" -d "$HEDEF"
done
echo "Hazır: $HEDEF"
ls -la "$HEDEF"
