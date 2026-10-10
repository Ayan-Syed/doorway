#!/usr/bin/env bash
# Compile-checks every C# file under Assets with Unity's own compiler, without opening Unity
# (Unity locks the project, so this is the only way to check while the editor or a build is running).
# Two passes, like the real build: the player (Android, no UNITY_EDITOR), then everything with the editor scripts.
# Needs one Unity import first, for Library/ScriptAssemblies.
#   bash Tools/dev/check.sh            errors only
#   bash Tools/dev/check.sh -w         warnings too
set -u
PROJECT="$(cd "$(dirname "$0")/../.." && pwd)"
UNITY_DATA="${UNITY_DATA:-/c/Users/masye/Unity/Hub/Editor/6000.3.25f1/Editor/Data}"
DOTNET="$UNITY_DATA/NetCoreRuntime/dotnet.exe"
CSC="$UNITY_DATA/DotNetSdkRoslyn/csc.dll"
ASSEMBLIES="$PROJECT/Library/ScriptAssemblies"
OUT="$PROJECT/Temp/check"
SHOW_WARNINGS=0
[ "${1:-}" = "-w" ] && SHOW_WARNINGS=1

[ -d "$ASSEMBLIES" ] || { echo "No Library/ScriptAssemblies: open the project in Unity once first."; exit 2; }
mkdir -p "$OUT"
win() { cygpath -w "$1"; }
short() { PREFIX="$(win "$PROJECT")\\" awk '{ p = ENVIRON["PREFIX"]; if (index($0, p) == 1) $0 = substr($0, length(p) + 1); print }'; }

DEFINES="UNITY_ANDROID;PLATFORM_ANDROID;UNITY_6000_3_OR_NEWER;UNITY_6000_0_OR_NEWER;UNITY_2023_1_OR_NEWER;UNITY_2022_3_OR_NEWER;UNITY_2021_3_OR_NEWER;ENABLE_INPUT_SYSTEM;ENABLE_LEGACY_INPUT_MANAGER;CSHARP_7_3_OR_NEWER"

runtime_files() { find "$PROJECT/Assets" -name '*.cs' -not -path '*/Editor/*'; }
editor_files() { find "$PROJECT/Assets" -name '*.cs' -path '*/Editor/*'; }

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
        Assembly-CSharp*) continue ;;
        *Editor*|*DocCodeSamples*) [ "$3" = 1 ] || continue ;;
      esac
      echo "-r:\"$(win "$f")\""
    done
  } > "$1"
}

compile() {  # $1 name, $2 rsp, then source list on stdin
  local name="$1" rsp="$2" log="$OUT/$1.log"
  while IFS= read -r f; do echo "\"$(win "$f")\""; done >> "$rsp"
  echo "-out:\"$(win "$OUT/$name.dll")\"" >> "$rsp"
  "$DOTNET" "$CSC" "@$(win "$rsp")" > "$log" 2>&1
  local errors warnings
  errors=$(grep -c ': error ' "$log")
  warnings=$(grep -c ': warning ' "$log")
  grep ': error ' "$log" | sed "s#$(win "$PROJECT")\\\\##" | sort -u
  [ "$SHOW_WARNINGS" = 1 ] && grep ': warning ' "$log" | sed "s#$(win "$PROJECT")\\\\##" | sort -u
  echo "[$name] $errors errors, $warnings warnings"
  [ "$errors" = 0 ]
}

n=$(runtime_files | wc -l)
[ "$n" -gt 0 ] || { echo "No C# files under Assets."; exit 0; }

write_rsp "$OUT/player.rsp" "$DEFINES" 0
runtime_files | compile player "$OUT/player.rsp"; ok1=$?

write_rsp "$OUT/editor.rsp" "$DEFINES;UNITY_EDITOR;UNITY_EDITOR_WIN" 1
{ runtime_files; editor_files; } | compile editor "$OUT/editor.rsp"; ok2=$?

[ $ok1 = 0 ] && [ $ok2 = 0 ] && echo "OK" && exit 0
exit 1
