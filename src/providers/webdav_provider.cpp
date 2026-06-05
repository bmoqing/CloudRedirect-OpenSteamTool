#include "webdav_provider.h"
#include "file_util.h"
#include "http_util.h"
#include "json.h"
#include "log.h"

#include <algorithm>
#include <cctype>
#include <cstring>
#include <ctime>
#include <fstream>
#include <iomanip>
#include <locale>
#include <random>
#include <sstream>

#ifdef _WIN32
#include <Windows.h>
#include <wincrypt.h>
#endif

using HttpUtil::HttpResp;
using HttpUtil::UrlDecode;
using HttpUtil::UrlEncode;

namespace {

std::string Trim(const std::string& s) {
    size_t begin = 0;
    while (begin < s.size() && std::isspace(static_cast<unsigned char>(s[begin]))) ++begin;
    size_t end = s.size();
    while (end > begin && std::isspace(static_cast<unsigned char>(s[end - 1]))) --end;
    return s.substr(begin, end - begin);
}

std::string ToLower(std::string s) {
    for (auto& c : s) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return s;
}

bool StartsWithInsensitive(const std::string& s, const std::string& prefix) {
    if (s.size() < prefix.size()) return false;
    for (size_t i = 0; i < prefix.size(); ++i) {
        if (std::tolower(static_cast<unsigned char>(s[i])) !=
            std::tolower(static_cast<unsigned char>(prefix[i]))) {
            return false;
        }
    }
    return true;
}

bool ContainsInsensitive(const std::string& s, const std::string& needle) {
    return ToLower(s).find(ToLower(needle)) != std::string::npos;
}

std::string StripQuotes(std::string value) {
    value = Trim(value);
    if (value.size() >= 2 && value.front() == '"' && value.back() == '"')
        return value.substr(1, value.size() - 2);
    return value;
}

std::unordered_map<std::string, std::string> ParseAuthParams(const std::string& input) {
    std::unordered_map<std::string, std::string> params;
    size_t pos = 0;
    while (pos < input.size()) {
        while (pos < input.size() && (std::isspace(static_cast<unsigned char>(input[pos])) || input[pos] == ','))
            ++pos;
        size_t keyStart = pos;
        while (pos < input.size() && input[pos] != '=' && input[pos] != ',') ++pos;
        if (pos >= input.size() || input[pos] != '=') break;
        std::string key = ToLower(Trim(input.substr(keyStart, pos - keyStart)));
        ++pos;

        std::string value;
        if (pos < input.size() && input[pos] == '"') {
            ++pos;
            while (pos < input.size()) {
                char c = input[pos++];
                if (c == '\\' && pos < input.size()) {
                    value.push_back(input[pos++]);
                } else if (c == '"') {
                    break;
                } else {
                    value.push_back(c);
                }
            }
        } else {
            size_t valueStart = pos;
            while (pos < input.size() && input[pos] != ',') ++pos;
            value = Trim(input.substr(valueStart, pos - valueStart));
        }
        if (!key.empty()) params[key] = value;
    }
    return params;
}

std::string RandomClientNonce() {
    unsigned char bytes[8] = {};
#ifdef _WIN32
    HCRYPTPROV hProv = 0;
    if (CryptAcquireContextW(&hProv, nullptr, nullptr, PROV_RSA_FULL, CRYPT_VERIFYCONTEXT)) {
        CryptGenRandom(hProv, sizeof(bytes), bytes);
        CryptReleaseContext(hProv, 0);
    } else
#endif
    {
        std::random_device rd;
        for (auto& b : bytes) b = static_cast<unsigned char>(rd());
    }

    static constexpr char hex[] = "0123456789abcdef";
    std::string out;
    out.reserve(sizeof(bytes) * 2);
    for (unsigned char b : bytes) {
        out.push_back(hex[b >> 4]);
        out.push_back(hex[b & 0x0F]);
    }
    return out;
}

std::string ExtractElementBody(const std::string& block, const char* localName) {
    std::string needle = ":";
    needle += localName;
    size_t tag = block.find(needle);
    if (tag == std::string::npos) {
        needle = "<";
        needle += localName;
        tag = block.find(needle);
    }
    while (tag != std::string::npos) {
        size_t openStart = block.rfind('<', tag);
        size_t openEnd = block.find('>', tag);
        if (openStart == std::string::npos || openEnd == std::string::npos) return {};
        bool closing = openStart + 1 < block.size() && block[openStart + 1] == '/';
        if (closing) {
            tag = block.find(needle, openEnd + 1);
            continue;
        }

        size_t nameStart = openStart + 1;
        while (nameStart < openEnd && std::isspace(static_cast<unsigned char>(block[nameStart])))
            ++nameStart;
        size_t nameEnd = nameStart;
        while (nameEnd < openEnd &&
               !std::isspace(static_cast<unsigned char>(block[nameEnd])) &&
               block[nameEnd] != '>') {
            ++nameEnd;
        }
        std::string tagName = block.substr(nameStart, nameEnd - nameStart);

        std::string closeTag = "</" + tagName + ">";
        size_t closeStart = block.find(closeTag, openEnd + 1);
        if (closeStart == std::string::npos) return {};
        return block.substr(openEnd + 1, closeStart - openEnd - 1);
    }
    return {};
}

bool BlockHasCollectionResourceType(const std::string& block) {
    auto type = ToLower(block);
    return type.find(":resourcetype") != std::string::npos &&
           (type.find(":collection") != std::string::npos ||
            type.find("<collection") != std::string::npos);
}

} // namespace

