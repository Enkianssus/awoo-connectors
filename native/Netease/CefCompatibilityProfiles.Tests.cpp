#include "CefCompatibilityProfiles.h"

#include <cstdio>
#include <cstdlib>

namespace {
int assertions = 0;
void Check(bool value, const char* description) {
  if (!value) {
    std::fprintf(stderr, "FAIL: %s\n", description);
    std::exit(1);
  }
  ++assertions;
}
}

int main() {
  using namespace awoo::netease;
  const auto& legacy = kCefProfiles[0];
  auto old_match = MatchCefProfile(
      legacy.version, legacy.api_hash_0, legacy.api_hash_1, nullptr);
  Check(old_match.profile == &legacy && old_match.exact_version,
        "retain known 91.2.2 ABI");
  Check(!old_match.RequiresRuntimeProbe(), "retain old exact build behavior");
  int old_patch[8] = {91, 2, 2, 2378, 91, 0, 4472, 170};
  old_match = MatchCefProfile(
      old_patch, legacy.api_hash_0, legacy.api_hash_1, nullptr);
  Check(old_match.profile == &legacy && old_match.RequiresRuntimeProbe(),
        "legacy same-ABI patch still requires runtime probe");
  old_patch[4] = 92;
  Check(MatchCefProfile(old_patch, legacy.api_hash_0,
                       legacy.api_hash_1, nullptr).profile == nullptr,
        "refuse changed Chromium major with legacy hashes");

  const auto& current = kCefProfiles[1];
  const auto match = MatchCefProfile(
      current.version, current.api_hash_0, current.api_hash_1,
      current.commit_hash);
  Check(match.profile == &current && match.exact_version,
        "accept exact 3.1.41 profile");
  Check(match.RequiresRuntimeProbe(), "new profile requires callback proof");
  for (int changed = 0; changed < 8; ++changed) {
    int changed_version[8]{};
    for (int index = 0; index < 8; ++index) {
      changed_version[index] = current.version[index] + (index == changed);
    }
    Check(MatchCefProfile(changed_version, current.api_hash_0,
                         current.api_hash_1, current.commit_hash).profile
              == nullptr,
          "refuse every changed version component for pinned profile");
  }
  Check(MatchCefProfile(current.version, current.api_hash_0,
                       current.api_hash_1, "unknown").profile == nullptr,
        "refuse unknown commit even with known hashes");
  Check(MatchCefProfile(current.version, current.api_hash_0,
                       current.api_hash_1, nullptr).profile == nullptr,
        "refuse missing commit for pinned profile");
  Check(MatchCefProfile(current.version, current.api_hash_1,
                       current.api_hash_0, current.commit_hash).profile == nullptr,
        "do not swap hash export indices");
  Check(MatchCefProfile(current.version, "unknown",
                       current.api_hash_1, current.commit_hash).profile == nullptr,
        "refuse unknown first hash");
  Check(MatchCefProfile(current.version, current.api_hash_0,
                       "unknown", current.commit_hash).profile == nullptr,
        "refuse unknown second hash");
  Check(MatchCefProfile(current.version, nullptr,
                       current.api_hash_1, current.commit_hash).profile == nullptr,
        "refuse missing hash");
  Check(MatchCefHostRvas(current, 0x8322880, 0x34b5b90, 0x34b5f30),
        "accept reviewed live host and call target RVAs");
  Check(!MatchCefHostRvas(current, 0x8322888, 0x34b5b90, 0x34b5f30),
        "refuse changed host vtable");
  Check(!MatchCefHostRvas(current, 0x8322880, 0x34b5b98, 0x34b5f30),
        "refuse changed SendDevToolsMessage target");
  Check(!MatchCefHostRvas(current, 0x8322880, 0x34b5b90, 0x34b5f38),
        "refuse changed observer target");
  Check(MatchCefHostRvas(legacy, 1, 2, 3),
        "legacy profile retains executable-pointer structural validation");
  std::printf("PASS: %d CEF compatibility assertions\n", assertions);
  return 0;
}
