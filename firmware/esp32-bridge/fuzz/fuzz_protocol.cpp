// Fuzzing of the code that reads the lines of the protocol (../src/protocol_parse.h), the same code the firmware runs.
//
// Two ways to build it (see run_fuzz.sh):
//   * with clang and -fsanitize=fuzzer,address,undefined: libFuzzer calls LLVMFuzzerTestOneInput and keeps the inputs that reach new code;
//   * with -DCB_STANDALONE and any compiler with -fsanitize=address,undefined: main() mutates a list of real commands with a seeded generator, so a run can be
//     repeated.
//
// Each input is used the way main.cpp uses it: split into a command and its fields, every field read as a number and as hex, and checked against a plain
// reference. The buffers are allocated with exactly the size the firmware has for them (a line ends where its text ends, a destination has exactly the bytes
// it was asked for), so a read or a write one byte too far is an error the sanitizer reports. A property that does not hold aborts with the input in hex.

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <string>
#include <vector>

#include "../src/protocol_parse.h"

[[noreturn]] static void fail(const char* what, const uint8_t* data, size_t size) {
  fprintf(stderr, "PROPERTY VIOLATED: %s\ninput (%zu bytes):", what, size);
  for (size_t i = 0; i < size && i < 256; i++) fprintf(stderr, " %02X", data[i]);
  fprintf(stderr, "%s\n", size > 256 ? " ..." : "");
  abort();
}

#define CHECK(condition, what)                       \
  do {                                               \
    if (!(condition)) fail((what), data, size);      \
  } while (0)

// What strtol(text, nullptr, 10) means, written out, then limited to the range of int.
static int referenceParseInt(const char* s) {
  while (*s == ' ' || (*s >= '\t' && *s <= '\r')) s++;
  bool negative = false;
  if (*s == '+' || *s == '-') negative = (*s++ == '-');
  long long value = 0;
  bool any = false;
  while (*s >= '0' && *s <= '9') {
    any = true;
    if (value < (1LL << 40)) value = value * 10 + (*s - '0');
    s++;
  }
  if (!any) return 0;
  if (negative) value = -value;
  if (value > INT32_MAX) return INT32_MAX;
  if (value < INT32_MIN) return INT32_MIN;
  return (int)value;
}

static int referenceNibble(char c) {
  if (c >= '0' && c <= '9') return c - '0';
  if (c >= 'A' && c <= 'F') return c - 'A' + 10;
  if (c >= 'a' && c <= 'f') return c - 'a' + 10;
  return 0;
}

// The field i of the parameters (split on ':'), or false when there are not that many.
static bool referenceField(const char* params, int index, std::string* out) {
  const char* p = params;
  for (int i = 0; i < index; i++) {
    const char* colon = strchr(p, ':');
    if (!colon) return false;
    p = colon + 1;
  }
  const char* colon = strchr(p, ':');
  out->assign(p, colon ? (size_t)(colon - p) : strlen(p));
  return true;
}

static bool referenceNumeric(const char* params, int count) {
  for (int i = 0; i < count; i++) {
    std::string field;
    if (!referenceField(params, i, &field)) return false;
    size_t k = (!field.empty() && field[0] == '-') ? 1 : 0;
    if (k >= field.size()) return false;
    for (; k < field.size(); k++)
      if (field[k] < '0' || field[k] > '9') return false;
  }
  return true;
}

