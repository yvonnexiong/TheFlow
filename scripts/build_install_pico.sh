#!/bin/zsh
set -euo pipefail

script_dir="${0:A:h}"
repo_dir="${script_dir:h}"
project_dir="$repo_dir/WayfinderUnity"
unity_bin="/Applications/Unity/Hub/Editor/6000.3.17f1/Unity.app/Contents/MacOS/Unity"
adb_bin="/Applications/Unity/Hub/Editor/6000.3.17f1/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb"
apk_path="$project_dir/Builds/PICO/Wayfinder-PICO.apk"
package_id="com.wayfinder.patience"

if [[ ! -x "$adb_bin" ]]; then
  print -u2 "Android tools are missing. Install Android Build Support in Unity Hub."
  exit 2
fi

if [[ "${1:-}" != "--install-only" ]]; then
  if [[ ! -x "$unity_bin" ]]; then
    print -u2 "Required Unity editor is missing: $unity_bin"
    exit 2
  fi
  "$unity_bin" -batchmode -quit -projectPath "$project_dir" \
    -executeMethod WayfinderPicoBuilder.BuildPicoApk \
    -logFile /tmp/wayfinder-pico-build.log
fi

if [[ ! -f "$apk_path" ]]; then
  print -u2 "APK not found: $apk_path"
  exit 3
fi

device_count="$($adb_bin devices | awk 'NR>1 && $2 == "device" {count++} END {print count+0}')"
if [[ "$device_count" -ne 1 ]]; then
  print -u2 "Expected exactly one authorized PICO device; found $device_count."
  print -u2 "Connect by USB, enable Developer Mode and USB debugging, then accept the headset authorization prompt."
  "$adb_bin" devices -l
  exit 4
fi

"$adb_bin" install -r "$apk_path"
"$adb_bin" reverse tcp:8787 tcp:8787 >/dev/null 2>&1 || true
"$adb_bin" shell am force-stop "$package_id"
"$adb_bin" shell monkey -p "$package_id" -c android.intent.category.LAUNCHER 1
print "Wayfinder installed and launched on PICO."
