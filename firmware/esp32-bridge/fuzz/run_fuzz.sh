#!/usr/bin/env bash
# Builds fuzz_protocol.cpp with AddressSanitizer and UndefinedBehaviorSanitizer and runs it.
#
#   ./run_fuzz.sh [seconds for libFuzzer, default 60] [inputs for the seeded run, default 2000000] [seed, default 1]
#
# Needs clang (sudo apt install clang) for libFuzzer; with only g++ it runs the seeded run, which needs no fuzzing engine.
# Exit code 0: the self-test passed, nothing violated a property and the sanitizers reported nothing.
set -euo pipefail
cd "$(dirname "$0")"

SECONDS_TO_RUN="${1:-60}"
INPUTS="${2:-2000000}"
SEED="${3:-1}"
OUT=build
mkdir -p "$OUT"

if command -v clang++ >/dev/null 2>&1; then CXX=clang++; elif command -v g++ >/dev/null 2>&1; then CXX=g++; else
  echo "No C++ compiler: sudo apt install clang" >&2
  exit 2
fi
echo "compiler: $($CXX --version | head -1)"

FLAGS="-std=c++17 -O1 -g -fno-omit-frame-pointer -Wall -Wextra"
# a report of undefined behaviour stops the run instead of printing and going on
SAN="-fsanitize=address,undefined -fno-sanitize-recover=undefined"

echo "== seeded run: $INPUTS inputs, seed $SEED"
$CXX $FLAGS $SAN -DCB_STANDALONE fuzz_protocol.cpp -o "$OUT/standalone"
"$OUT/standalone" "$INPUTS" "$SEED"

if [ "$CXX" = clang++ ] && echo 'extern "C" int LLVMFuzzerTestOneInput(const unsigned char*, unsigned long) { return 0; }' |
   clang++ -x c++ -fsanitize=fuzzer - -o "$OUT/probe" >/dev/null 2>&1; then
  echo "== libFuzzer: $SECONDS_TO_RUN s"
  $CXX $FLAGS -fsanitize=fuzzer,address,undefined -fno-sanitize-recover=undefined fuzz_protocol.cpp -o "$OUT/libfuzzer"
  mkdir -p "$OUT/corpus"
  # the lines of seeds.txt are the first corpus: one file each
  i=0
  while IFS= read -r line; do
    [ -n "$line" ] && printf '%s' "$line" > "$OUT/corpus/seed$i" && i=$((i + 1))
  done < seeds.txt
  "$OUT/libfuzzer" "$OUT/corpus" -max_total_time="$SECONDS_TO_RUN" -max_len=1024 -seed="$SEED" -print_final_stats=1
else
  echo "== libFuzzer not available (needs clang); only the seeded run was made"
fi
echo "fuzz: done"