static void exercise(const uint8_t* data, size_t size) {
  // The line as the firmware holds it: a C string whose buffer ends right after its terminator.
  char* line = (char*)malloc(size + 1);
  memcpy(line, data, size);
  line[size] = '\0';
  const size_t lineLength = strlen(line);   // a NUL inside the input ends the line, as it does there

  size_t nameLength = 0;
  const char* params = cbp::splitCommand(line, &nameLength);
  CHECK(nameLength <= lineLength, "the name is longer than the line");
  if (nameLength < lineLength) {
    CHECK(line[nameLength] == ':', "the name does not end at a colon");
    CHECK(params == line + nameLength + 1, "the parameters do not start after the colon");
  } else {
    CHECK(params[0] == '\0', "a line with no colon has parameters");
    CHECK(memchr(line, ':', lineLength) == nullptr, "a colon was missed");
  }

  // numericFields against a plain reading of the fields, for the counts the commands use
  for (int count = 0; count <= 6; count++)
    CHECK(cbp::numericFields(params, count) == referenceNumeric(params, count), "numericFields differs from its reference");

  // every field, and a few asks beyond the last one (a handler reads a fixed number of fields whatever the line has)
  const size_t paramsLength = strlen(params);
  const char* end = params + paramsLength;
  int fieldCount = 1;
  for (const char* c = params; *c; c++)
    if (*c == ':') fieldCount++;

  const char* ptr = params;
  for (int i = 0; i < fieldCount + 3; i++) {
    const char* before = ptr;
    cbp::Field field = cbp::nextField(ptr);
    CHECK(field.start == before, "a field does not start where the last one ended");
    CHECK(field.start + field.length <= end, "a field runs past the end of the line");
    CHECK(memchr(field.start, ':', field.length) == nullptr, "a field contains a colon");
    CHECK(ptr >= before && ptr <= end, "the cursor left the line");
    if (i < fieldCount) {
      std::string reference;
      CHECK(referenceField(params, i, &reference), "the reference has fewer fields");
      CHECK(reference.size() == field.length && memcmp(reference.data(), field.start, field.length) == 0, "a field differs from its reference");
    } else {
      CHECK(field.length == 0 && ptr == end, "asking past the last field must give an empty one");
    }

    // as a number: the firmware reads the number from the start of the field to the end of the line
    CHECK(cbp::parseInt(field.start) == referenceParseInt(field.start), "parseInt differs from its reference");

    // as hex: a copy with exactly the field's bytes and no terminator, and a destination with exactly the bytes asked for
    char* hex = (char*)malloc(field.length ? field.length : 1);
    if (field.length) memcpy(hex, field.start, field.length);
    const size_t counts[] = {0, field.length / 2, field.length / 2 + 1, field.length / 2 + 5, 1, 16};
    for (size_t count : counts) {
      uint8_t* out = (uint8_t*)malloc(count ? count : 1);
      memset(out, 0xA5, count ? count : 1);
      cbp::hexToBytes(hex, field.length, out, count);
      for (size_t k = 0; k < count; k++) {
        int high = (2 * k < field.length) ? referenceNibble(hex[2 * k]) : 0;
        int low = (2 * k + 1 < field.length) ? referenceNibble(hex[2 * k + 1]) : 0;
        CHECK(out[k] == (uint8_t)((high << 4) | low), "hexToBytes differs from its reference");
      }
      free(out);
    }
    free(hex);
  }
  free(line);
}

extern "C" int LLVMFuzzerTestOneInput(const uint8_t* data, size_t size) {
  exercise(data, size);
  return 0;
}

#ifdef CB_STANDALONE

static int failures = 0;

