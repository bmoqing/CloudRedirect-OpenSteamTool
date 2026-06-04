#pragma once
#include <cstdint>
#include <atomic>

namespace MetadataSync {

extern std::atomic<bool> steamToolsPresent;
extern std::atomic<bool> syncAchievements;
extern std::atomic<bool> syncPlaytime;
extern std::atomic<bool> syncLuas;

inline bool IsEnabled() {
    return steamToolsPresent.load(std::memory_order_relaxed) &&
           (syncAchievements.load(std::memory_order_relaxed) ||
            syncPlaytime.load(std::memory_order_relaxed) ||
            syncLuas.load(std::memory_order_relaxed));
}

}