bool WebDavProvider::Init(const std::string& configPath) {
    if (m_initialized) return true;
    if (!LoadConfig(configPath)) return false;

    m_transport = CreateHttpTransport("[WebDAV]");
    if (!m_transport || !m_transport->Init()) {
        LOG("[WebDAV] Transport init failed");
        return false;
    }

    m_basicAuthHeader = "Authorization: Basic " + Base64Encode(m_username + ":" + m_password);
    m_initialized = true;
    LOG("[WebDAV] Initialized at %s", m_baseUrl.c_str());
    return true;
}

void WebDavProvider::Shutdown() {
    if (m_transport) m_transport->Shutdown();
    m_initialized = false;
}

bool WebDavProvider::IsAuthenticated() const {
    return m_initialized && !m_baseUrl.empty() && !m_username.empty();
}

bool WebDavProvider::LoadConfig(const std::string& configPath) {
    std::ifstream f(FileUtil::Utf8ToPath(configPath), std::ios::binary);
    if (!f) {
        LOG("[WebDAV] Cannot read config: %s", configPath.c_str());
        return false;
    }
    std::string raw((std::istreambuf_iterator<char>(f)), {});
    auto cfg = Json::Parse(raw);

    m_baseUrl = NormalizeBaseUrl(cfg["webdav_url"].str());
    m_username = cfg["webdav_username"].str();
    m_password = cfg["webdav_password"].str();
    m_authMode = ParseAuthMode(cfg["webdav_auth_mode"].str());
    m_baseUrlPath = ExtractUrlPath(m_baseUrl);

    if (m_baseUrl.empty() || !StartsWithInsensitive(m_baseUrl, "https://")) {
        LOG("[WebDAV] webdav_url must be an HTTPS URL");
        return false;
    }
    if (m_baseUrl.find_first_of("?#") != std::string::npos) {
        LOG("[WebDAV] webdav_url must not include a query string or fragment");
        return false;
    }
    if (m_username.empty() || m_password.empty()) {
        LOG("[WebDAV] Username/password are required");
        return false;
    }
    return true;
}

WebDavProvider::AuthMode WebDavProvider::ParseAuthMode(std::string mode) {
    mode = ToLower(Trim(mode));
    if (mode == "basic") return AuthMode::Basic;
    if (mode == "digest") return AuthMode::Digest;
    return AuthMode::Auto;
}

std::string WebDavProvider::NormalizeBaseUrl(std::string url) {
    url = Trim(url);
    while (!url.empty() && url.back() == '/') url.pop_back();
    return url;
}

