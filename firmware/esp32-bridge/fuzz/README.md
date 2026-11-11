# Fuzzing the protocol parser

`../src/protocol_parse.h` is the code that reads the lines of the protocol: it splits `NAME:field:field`, reads a field as a number, checks that the fields of a pin command are numbers, and turns hex text into bytes. It has no dependency on Arduino, so the firmware (`main.cpp`) and this fuzzer use the very same code.

`fuzz_protocol.cpp` feeds it random and mutated lines. For each one it splits the line, reads every field as a number and as hex (and asks for fields past the last one, as a handler that expects more arguments does), and compares each result with a plain reference written in the same file. The buffers have exactly the size the firmware gives them, so a read or a write one byte too far is reported by AddressSanitizer; UndefinedBehaviorSanitizer reports overflows. A result that differs from the reference aborts with the input in hex.

```bash
bash run_fuzz.sh [seconds for libFuzzer = 60] [inputs of the seeded run = 2000000] [seed = 1]
```

Needs `clang` (`sudo apt install clang`). On Windows run it in WSL. With only `g++` it makes the seeded run and skips libFuzzer. The CI job `firmware-fuzz` runs it on every pull request.

* **Seeded run** (`-DCB_STANDALONE`): starts with a self-test of fixed cases (the ones that were bugs, such as `DW:abc:1` being read as GPIO0), then mutates real commands (`seeds.txt` and a list inside the file) with a generator that has a seed, so a failure can be repeated: `./build/standalone 1000000 7`.
* **libFuzzer**: keeps the inputs that reach new code. A failing input is written next to the script as `crash-*`; run it again with `./build/libfuzzer crash-...`.
* `CB_SKIP_SELFTEST=1 ./build/standalone ...` skips the fixed cases, to see what the random inputs alone find.

## What it can and cannot tell

It covers the helpers that every command goes through. It does not run the handlers themselves (`handleI2cWrite`, `handleSpiTransfer`...): those drive hardware and use Arduino classes. What it cannot see there is reviewed by reading and checked against a real board with `tests/firmware_robustness.ps1`.

To check that the fuzzer can find something, break the parser on purpose (for example make `hexToBytes` read both characters of every byte without looking at `hexLength`, or cast `strtol` to `int` without limiting it) and run it again: it must stop. Both were tried, together with a `numericFields` that accepts a lone minus sign and a `nextField` that moves one character too far, and the random inputs alone stopped each of them.
