#!/usr/bin/env bash
# Packages a Native AOT publish folder with Velopack: an installer or portable app for people to download, and the
# full and delta packages that installed launchers update from. The release's files are copied to artifacts/.
# Usage: packaging/pack.sh <rid> <publish folder> <version> [more `vpk pack` options, e.g. for signing]
set -euo pipefail
rid="$1"
dir="$2"
version="$3"
shift 3

# The previous release's full package, which the delta package is made against. When there's none (the first Velopack
# release), this only warns.
token=()
[[ -n "${GH_TOKEN:-}" ]] && token=(--token "${GH_TOKEN}")
vpk download github --repoUrl "${GITHUB_SERVER_URL}/${GITHUB_REPOSITORY}" --channel "${rid}" --outputDir releases "${token[@]}"

# Each platform is its own channel, so its update packages don't mix with the other platforms' in a release.
options=(--packId MeowGodotLauncher --packVersion "${version}" --packDir "${dir}" --packTitle "Meow Godot Launcher"
  --packAuthors Meowcat285 --channel "${rid}" --runtime "${rid}" --outputDir releases --exclude '.*\.(pdb|dbg)$')
case "${rid}" in
  win-*)
    vpk '[win]' pack "${options[@]}" --mainExe MeowGodotLauncher.exe --icon src/MeowGodotLauncher/Assets/icon.ico "$@"
    ;;
  linux-*)
    vpk '[linux]' pack "${options[@]}" --mainExe MeowGodotLauncher --icon src/MeowGodotLauncher/Assets/icon.png "$@"
    ;;
  osx-*)
    # No .pkg installer, which would need a Developer ID Installer certificate: the portable zip holds the app.
    vpk '[osx]' pack "${options[@]}" --mainExe MeowGodotLauncher --icon packaging/macos/MeowGodotLauncher.icns \
      --bundleId com.meowcat285.meowgodotlauncher --noInst "$@"
    ;;
esac

# Only this release's files, not the previous release's package downloaded above.
mkdir -p artifacts
# tr: jq writes CRLF line endings on Windows.
files=("releases.${rid}.json" $(jq -r '.[].RelativeFileName' "releases/assets.${rid}.json" | tr -d '\r'))
[[ -f "releases/RELEASES-${rid}" ]] && files+=("RELEASES-${rid}")
for file in "${files[@]}"; do
  cp "releases/${file}" artifacts/
done
ls -l artifacts
