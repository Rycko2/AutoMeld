#!/usr/bin/env bash

set -euo pipefail

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
cd "$script_dir"

usage() {
    cat <<'EOF'
Usage:
  ./bump-version.sh VERSION
    ./bump-version.sh --major
  ./bump-version.sh --minor
    ./bump-version.sh --patch

VERSION must be MAJOR.MINOR.PATCH, for example 0.3.0.
--major increments the major component of the version in AutoMeld.csproj and resets minor and patch to 0.
--minor increments the minor component of the version in AutoMeld.csproj and resets patch to 0.
--patch increments the patch component of the version in AutoMeld.csproj.
EOF
}

if [[ $# -ne 1 ]]; then
    usage >&2
    exit 1
fi

current_version=$(sed -nE 's/.*<Version>([0-9]+\.[0-9]+\.[0-9]+)<\/Version>.*/\1/p' AutoMeld.csproj)
if [[ -z "$current_version" ]]; then
    echo "Could not read the current version from AutoMeld.csproj." >&2
    exit 1
fi

if [[ "$1" == "--major" ]]; then
    IFS=. read -r major minor patch <<< "$current_version"
    version="$((major + 1)).0.0"
elif [[ "$1" == "--minor" ]]; then
    IFS=. read -r major minor patch <<< "$current_version"
    version="$major.$((minor + 1)).0"
elif [[ "$1" == "--patch" ]]; then
    IFS=. read -r major minor patch <<< "$current_version"
    version="$major.$minor.$((patch + 1))"
else
    version="$1"
fi

if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "Invalid version '$version'. Expected MAJOR.MINOR.PATCH." >&2
    exit 1
fi

assembly_version="${version}.0"
release_url="https://github.com/Rycko2/AutoMeld/releases/download/v${version}/latest.zip"

replace_once() {
    local file=$1
    local pattern=$2
    local replacement=$3
    local count

    count=$(grep -Eo "$pattern" "$file" | wc -l)
    if [[ "$count" -ne 1 ]]; then
        echo "Expected one match for '$pattern' in $file, found $count." >&2
        exit 1
    fi

    perl -0pi -e "s#${pattern}#${replacement}#" "$file"
}

replace_once AutoMeld.csproj '<Version>[0-9]+\.[0-9]+\.[0-9]+</Version>' "<Version>${version}</Version>"
replace_once AutoMeld.json 'DownloadLinkTesting": "[^"]+"' "DownloadLinkTesting\": \"${release_url}\""
replace_once repo.json 'AssemblyVersion": "[^"]+"' "AssemblyVersion\": \"${assembly_version}\""
replace_once repo.json 'DownloadLinkInstall": "[^"]+"' "DownloadLinkInstall\": \"${release_url}\""
replace_once repo.json 'DownloadLinkUpdate": "[^"]+"' "DownloadLinkUpdate\": \"${release_url}\""

python3 -m json.tool AutoMeld.json >/dev/null
python3 -m json.tool repo.json >/dev/null

echo "Updated plugin version from ${current_version} to ${version}."
echo "Updated release metadata to ${release_url}."
git --no-pager diff -- AutoMeld.csproj AutoMeld.json repo.json