std::string WebDavProvider::ExtractUrlPath(const std::string& url) {
    size_t schemeEnd = url.find("://");
    if (schemeEnd == std::string::npos) return "/";
    size_t hostStart = schemeEnd + 3;
    size_t pathStart = url.find('/', hostStart);
    if (pathStart == std::string::npos) return "/";
    auto path = url.substr(pathStart);
    size_t query = path.find_first_of("?#");
    if (query != std::string::npos) path.resize(query);
    while (path.size() > 1 && path.back() == '/') path.pop_back();
    return path.empty() ? "/" : UrlDecode(path);
}

std::string WebDavProvider::DigestUriForUrl(const std::string& url) {
    size_t schemeEnd = url.find("://");
    if (schemeEnd == std::string::npos) return "/";
    size_t hostStart = schemeEnd + 3;
    size_t pathStart = url.find('/', hostStart);
    if (pathStart == std::string::npos) return "/";
    std::string path = url.substr(pathStart);
    if (path.empty()) path = "/";
    return path;
}

bool WebDavProvider::IsSafeRelativePath(const std::string& path) {
    if (path.empty() || path[0] == '/') return false;
    size_t start = 0;
    while (start < path.size()) {
        size_t slash = path.find('/', start);
        std::string seg = (slash == std::string::npos)
            ? path.substr(start)
            : path.substr(start, slash - start);
        if (seg.empty() || seg == "." || seg == "..") return false;
        if (seg.find('\\') != std::string::npos) return false;
        if (slash == std::string::npos) break;
        start = slash + 1;
    }
    return true;
}

std::string WebDavProvider::EncodePath(const std::string& path) {
    std::string out;
    size_t start = 0;
    while (start < path.size()) {
        size_t slash = path.find('/', start);
        std::string seg = (slash == std::string::npos)
            ? path.substr(start)
            : path.substr(start, slash - start);
        if (!seg.empty()) out += UrlEncode(seg);
        if (slash == std::string::npos) break;
        out += '/';
        start = slash + 1;
    }
    return out;
}

std::string WebDavProvider::UrlForPath(const std::string& relPath) const {
    if (relPath.empty()) return m_baseUrl;
    return m_baseUrl + "/" + EncodePath(relPath);
}

std::vector<std::string> WebDavProvider::Headers(const std::vector<std::string>& extra,
                                                 const std::string& authHeader) const {
    std::vector<std::string> headers;
    headers.reserve(extra.size() + 2);
    if (!authHeader.empty()) headers.push_back(authHeader);
    headers.push_back("Accept: */*");
    for (const auto& h : extra) headers.push_back(h);
    return headers;
}

HttpResp WebDavProvider::RequestOnce(const char* method, const std::string& relPath,
                                     const std::string& body,
                                     const std::vector<std::string>& extraHeaders,
                                     const std::string& authHeader) {
    if (!m_transport || !m_transport->IsReady()) return {};
    return m_transport->RequestUrl(method, UrlForPath(relPath), body, Headers(extraHeaders, authHeader));
}

HttpResp WebDavProvider::Request(const char* method, const std::string& relPath,
                                 const std::string& body,
                                 const std::vector<std::string>& extraHeaders) {
    if (!m_transport || !m_transport->IsReady()) return {};

    if (m_authMode == AuthMode::Digest && !m_digestChallenge.nonce.empty()) {
        return RequestOnce(method, relPath, body, extraHeaders,
            BuildDigestAuthHeader(method, relPath, m_digestChallenge));
    }

    HttpResp r = RequestOnce(method, relPath, body, extraHeaders,
        m_authMode == AuthMode::Digest ? std::string() : m_basicAuthHeader);

    if (r.status == 401 && m_authMode != AuthMode::Basic) {
        DigestChallenge challenge;
        if (ExtractDigestChallenge(r, challenge)) {
            m_digestChallenge = std::move(challenge);
            LOG("[WebDAV] Retrying %s with Digest authentication", method);
            return RequestOnce(method, relPath, body, extraHeaders,
                BuildDigestAuthHeader(method, relPath, m_digestChallenge));
        }
    }

    return r;
}

