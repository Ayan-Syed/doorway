#!/usr/bin/env bash
# Compile-checks every C# file under Assets with Unity's own compiler, without opening Unity
# (Unity locks the project, so this is the only way to check while the editor or a build is running).
# Two passes, like the real build: the player (Android, no UNITY_EDITOR), then everything with the editor scripts.
# Needs one Unity import first, for Library/ScriptAssemblies.
#   bash Tools/dev/check.sh            errors only
#   bash Tools/dev/check.sh -w         warnings too
set -u
PROJECT="$(cd "$(dirname "$0")/../.." && pwd)"
VERSION="$(sed -n 's/^m_EditorVersion: //p' "$PROJECT/ProjectSettings/ProjectVersion.txt" | tr -d '\r')"
# The editor's Data folder: $UNITY_DATA, else the usual Unity Hub install places for the project's version.
if [ -z "${UNITY_DATA:-}" ]; then
  for d in "$HOME/Unity/Hub/Editor/$VERSION/Editor/Data" \
           "/c/Program Files/Unity/Hub/Editor/$VERSION/Editor/Data" \
           "/Applications/Unity/Hub/Editor/$VERSION/Unity.app/Contents"; do
    [ -d "$d" ] && { UNITY_DATA="$d"; break; }
  done
fi
[ -n "${UNITY_DATA:-}" ] && [ -d "$UNITY_DATA" ] || { echo "Unity $VERSION not found: set UNITY_DATA to its Editor/Data folder."; exit 2; }
DOTNET="$UNITY_DATA/NetCoreRuntime/dotnet.exe"
[ -f "$DOTNET" ] || DOTNET="$UNITY_DATA/NetCoreRuntime/dotnet"
CSC="$UNITY_DATA/DotNetSdkRoslyn/csc.dll"
[ -f "$DOTNET" ] && [ -f "$CSC" ] || { echo "No compiler in $UNITY_DATA (NetCoreRuntime/dotnet, DotNetSdkRoslyn/csc.dll)."; exit 2; }
ASSEMBLIES="$PROJECT/Library/ScriptAssemblies"
SHOW_WARNINGS=0
[ "${1:-}" = "-w" ] && SHOW_WARNINGS=1

[ -d "$ASSEMBLIES" ] || { echo "No Library/ScriptAssemblies: open the project in Unity once first."; exit 2; }
# A folder of its own per run, so two checks at once don't overwrite each other's files.
mkdir -p "$PROJECT/Temp"
OUT="$(mktemp -d "$PROJECT/Temp/check.XXXXXX")"
trap 'rm -rf "$OUT"' EXIT
if command -v cygpath >/dev/null 2>&1; then win() { cygpath -w "$1"; }; else win() { printf '%s' "$1"; }; fi
short() { PREFIX="$(win "$PROJECT")\\" awk '{ p = ENVIRON["PREFIX"]; if (index($0, p) == 1) $0 = substr($0, length(p) + 1); print }'; }

DEFINES="UNITY_ANDROID;PLATFORM_ANDROID;UNITY_6000_3_OR_NEWER;UNITY_6000_0_OR_NEWER;UNITY_2023_1_OR_NEWER;UNITY_2022_3_OR_NEWER;UNITY_2021_3_OR_NEWER;ENABLE_INPUT_SYSTEM;ENABLE_LEGACY_INPUT_MANAGER;CSHARP_7_3_OR_NEWER"

# The player has neither the editor scripts nor the tests; the second pass compiles all of it, tests included.
runtime_files() { find "$PROJECT/Assets" -name '*.cs' -not -path '*/Editor/*' -not -path '*/Tests/*'; }
editor_files() { find "$PROJECT/Assets" -name '*.cs' \( -path '*/Editor/*' -o -path '*/Tests/*' \); }

