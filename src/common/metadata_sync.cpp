#include "metadata_sync.h"

namespace MetadataSync {

std::atomic<bool> steamToolsPresent{false};
std::atomic<bool> syncAchievements{false};
std::atomic<bool> syncPlaytime{false};
std::atomic<bool> syncLuas{false};

}
