#pragma once
#include "cloud_provider.h"
#include "cloud_provider_base.h"

#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <unordered_set>

// WebDAV provider backed by a user-supplied HTTPS WebDAV root.
// Paths are mapped directly under the configured root:
//   {webdav_url}/{accountId}/{appId}/...

class WebDavProvider : public ICloudProvider {
public:
    const char* Name() const override { return "WebDAV"; }

    bool Init(const std::string& configPath) override;
    void Shutdown() override;
    bool IsAuthenticated() const override;

    bool Upload(const std::string& path, const uint8_t* data, size_t len) override;
    bool Download(const std::string& path, std::vector<uint8_t>& outData) override;
    bool Remove(const std::string& path) override;
    ExistsStatus CheckExists(const std::string& path) override;
    std::vector<FileInfo> List(const std::string& prefix) override;
    std::vector<std::string> ListSubfolders(const std::string& prefix) override;
    bool ListChecked(const std::string& prefix, std::vector<FileInfo>& outFiles,
                     bool* outComplete = nullptr) override;

private:
    enum class AuthMode {
        Auto,
        Basic,
        Digest,
    };

    struct DigestChallenge {
        std::string realm;
        std::string nonce;
        std::string opaque;
        std::string qop;
        std::string algorithm;
    };

    struct DavItem {
        std::string path;
        bool isCollection = false;
        uint64_t size = 0;
        uint64_t modifiedTime = 0;
    };

    bool LoadConfig(const std::string& configPath);
    bool EnsureParentCollections(const std::string& relPath);
    bool Mkcol(const std::string& relDir);
    HttpUtil::HttpResp Request(const char* method, const std::string& relPath,
                               const std::string& body = {},
                               const std::vector<std::string>& extraHeaders = {});
    HttpUtil::HttpResp RequestOnce(const char* method, const std::string& relPath,
                                   const std::string& body,
                                   const std::vector<std::string>& extraHeaders,
                                   const std::string& authHeader);

    std::string UrlForPath(const std::string& relPath) const;
    std::vector<std::string> Headers(const std::vector<std::string>& extra,
                                     const std::string& authHeader) const;
    std::string BuildDigestAuthHeader(const char* method, const std::string& relPath,
                                      const DigestChallenge& challenge);
    static AuthMode ParseAuthMode(std::string mode);
    static bool ExtractDigestChallenge(const HttpUtil::HttpResp& resp, DigestChallenge& out);

    static bool IsSafeRelativePath(const std::string& path);
    static std::string EncodePath(const std::string& path);
    static std::string NormalizeBaseUrl(std::string url);
    static std::string ExtractUrlPath(const std::string& url);
    static std::string DigestUriForUrl(const std::string& url);
    static std::string Base64Encode(const std::string& input);
    static std::string Md5Hex(const std::string& input);
    static std::string XmlDecode(std::string s);
    static std::vector<DavItem> ParsePropfind(const std::string& xml,
                                              const std::string& baseUrlPath);
    static int64_t ParseHttpDate(const std::string& value);
    bool ListRecursive(const std::string& prefix, std::vector<FileInfo>& outFiles,
                       bool* outComplete, int depth = 0);

    std::string m_baseUrl;
    std::string m_baseUrlPath;
    std::string m_username;
    std::string m_password;
    std::string m_basicAuthHeader;
    AuthMode m_authMode = AuthMode::Auto;
    DigestChallenge m_digestChallenge;
    uint32_t m_digestNonceCount = 0;
    bool m_initialized = false;
    std::unique_ptr<IHttpTransport> m_transport;
    std::unordered_set<std::string> m_createdCollections;
    std::mutex m_collectionMtx;
};