bool WebDavProvider::Mkcol(const std::string& relDir) {
    if (relDir.empty()) return true;
    {
        std::lock_guard<std::mutex> lock(m_collectionMtx);
        if (m_createdCollections.count(relDir)) return true;
    }
    auto r = Request("MKCOL", relDir);
    if (r.status == 201 || r.status == 405 || r.status == 301 || r.status == 302) {
        std::lock_guard<std::mutex> lock(m_collectionMtx);
        m_createdCollections.insert(relDir);
        return true;
    }
    if (r.status == 409) return false;
    auto exists = CheckExists(relDir);
    if (exists == ExistsStatus::Exists) {
        std::lock_guard<std::mutex> lock(m_collectionMtx);
        m_createdCollections.insert(relDir);
        return true;
    }
    LOG("[WebDAV] MKCOL failed for %s: HTTP %d", relDir.c_str(), r.status);
    return false;
}

bool WebDavProvider::EnsureParentCollections(const std::string& relPath) {
    size_t pos = 0;
    while (true) {
        pos = relPath.find('/', pos);
        if (pos == std::string::npos) break;
        std::string dir = relPath.substr(0, pos);
        if (!dir.empty() && !Mkcol(dir)) return false;
        ++pos;
    }
    return true;
}

bool WebDavProvider::Upload(const std::string& path, const uint8_t* data, size_t len) {
    if (!IsSafeRelativePath(path)) {
        LOG("[WebDAV] Upload: bad path '%s'", path.c_str());
        return false;
    }
    if (!EnsureParentCollections(path)) return false;

    std::string body;
    if (data && len > 0) body.assign(reinterpret_cast<const char*>(data), len);
    auto r = Request("PUT", path, body, {"Content-Type: application/octet-stream"});
    bool ok = r.status >= 200 && r.status < 300;
    if (!ok) LOG("[WebDAV] Upload failed for %s: HTTP %d", path.c_str(), r.status);
    return ok;
}

bool WebDavProvider::Download(const std::string& path, std::vector<uint8_t>& outData) {
    if (!IsSafeRelativePath(path)) {
        LOG("[WebDAV] Download: bad path '%s'", path.c_str());
        return false;
    }
    auto r = Request("GET", path);
    if (r.status != 200) {
        LOG("[WebDAV] Download failed for %s: HTTP %d", path.c_str(), r.status);
        return false;
    }
    outData.assign(r.body.begin(), r.body.end());
    return true;
}

bool WebDavProvider::Remove(const std::string& path) {
    if (!IsSafeRelativePath(path)) {
        LOG("[WebDAV] Remove: bad path '%s'", path.c_str());
        return false;
    }
    auto r = Request("DELETE", path);
    if (r.status == 404 || r.status == 410) return true;
    bool ok = r.status >= 200 && r.status < 300;
    if (!ok) LOG("[WebDAV] Delete failed for %s: HTTP %d", path.c_str(), r.status);
    return ok;
}

ICloudProvider::ExistsStatus WebDavProvider::CheckExists(const std::string& path) {
    if (!IsSafeRelativePath(path)) return ExistsStatus::Error;

    auto r = Request("HEAD", path);
    if (r.status >= 200 && r.status < 300) return ExistsStatus::Exists;
    if (r.status == 404 || r.status == 410) return ExistsStatus::Missing;

    std::string body =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
        "<d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/></d:prop></d:propfind>";
    r = Request("PROPFIND", path, body, {"Depth: 0", "Content-Type: application/xml; charset=utf-8"});
    if (r.status == 207 || (r.status >= 200 && r.status < 300)) return ExistsStatus::Exists;
    if (r.status == 404 || r.status == 410) return ExistsStatus::Missing;
    return ExistsStatus::Error;
}

std::vector<ICloudProvider::FileInfo> WebDavProvider::List(const std::string& prefix) {
    std::vector<FileInfo> result;
    ListChecked(prefix, result);
    return result;
}

bool WebDavProvider::ListChecked(const std::string& prefix, std::vector<FileInfo>& result,
                                 bool* outComplete) {
    result.clear();
    if (outComplete) *outComplete = false;
    if (!IsSafeRelativePath(prefix)) return false;

    bool complete = true;
    bool ok = ListRecursive(prefix, result, &complete);
    if (outComplete) *outComplete = complete;
    return ok;
}