# One response file per pass: references, defines and sources.
write_rsp() {  # $1 rsp, $2 defines, $3 include editor refs (0/1)
  {
    echo "-nologo -noconfig -nostdlib -target:library -langversion:9 -unsafe -deterministic"
    echo "-nowarn:0169,0649,0414,1701,1702"
    echo "-define:$2"
    echo "-r:\"$(win "$UNITY_DATA/NetStandard/ref/2.1.0/netstandard.dll")\""
    # Shims for package and editor DLLs built against .NET Framework (mscorlib), as Unity adds them
    for f in "$UNITY_DATA"/NetStandard/compat/2.1.0/shims/netfx/*.dll; do echo "-r:\"$(win "$f")\""; done
    for f in "$UNITY_DATA"/Managed/UnityEngine/UnityEngine*.dll; do echo "-r:\"$(win "$f")\""; done
    if [ "$3" = 1 ]; then
      for f in "$UNITY_DATA"/Managed/UnityEngine/UnityEditor*.dll; do echo "-r:\"$(win "$f")\""; done
      # The Android build target's editor API (UnityEditor.Android: icons, Android player settings)
      for f in "$UNITY_DATA"/PlaybackEngines/AndroidPlayer/UnityEditor.Android.Extensions.dll \
               "$UNITY_DATA"/PlaybackEngines/AndroidPlayer/Unity.Android.Types.dll \
               "$UNITY_DATA"/PlaybackEngines/AndroidPlayer/Unity.Android.Gradle.dll; do
        [ -f "$f" ] && echo "-r:\"$(win "$f")\""
      done
    fi
    for f in "$ASSEMBLIES"/*.dll; do
      case "$(basename "$f")" in
        Assembly-CSharp*|Doorway*) continue ;;   # built from the very sources compiled here
        *Editor*|*DocCodeSamples*) [ "$3" = 1 ] || continue ;;
      esac
      echo "-r:\"$(win "$f")\""
    done
    if [ "$3" = 1 ]; then   # NUnit, for the tests (from the Test Framework package)
      for f in "$PROJECT"/Library/PackageCache/com.unity.ext.nunit*/net40/unity-custom/nunit.framework.dll; do
        [ -f "$f" ] && echo "-r:\"$(win "$f")\""
      done
    fi
  } > "$1"
}

compile() {  # $1 name, $2 rsp, then source list on stdin
  local name="$1" rsp="$2" log="$OUT/$1.log"
  while IFS= read -r f; do echo "\"$(win "$f")\""; done >> "$rsp"
  echo "-out:\"$(win "$OUT/$name.dll")\"" >> "$rsp"
  "$DOTNET" "$CSC" "@$(win "$rsp")" > "$log" 2>&1
  local status=$? errors warnings
  errors=$(grep -c ': error ' "$log")
  warnings=$(grep -c ': warning ' "$log")
  grep ': error ' "$log" | sed "s#$(win "$PROJECT")\\\\##" | sort -u
  [ "$SHOW_WARNINGS" = 1 ] && grep ': warning ' "$log" | sed "s#$(win "$PROJECT")\\\\##" | sort -u
  echo "[$name] $errors errors, $warnings warnings"
  # The compiler's exit code too: it fails without printing ': error ' when it can't start or read a file.
  if [ "$status" != 0 ] && [ "$errors" = 0 ]; then
    echo "[$name] the compiler failed (exit $status):"
    tail -5 "$log"
  fi
  [ "$status" = 0 ] && [ "$errors" = 0 ]
}

n=$(runtime_files | wc -l)
[ "$n" -gt 0 ] || { echo "No C# files under Assets."; exit 0; }

write_rsp "$OUT/player.rsp" "$DEFINES" 0
runtime_files | compile player "$OUT/player.rsp"; ok1=$?

write_rsp "$OUT/editor.rsp" "$DEFINES;UNITY_EDITOR;UNITY_EDITOR_WIN" 1
{ runtime_files; editor_files; } | compile editor "$OUT/editor.rsp"; ok2=$?

[ $ok1 = 0 ] && [ $ok2 = 0 ] && echo "OK" && exit 0
exit 1
