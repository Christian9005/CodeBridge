/*
 * Reading the lines of the protocol: the part of the firmware that handles bytes someone else chose.
 *
 * It depends on nothing of Arduino, so the same code is built on a PC and run under AddressSanitizer and UndefinedBehaviorSanitizer with random and mutated
 * input (see ../fuzz). main.cpp uses it; there is no second copy.
 *
 * Rules every function here keeps, whatever the input: it never reads before the start of a string or after its terminator (or after the length it is
 * given), never writes outside the buffer it is given, and does no arithmetic that overflows.
 */
#pragma once

#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

namespace cbp {

// A whole number, or 0 for an empty field or one that does not start with a number (numericFields tells those apart where it matters). A number out of the
// range of int saturates: the same result atoi gave on this chip, written down instead of left undefined.
inline int parseInt(const char* text) {
  long v = strtol(text, nullptr, 10);
  if (v > INT32_MAX) return INT32_MAX;
  if (v < INT32_MIN) return INT32_MIN;
  return (int)v;
}

// For a command that drives a pin, a field that is empty or not a number would be read as 0, which is GPIO0, a pin the chip reads when it starts: "DW" or
// "DW:abc:1" would pull it low. Such a command requires its first `count` fields to be whole numbers (an optional minus sign and digits).
inline bool numericFields(const char* params, int count) {
  const char* p = params;
  for (int i = 0; i < count; i++) {
    const char* q = p;
    if (*q == '-') q++;
    if (*q < '0' || *q > '9') return false;
    while (*q >= '0' && *q <= '9') q++;
    if (i == count - 1) return *q == ':' || *q == '\0';
    if (*q != ':') return false;
    p = q + 1;
  }
  return true;
}

// The field that starts at `ptr`, up to the next ':' or the end of the line. `ptr` moves to the start of the next field (the end of the line when this was
// the last one), so asking again after the last field gives an empty one.
struct Field {
  const char* start;
  unsigned int length;
};

inline Field nextField(const char*& ptr) {
  const char* next = strchr(ptr, ':');
  Field field = {ptr, next ? (unsigned int)(next - ptr) : (unsigned int)strlen(ptr)};
  ptr += next ? field.length + 1 : field.length;
  return field;
}

// A hex digit as a number; anything else is 0 (a line with a stray letter in its hex has always been accepted and read that way).
inline uint8_t hexNibble(char c) {
  if (c >= '0' && c <= '9') return (uint8_t)(c - '0');
  if (c >= 'A' && c <= 'F') return (uint8_t)(c - 'A' + 10);
  if (c >= 'a' && c <= 'f') return (uint8_t)(c - 'a' + 10);
  return 0;
}

// `count` bytes from hex text of `hexLength` characters. It never reads past `hexLength`: a text too short for `count` bytes leaves the rest of them 0,
// whatever the caller checked (or forgot to check) before.
inline void hexToBytes(const char* hex, size_t hexLength, uint8_t* out, size_t count) {
  for (size_t i = 0; i < count; i++) {
    uint8_t high = (2 * i < hexLength) ? hexNibble(hex[2 * i]) : 0;
    uint8_t low = (2 * i + 1 < hexLength) ? hexNibble(hex[2 * i + 1]) : 0;
    out[i] = (uint8_t)((high << 4) | low);
  }
}

// "NAME:params": the length of NAME and where the parameters start (an empty string when the line has no ':').
inline const char* splitCommand(const char* line, size_t* nameLength) {
  const char* separator = strchr(line, ':');
  *nameLength = separator ? (size_t)(separator - line) : strlen(line);
  return separator ? separator + 1 : "";
}

}  // namespace cbp
