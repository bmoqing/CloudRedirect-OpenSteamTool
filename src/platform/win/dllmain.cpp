#include "common.h"
#include "log.h"
#include "cloud_intercept.h"
#include "metadata_sync.h"
#include "file_util.h"
#include "cli.h"
#include "steam_kv_injector.h"
#include <atomic>
#include <exception>
#include <mutex>

static HMODULE g_thisModule = nullptr;
static std::once_flag g_initFlag;
static std::once_flag g_directInitFlag;
static std::once_flag g_commonInitFlag;
static std::atomic<bool> g_commonInitFailed{false};

// Steam dir from the DLL's own location, UTF-8.
// All "narrow" std::string paths in the DLL are UTF-8; ACP narrowing here
// would corrupt every non-ASCII Steam install.
static std::string GetSteamPath() {
    wchar_t wdllPath[MAX_PATH];
    DWORD n = GetModuleFileNameW(g_thisModule, wdllPath, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return {};

    // Trim to parent on wide data so we don't split a multi-byte sequence.
    DWORD endIdx = n;
    for (DWORD i = n; i > 0; --i) {
        if (wdllPath[i - 1] == L'\\') { endIdx = i; break; }
    }

    // WideToUtf8 rejects ill-formed UTF-16 (init then logs+skips).
    return FileUtil::WideToUtf8(wdllPath, (size_t)endIdx);
}

static void InitializeCommonOnce(const char* source) {
    std::call_once(g_commonInitFlag, [&]() {
        try {
            std::string steamPath = GetSteamPath();
            std::string logPath = steamPath + "cloud_redirect.log";

            Log::Init(logPath.c_str());
            MetadataSync::steamToolsPresent.store(true, std::memory_order_relaxed);
            LOG("CloudRedirect loaded via %s, PID=%u", source, GetCurrentProcessId());
#ifdef CR_CUSTOM_WEB_DAV_BUILD
            LOG("[Build] Custom OpenSteamTool/WebDAV build active; official DLL auto-update is disabled");
#else
            LOG("[Build] Official CloudRedirect build active");
#endif
            LOG("Steam path: %s", steamPath.c_str());

            HMODULE hSteamClient = GetModuleHandleA("steamclient64.dll");
            LOG("steamclient64.dll base: %p", hSteamClient);

            CloudIntercept::Init(steamPath);
        } catch (const std::exception& ex) {
            LOG("CloudRedirect common init FAILED via %s: %s", source, ex.what());
            g_commonInitFailed.store(true, std::memory_order_relaxed);
        } catch (...) {
            LOG("CloudRedirect common init FAILED via %s: unknown exception", source);
            g_commonInitFailed.store(true, std::memory_order_relaxed);
        }
    });
}

// Entry point from the SteamTools payload code cave.
// Returns nonzero if we handled the packet; zero lets Steam's SendPkt run.
extern "C" __declspec(dllexport)
int CloudOnSendPkt(void* thisptr, const uint8_t* data, uint32_t size, void* recvPktFn) {
    // One-time init; init failure -> return 0 (let Steam handle).
    static std::atomic<bool> g_initFailed{false};
    std::call_once(g_initFlag, [&]() {
        try {
            InitializeCommonOnce("SteamTools code cave");
            if (g_commonInitFailed.load(std::memory_order_relaxed)) {
                g_initFailed.store(true, std::memory_order_relaxed);
                return;
            }

            if (recvPktFn) {
                CloudIntercept::SetSendPktAddr(recvPktFn);

                // recvPktFn = RecvPkt slot (RVA 0x1CAB48); saved orig at RVA 0x1CAB20.
                uintptr_t recvPktGlobal = (uintptr_t)recvPktFn;
                uintptr_t payloadBase = recvPktGlobal - 0x1CAB48;
                uintptr_t savedOrigAddr = payloadBase + 0x1CAB20;
                CloudIntercept::InstallRecvPktMonitor((void*)savedOrigAddr);
            }

            CloudIntercept::InstallManifestPinHook();

            CloudIntercept::InstallReleaseStateNop();

            LOG("CloudRedirect fully initialized with hooks");
        } catch (const std::exception& ex) {
            LOG("CloudRedirect init FAILED: %s", ex.what());
            g_initFailed.store(true, std::memory_order_relaxed);
        } catch (...) {
            LOG("CloudRedirect init FAILED: unknown exception");
            g_initFailed.store(true, std::memory_order_relaxed);
        }
    });

    if (g_initFailed.load(std::memory_order_relaxed)) return 0;
    return CloudIntercept::OnSendPkt(thisptr, data, size) ? 1 : 0;
}

// Lua entry point for OpenSteamTool:
// package.loadlib("cloud_redirect.dll", "CloudRedirectLua")()
extern "C" __declspec(dllexport)
int CloudRedirectLua(void* /*luaState*/) {
    static std::atomic<bool> g_directInitFailed{false};
    std::call_once(g_directInitFlag, [&]() {
        try {
            InitializeCommonOnce("OpenSteamTool Lua");
            if (g_commonInitFailed.load(std::memory_order_relaxed)) {
                g_directInitFailed.store(true, std::memory_order_relaxed);
                return;
            }
            SteamKvInjector::Init();

            HANDLE h = CreateThread(nullptr, 0, [](LPVOID) -> DWORD {
                CloudIntercept::InstallDirectHooks();
                LOG("CloudRedirect OpenSteamTool direct initialization complete");
                return 0;
            }, nullptr, 0, nullptr);
            if (h) CloseHandle(h);
            else LOG("CloudRedirect failed to start direct hook thread (%u)", GetLastError());
        } catch (const std::exception& ex) {
            LOG("CloudRedirect OpenSteamTool init FAILED: %s", ex.what());
            g_directInitFailed.store(true, std::memory_order_relaxed);
        } catch (...) {
            LOG("CloudRedirect OpenSteamTool init FAILED: unknown exception");
            g_directInitFailed.store(true, std::memory_order_relaxed);
        }
    });

    return 0;
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID reserved) {
    switch (reason) {
    case DLL_PROCESS_ATTACH:
        g_thisModule = hModule;
        DisableThreadLibraryCalls(hModule);

        // Pin against FreeLibrary so hook threads survive.
        {
            HMODULE pinned = nullptr;
            GetModuleHandleExA(
                GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
                reinterpret_cast<LPCSTR>(&DllMain),
                &pinned);
        }
        break;

    case DLL_PROCESS_DETACH:
        // FreeLibrary path only (we're pinned, so unreachable today). ExitProcess
        // path runs from an atexit hook installed in CloudIntercept::Init.
        if (reserved == nullptr) {
            CloudIntercept::Shutdown();
        }
        break;
    }
    return TRUE;
}