#define EXPECT(condition)                                                  \
  do {                                                                     \
    if (!(condition)) {                                                    \
      fprintf(stderr, "self-test failed: %s (line %d)\n", #condition, __LINE__); \
      failures++;                                                          \
    }                                                                      \
  } while (0)

// Cases that were bugs, or are meant to stay as they are
static void selfTest() {
  EXPECT(!cbp::numericFields("abc:1", 1));   // used to be read as GPIO0
  EXPECT(!cbp::numericFields("", 1));
  EXPECT(!cbp::numericFields("-:1", 1));
  EXPECT(!cbp::numericFields("5x:1", 1));
  EXPECT(cbp::numericFields("5", 1));
  EXPECT(cbp::numericFields("-5:3", 1));
  EXPECT(cbp::numericFields("2:3", 2));
  EXPECT(!cbp::numericFields("2", 2));
  EXPECT(cbp::numericFields("", 0));
  EXPECT(cbp::parseInt("99999999999999999999") == INT32_MAX);
  EXPECT(cbp::parseInt("-99999999999999999999") == INT32_MIN);
  EXPECT(cbp::parseInt("2147483647") == INT32_MAX);
  EXPECT(cbp::parseInt("-2147483648") == INT32_MIN);
  EXPECT(cbp::parseInt("abc") == 0);
  EXPECT(cbp::parseInt("") == 0);
  EXPECT(cbp::parseInt("12:34") == 12);

  const char* line = "a::b";
  const char* p = line;
  cbp::Field f1 = cbp::nextField(p), f2 = cbp::nextField(p), f3 = cbp::nextField(p), f4 = cbp::nextField(p);
  EXPECT(f1.length == 1 && f2.length == 0 && f3.length == 1 && f4.length == 0 && p == line + 4);

  uint8_t out[3] = {0xEE, 0xEE, 0xEE};
  cbp::hexToBytes("A", 1, out, 3);   // too short for the bytes asked: the rest is zero, nothing is read past the text
  EXPECT(out[0] == 0xA0 && out[1] == 0 && out[2] == 0);
  cbp::hexToBytes("zz1f", 4, out, 2);   // a stray letter counts as 0, as it always did
  EXPECT(out[0] == 0x00 && out[1] == 0x1F);

  size_t n = 0;
  EXPECT(strcmp(cbp::splitCommand("PING", &n), "") == 0 && n == 4);
  EXPECT(strcmp(cbp::splitCommand("DW:2:1", &n), "2:1") == 0 && n == 2);
  EXPECT(strcmp(cbp::splitCommand(":x", &n), "x") == 0 && n == 0);
}

// a small xorshift: the same seed gives the same run
static uint64_t rngState = 88172645463325252ULL;
static uint64_t rnd() {
  rngState ^= rngState << 13;
  rngState ^= rngState >> 7;
  rngState ^= rngState << 17;
  return rngState;
}
static size_t below(size_t n) { return n ? (size_t)(rnd() % n) : 0; }

static const char* const builtinSeeds[] = {
    "PING", "VER", "INFO", "DW:2:1", "PM:2:1", "AR:34", "PW:2:128:5000", "SV:13:90", "STI:25:26:27:14:2048", "STS:25:4:60", "NI:5:8", "NS:0:255:0:0",
    "NR:0:10:20:30:40:50:60", "IW:60:AABB", "IWR:60:0:AABB", "IRR:60:0:4", "IR:60:4", "OWR:4:0000000000000000:8", "OWW:4:28FF000000000000:AA55", "SW:5:AABBCC",
    "ST:5:0102030405", "SCFG:34:1:0:100:64:0:8", "SRD:S0:16", "WCFGX:48656C6C6F:70617373", "WCFG:ssid:pass", "MESHKEY:00112233445566778899AABBCCDDEEFF",
    "GINT:4:3", "TN:2:440:5", "LI:39:16:2", "OI:60:128:64", "MI:25:26:-1", "USR:12:14", "DHTR:4:22", "AUTH:token", "WTOK:token"};

static const char interesting[] = {':', ':', ':', '-', '+', ' ', '0', '9', 'F', 'f', 'z', '\r', '\n', '\0', (char)0xFF, (char)0x80, '\t', 'x', '.'};

static std::string mutate(std::string s) {
  const int steps = 1 + (int)below(6);
  for (int step = 0; step < steps; step++) {
    switch (below(10)) {
      case 0:   // flip a bit
        if (!s.empty()) s[below(s.size())] ^= (char)(1 << below(8));
        break;
      case 1:   // a byte that is likely to matter
        s.insert(below(s.size() + 1), 1, interesting[below(sizeof(interesting))]);
        break;
      case 2:   // delete a range
        if (!s.empty()) {
          size_t at = below(s.size());
          s.erase(at, 1 + below(s.size() - at));
        }
        break;
      case 3:   // repeat a range
        if (!s.empty()) {
          size_t at = below(s.size());
          size_t len = 1 + below(s.size() - at);
          std::string part = s.substr(at, len);
          size_t times = 1 + below(40);
          std::string many;
          for (size_t t = 0; t < times; t++) many += part;
          s.insert(at, many);
        }
        break;
      case 4:   // a very long number
        s.insert(below(s.size() + 1), std::string(1 + below(40), (char)('0' + below(10))));
        break;
      case 5:   // a run of colons
        s.insert(below(s.size() + 1), std::string(1 + below(60), ':'));
        break;
      case 6:   // cut it
        s.resize(below(s.size() + 1));
        break;
      case 7:   // a random byte
        s.insert(below(s.size() + 1), 1, (char)rnd());
        break;
      case 8:   // long hex, odd or even
        s.insert(below(s.size() + 1), std::string(1 + below(600), "0123456789ABCDEFabcdefxyz"[below(25)]));
        break;
      default:   // a minus sign or a number that does not fit in an int in front of a field
        s.insert(below(s.size() + 1), below(2) ? "-2147483649" : "4294967296");
        break;
    }
    if (s.size() > 4096) s.resize(4096);
  }
  return s;
}

int main(int argc, char** argv) {
  const long long iterations = argc > 1 ? atoll(argv[1]) : 1000000;
  if (argc > 2) rngState ^= (uint64_t)atoll(argv[2]) * 0x9E3779B97F4A7C15ULL;
  if (rngState == 0) rngState = 1;

  if (getenv("CB_SKIP_SELFTEST")) {   // to see what the random inputs alone find (used to check the fuzzer against broken copies of the parser)
    printf("self-test skipped\n");
  } else {
    selfTest();
    if (failures) return 1;
    printf("self-test passed\n");
  }

  std::vector<std::string> seeds;
  for (const char* seed : builtinSeeds) seeds.push_back(seed);
  if (argc > 3) {   // one command per line
    FILE* f = fopen(argv[3], "r");
    if (f) {
      char buffer[8192];
      while (fgets(buffer, sizeof(buffer), f)) {
        size_t n = strlen(buffer);
        while (n && (buffer[n - 1] == '\n' || buffer[n - 1] == '\r')) buffer[--n] = '\0';
        if (n) seeds.push_back(buffer);
      }
      fclose(f);
    }
  }

  for (long long i = 0; i < iterations; i++) {
    std::string input;
    if (below(20) == 0) {   // now and then, bytes with no structure at all
      input.resize(below(300));
      for (char& c : input) c = (char)rnd();
    } else {
      input = mutate(seeds[below(seeds.size())]);
      if (below(4) == 0) input = mutate(input);
    }
    exercise((const uint8_t*)input.data(), input.size());
    if ((i + 1) % 250000 == 0) printf("%lld inputs\n", i + 1);
  }
  printf("%lld inputs, no property violated and no sanitizer report\n", iterations);
  return 0;
}

#endif   // CB_STANDALONE