bool WebDavProvider::ListRecursive(const std::string& prefix, std::vector<FileInfo>& result,
                                   bool* outComplete, int depth) {
    static constexpr int MAX_DEPTH = 32;
    if (depth >= MAX_DEPTH) {
        if (outComplete) *outComplete = false;
        LOG("[WebDAV] ListRecursive depth limit reached at %s", prefix.c_str());
        return true;
    }

    std::string body =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
        "<d:propfind xmlns:d=\"DAV:\"><d:prop>"
        "<d:resourcetype/><d:getcontentlength/><d:getlastmodified/>"
        "</d:prop></d:propfind>";

    auto r = Request("PROPFIND", prefix, body, {"Depth: 1", "Content-Type: application/xml; charset=utf-8"});
    if (r.status == 404 || r.status == 410) {
        return true;
    }
    if (r.status != 207 && (r.status < 200 || r.status >= 300)) {
        LOG("[WebDAV] List failed for %s: HTTP %d", prefix.c_str(), r.status);
        return false;
    }

    auto items = ParsePropfind(r.body, m_baseUrlPath);
    std::string self = prefix;
    while (self.size() > 1 && self.back() == '/') self.pop_back();
    for (const auto& item : items) {
        if (item.path == self || item.path == prefix) continue;
        if (item.path.size() < prefix.size() || item.path.rfind(prefix, 0) != 0) continue;
        if (item.isCollection) {
            std::string childPrefix = item.path;
            if (!childPrefix.empty() && childPrefix.back() != '/') childPrefix += '/';
            if (!ListRecursive(childPrefix, result, outComplete, depth + 1)) return false;
        } else {
            result.push_back({item.path, item.size, item.modifiedTime});
        }
    }
    return true;
}

std::vector<std::string> WebDavProvider::ListSubfolders(const std::string& prefix) {
    if (!IsSafeRelativePath(prefix)) return {};

    std::string body =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
        "<d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/></d:prop></d:propfind>";
    auto r = Request("PROPFIND", prefix, body, {"Depth: 1", "Content-Type: application/xml; charset=utf-8"});
    if (r.status == 404 || r.status == 410) return {};
    if (r.status != 207 && (r.status < 200 || r.status >= 300)) return {};

    auto items = ParsePropfind(r.body, m_baseUrlPath);
    std::vector<std::string> folders;
    for (const auto& item : items) {
        if (!item.isCollection) continue;
        if (item.path.size() <= prefix.size() || item.path.rfind(prefix, 0) != 0) continue;
        auto rest = item.path.substr(prefix.size());
        if (rest.empty() || rest.find('/') != std::string::npos) continue;
        if (std::find(folders.begin(), folders.end(), rest) == folders.end())
            folders.push_back(rest);
    }
    return folders;
}

std::string WebDavProvider::Base64Encode(const std::string& input) {
    static constexpr char table[] =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::string out;
    out.reserve(((input.size() + 2) / 3) * 4);
    uint32_t val = 0;
    int valb = -6;
    for (uint8_t c : input) {
        val = (val << 8) | c;
        valb += 8;
        while (valb >= 0) {
            out.push_back(table[(val >> valb) & 0x3F]);
            valb -= 6;
        }
    }
    if (valb > -6) out.push_back(table[((val << 8) >> (valb + 8)) & 0x3F]);
    while (out.size() % 4) out.push_back('=');
    return out;
}

