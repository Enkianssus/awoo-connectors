#pragma once

#include <cstddef>
#include <cstdint>
#include <cstring>

namespace awoo::netease {

struct CefCompatibilityProfile {
  const char* id;
  const char* display_version;
  int version[8];
  // Keep the export indices explicit: these are measured values from NetEase's
  // custom libcef, not hashes generated from the upstream build headers.
  const char* api_hash_0;
  const char* api_hash_1;
  const char* commit_hash;
  bool allow_same_api_patch;
  std::size_t delegate_browser_offset;
  std::size_t send_message_slot;
  std::size_t add_observer_slot;
  std::size_t last_host_slot;
  std::uintptr_t host_vtable_rva;
  std::uintptr_t send_message_rva;
  std::uintptr_t add_observer_rva;
};

inline constexpr CefCompatibilityProfile kCefProfiles[] = {
    {"netease-cef-91.2.2", "91.2.2+4472.169",
     {91, 2, 2, 2376, 91, 0, 4472, 169},
     "37d5f9f068cf9b5ecfb6d039fc3c5c56be3864ba",
     "306fdfb40c5dbdc34992b9a5669c199a64749d5c",
     nullptr, true, 0x10, 21, 23, 59, 0, 0, 0},
    {"netease-3.1.41.205529", "91.2.3+4472.169",
     {91, 2, 3, 2377, 91, 0, 4472, 169},
     "135912956b05b8d66775a091c5f7f17ae26eb09c",
     "0e4b5d3ff0027a5cf6e295b37d9b8b3dd2e80b7e",
     "326865300051196bd4a8e991bab2aa493c9c9a8f",
     false, 0x10, 21, 23, 59, 0x8322880, 0x34b5b90, 0x34b5f30},
};

struct CefProfileMatch {
  const CefCompatibilityProfile* profile = nullptr;
  bool exact_version = false;

  bool RequiresRuntimeProbe() const {
    return profile != nullptr
        && (!exact_version || profile->host_vtable_rva != 0);
  }
};

inline CefProfileMatch MatchCefProfile(
    const int (&version)[8],
    const char* hash_0,
    const char* hash_1,
    const char* commit_hash) {
  if (hash_0 == nullptr || hash_1 == nullptr) {
    return {};
  }
  for (const auto& profile : kCefProfiles) {
    if (std::strcmp(hash_0, profile.api_hash_0) != 0
        || std::strcmp(hash_1, profile.api_hash_1) != 0
        || (profile.commit_hash != nullptr
            && (commit_hash == nullptr
                || std::strcmp(commit_hash, profile.commit_hash) != 0))) {
      continue;
    }
    bool exact = true;
    for (std::size_t index = 0; index < 8; ++index) {
      exact = exact && version[index] == profile.version[index];
    }
    if (exact
        || (profile.allow_same_api_patch
            && version[0] == profile.version[0]
            && version[4] == profile.version[4])) {
      return {&profile, exact};
    }
  }
  return {};
}

inline bool MatchCefHostRvas(
    const CefCompatibilityProfile& profile,
    std::uintptr_t vtable,
    std::uintptr_t send_message,
    std::uintptr_t add_observer) {
  return profile.host_vtable_rva == 0
      || (vtable == profile.host_vtable_rva
          && send_message == profile.send_message_rva
          && add_observer == profile.add_observer_rva);
}

}  // namespace awoo::netease