std::string WebDavProvider::Md5Hex(const std::string& input) {
    unsigned char hash[16] = {};
#ifdef _WIN32
    HCRYPTPROV hProv = 0;
    HCRYPTHASH hHash = 0;
    if (CryptAcquireContextW(&hProv, nullptr, nullptr, PROV_RSA_FULL, CRYPT_VERIFYCONTEXT)) {
        if (CryptCreateHash(hProv, CALG_MD5, 0, 0, &hHash)) {
            CryptHashData(hHash, reinterpret_cast<const BYTE*>(input.data()), (DWORD)input.size(), 0);
            DWORD len = sizeof(hash);
            CryptGetHashParam(hHash, HP_HASHVAL, hash, &len, 0);
            CryptDestroyHash(hHash);
        }
        CryptReleaseContext(hProv, 0);
    }
#else
    // Tiny MD5 implementation adapted for Digest auth input strings.
    auto leftRotate = [](uint32_t x, uint32_t c) {
        return (x << c) | (x >> (32 - c));
    };
    static constexpr uint32_t s[] = {
        7,12,17,22, 7,12,17,22, 7,12,17,22, 7,12,17,22,
        5,9,14,20, 5,9,14,20, 5,9,14,20, 5,9,14,20,
        4,11,16,23, 4,11,16,23, 4,11,16,23, 4,11,16,23,
        6,10,15,21, 6,10,15,21, 6,10,15,21, 6,10,15,21
    };
    static constexpr uint32_t k[] = {
        0xd76aa478,0xe8c7b756,0x242070db,0xc1bdceee,0xf57c0faf,0x4787c62a,0xa8304613,0xfd469501,
        0x698098d8,0x8b44f7af,0xffff5bb1,0x895cd7be,0x6b901122,0xfd987193,0xa679438e,0x49b40821,
        0xf61e2562,0xc040b340,0x265e5a51,0xe9b6c7aa,0xd62f105d,0x02441453,0xd8a1e681,0xe7d3fbc8,
        0x21e1cde6,0xc33707d6,0xf4d50d87,0x455a14ed,0xa9e3e905,0xfcefa3f8,0x676f02d9,0x8d2a4c8a,
        0xfffa3942,0x8771f681,0x6d9d6122,0xfde5380c,0xa4beea44,0x4bdecfa9,0xf6bb4b60,0xbebfbc70,
        0x289b7ec6,0xeaa127fa,0xd4ef3085,0x04881d05,0xd9d4d039,0xe6db99e5,0x1fa27cf8,0xc4ac5665,
        0xf4292244,0x432aff97,0xab9423a7,0xfc93a039,0x655b59c3,0x8f0ccc92,0xffeff47d,0x85845dd1,
        0x6fa87e4f,0xfe2ce6e0,0xa3014314,0x4e0811a1,0xf7537e82,0xbd3af235,0x2ad7d2bb,0xeb86d391
    };

    std::vector<uint8_t> msg(input.begin(), input.end());
    uint64_t bitLen = static_cast<uint64_t>(msg.size()) * 8;
    msg.push_back(0x80);
    while ((msg.size() % 64) != 56) msg.push_back(0);
    for (int i = 0; i < 8; ++i) msg.push_back(static_cast<uint8_t>(bitLen >> (8 * i)));

    uint32_t a0 = 0x67452301, b0 = 0xefcdab89, c0 = 0x98badcfe, d0 = 0x10325476;
    for (size_t offset = 0; offset < msg.size(); offset += 64) {
        uint32_t m[16];
        for (int i = 0; i < 16; ++i) {
            size_t j = offset + i * 4;
            m[i] = (uint32_t)msg[j] | ((uint32_t)msg[j + 1] << 8) |
                   ((uint32_t)msg[j + 2] << 16) | ((uint32_t)msg[j + 3] << 24);
        }

        uint32_t A = a0, B = b0, C = c0, D = d0;
        for (uint32_t i = 0; i < 64; ++i) {
            uint32_t F, g;
            if (i < 16) {
                F = (B & C) | ((~B) & D);
                g = i;
            } else if (i < 32) {
                F = (D & B) | ((~D) & C);
                g = (5 * i + 1) % 16;
            } else if (i < 48) {
                F = B ^ C ^ D;
                g = (3 * i + 5) % 16;
            } else {
                F = C ^ (B | (~D));
                g = (7 * i) % 16;
            }
            F += A + k[i] + m[g];
            A = D;
            D = C;
            C = B;
            B += leftRotate(F, s[i]);
        }
        a0 += A; b0 += B; c0 += C; d0 += D;
    }

    uint32_t words[4] = {a0, b0, c0, d0};
    for (int i = 0; i < 4; ++i) {
        hash[i * 4 + 0] = static_cast<unsigned char>(words[i] & 0xFF);
        hash[i * 4 + 1] = static_cast<unsigned char>((words[i] >> 8) & 0xFF);
        hash[i * 4 + 2] = static_cast<unsigned char>((words[i] >> 16) & 0xFF);
        hash[i * 4 + 3] = static_cast<unsigned char>((words[i] >> 24) & 0xFF);
    }
#endif

    static constexpr char hex[] = "0123456789abcdef";
    std::string out;
    out.reserve(32);
    for (unsigned char b : hash) {
        out.push_back(hex[b >> 4]);
        out.push_back(hex[b & 0x0F]);
    }
    return out;
}

bool WebDavProvider::ExtractDigestChallenge(const HttpResp& resp, DigestChallenge& out) {
    for (const auto& header : resp.headers) {
        size_t colon = header.find(':');
        if (colon == std::string::npos) continue;
        auto name = ToLower(Trim(header.substr(0, colon)));
        if (name != "www-authenticate") continue;
        auto value = Trim(header.substr(colon + 1));
        if (!StartsWithInsensitive(value, "Digest")) continue;

        auto params = ParseAuthParams(value.substr(6));
        out.realm = params["realm"];
        out.nonce = params["nonce"];
        out.opaque = params["opaque"];
        out.qop = params["qop"];
        out.algorithm = params["algorithm"];
        if (out.algorithm.empty()) out.algorithm = "MD5";
        if (!ContainsInsensitive(out.algorithm, "MD5")) {
            LOG("[WebDAV] Unsupported Digest algorithm: %s", out.algorithm.c_str());
            return false;
        }
        return !out.realm.empty() && !out.nonce.empty();
    }
    return false;
}

std::string WebDavProvider::BuildDigestAuthHeader(const char* method, const std::string& relPath,
                                                  const DigestChallenge& challenge) {
    std::string uri = DigestUriForUrl(UrlForPath(relPath));
    std::string qop = "auth";
    if (!challenge.qop.empty() && !ContainsInsensitive(challenge.qop, "auth"))
        qop.clear();

    std::string ha1 = Md5Hex(m_username + ":" + challenge.realm + ":" + m_password);
    std::string ha2 = Md5Hex(std::string(method) + ":" + uri);
    std::ostringstream nc;
    nc << std::hex << std::setw(8) << std::setfill('0') << ++m_digestNonceCount;
    std::string cnonce = RandomClientNonce();

    std::string response;
    if (!qop.empty()) {
        response = Md5Hex(ha1 + ":" + challenge.nonce + ":" + nc.str() + ":" +
                          cnonce + ":" + qop + ":" + ha2);
    } else {
        response = Md5Hex(ha1 + ":" + challenge.nonce + ":" + ha2);
    }

    auto quote = [](const std::string& s) {
        std::string out = "\"";
        for (char c : s) {
            if (c == '"' || c == '\\') out.push_back('\\');
            out.push_back(c);
        }
        out.push_back('"');
        return out;
    };

    std::string header = "Authorization: Digest username=" + quote(m_username) +
        ", realm=" + quote(challenge.realm) +
        ", nonce=" + quote(challenge.nonce) +
        ", uri=" + quote(uri) +
        ", response=" + quote(response) +
        ", algorithm=MD5";
    if (!challenge.opaque.empty())
        header += ", opaque=" + quote(challenge.opaque);
    if (!qop.empty())
        header += ", qop=" + qop + ", nc=" + nc.str() + ", cnonce=" + quote(cnonce);
    return header;
}

std::string WebDavProvider::XmlDecode(std::string s) {
    struct Entity { const char* from; const char* to; };
    static constexpr Entity entities[] = {
        {"&amp;", "&"}, {"&lt;", "<"}, {"&gt;", ">"}, {"&quot;", "\""}, {"&apos;", "'"}
    };
    for (const auto& entity : entities) {
        size_t pos = 0;
        while ((pos = s.find(entity.from, pos)) != std::string::npos) {
            s.replace(pos, strlen(entity.from), entity.to);
            pos += strlen(entity.to);
        }
    }
    return s;
}

std::vector<WebDavProvider::DavItem> WebDavProvider::ParsePropfind(
    const std::string& xml, const std::string& baseUrlPath) {
    std::vector<DavItem> items;
    size_t pos = 0;

    while (true) {
        size_t responseTag = xml.find(":response", pos);
        if (responseTag == std::string::npos) responseTag = xml.find("<response", pos);
        if (responseTag == std::string::npos) break;
        size_t responseStart = xml.rfind('<', responseTag);
        size_t responseOpenEnd = xml.find('>', responseTag);
        if (responseStart == std::string::npos || responseOpenEnd == std::string::npos) break;

        size_t responseNameStart = responseStart + 1;
        while (responseNameStart < responseOpenEnd &&
               std::isspace(static_cast<unsigned char>(xml[responseNameStart]))) {
            ++responseNameStart;
        }
        size_t responseNameEnd = responseNameStart;
        while (responseNameEnd < responseOpenEnd &&
               !std::isspace(static_cast<unsigned char>(xml[responseNameEnd])) &&
               xml[responseNameEnd] != '>') {
            ++responseNameEnd;
        }
        std::string responseName = xml.substr(responseNameStart, responseNameEnd - responseNameStart);
        std::string closeTag = "</" + responseName + ">";
        size_t responseEnd = xml.find(closeTag, responseOpenEnd + 1);
        if (responseEnd == std::string::npos) break;

        std::string block = xml.substr(responseOpenEnd + 1, responseEnd - responseOpenEnd - 1);
        pos = responseEnd + closeTag.size();

        std::string href = XmlDecode(ExtractElementBody(block, "href"));
        if (href.empty()) continue;

        size_t scheme = href.find("://");
        if (scheme != std::string::npos) {
            size_t pathStart = href.find('/', scheme + 3);
            href = pathStart == std::string::npos ? "/" : href.substr(pathStart);
        }
        size_t query = href.find_first_of("?#");
        if (query != std::string::npos) href.resize(query);
        while (href.size() > 1 && href.back() == '/') href.pop_back();
        href = UrlDecode(href);

        std::string base = baseUrlPath.empty() ? "/" : baseUrlPath;
        while (base.size() > 1 && base.back() == '/') base.pop_back();

        std::string rel;
        if (href == base) {
            continue;
        } else if (href.rfind(base + "/", 0) == 0) {
            rel = href.substr(base.size() + 1);
        } else if (!base.empty() && base != "/" && href.rfind(base, 0) == 0) {
            rel = href.substr(base.size());
            if (!rel.empty() && rel[0] == '/') rel.erase(rel.begin());
        } else if (!href.empty() && href[0] == '/') {
            rel = href.substr(1);
        } else {
            rel = href;
        }
        if (rel.empty()) continue;

        DavItem item;
        item.path = rel;
        item.isCollection = BlockHasCollectionResourceType(block);
        auto sizeText = Trim(ExtractElementBody(block, "getcontentlength"));
        if (!sizeText.empty()) {
            try { item.size = static_cast<uint64_t>(std::stoull(sizeText)); }
            catch (...) { item.size = 0; }
        }
        auto modifiedText = Trim(ExtractElementBody(block, "getlastmodified"));
        int64_t modified = ParseHttpDate(modifiedText);
        item.modifiedTime = modified > 0 ? static_cast<uint64_t>(modified) : 0;
        items.push_back(std::move(item));
    }

    return items;
}

int64_t WebDavProvider::ParseHttpDate(const std::string& value) {
    if (value.empty()) return 0;
    std::tm tm{};
    std::istringstream ss(value);
    ss.imbue(std::locale::classic());
    ss >> std::get_time(&tm, "%a, %d %b %Y %H:%M:%S GMT");
    if (ss.fail()) return 0;
#ifdef _WIN32
    return static_cast<int64_t>(_mkgmtime(&tm));
#else
    return static_cast<int64_t>(timegm(&tm));
#endif
}
