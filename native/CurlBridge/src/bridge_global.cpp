/* Process-wide initialization, build validation, version diagnostics. */

#include "bridge_internal.h"

#include <cstdio>
#include <cstring>
#include <mutex>

namespace
{
    std::once_flag g_init_once;
    curl_bridge_result g_init_result = CURL_BRIDGE_INTERNAL_ERROR;

    thread_local std::string g_last_global_error;

    constexpr const char* kBridgeVersion = "1.0.0";

    void append_json_escaped(std::string& out, const char* value)
    {
        if (value == nullptr)
        {
            return;
        }
        for (const char* p = value; *p != '\0'; ++p)
        {
            const unsigned char c = static_cast<unsigned char>(*p);
            switch (c)
            {
            case '"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            default:
                if (c < 0x20)
                {
                    char buf[8];
                    std::snprintf(buf, sizeof(buf), "\\u%04x", c);
                    out += buf;
                }
                else
                {
                    out += static_cast<char>(c);
                }
                break;
            }
        }
    }

    /* Validates that the libcurl we linked is the build this project
     * requires. A wrong build (Schannel TLS, synchronous DNS) must fail
     * loudly at startup, never at first request. */
    curl_bridge_result validate_build(std::string& error)
    {
        const curl_version_info_data* info = curl_version_info(CURLVERSION_NOW);
        if (info == nullptr)
        {
            error = "curl_version_info returned NULL";
            return CURL_BRIDGE_INTERNAL_ERROR;
        }
        if (info->ssl_version == nullptr ||
            std::strncmp(info->ssl_version, "OpenSSL", 7) != 0)
        {
            error = "libcurl TLS backend is not OpenSSL (got: ";
            error += info->ssl_version != nullptr ? info->ssl_version : "<none>";
            error += "). This build must never fall back to Schannel.";
            return CURL_BRIDGE_UNSUPPORTED;
        }
        if ((info->features & CURL_VERSION_ASYNCHDNS) == 0)
        {
            error = "libcurl lacks the threaded resolver (AsynchDNS). "
                    "Synchronous DNS would block cancellation during connects.";
            return CURL_BRIDGE_UNSUPPORTED;
        }
        if ((info->features & CURL_VERSION_LIBZ) == 0)
        {
            error = "libcurl lacks zlib (gzip/deflate decompression).";
            return CURL_BRIDGE_UNSUPPORTED;
        }
        return CURL_BRIDGE_OK;
    }
} // namespace

namespace bridge
{
    void set_last_global_error(std::string message)
    {
        g_last_global_error = std::move(message);
    }
} // namespace bridge

extern "C" {

CURL_BRIDGE_API curl_bridge_result CURL_BRIDGE_CALL
curl_bridge_global_initialize(void)
{
    try
    {
        std::call_once(g_init_once, []()
        {
            /* This is a MultiSSL build (the vcpkg curl port's http2 feature
             * drags in Schannel as an alternate backend on Windows). Pin the
             * backend to OpenSSL BEFORE any other libcurl call so nothing —
             * including a CURL_SSL_BACKEND environment variable — can ever
             * select Schannel in this process. */
            const CURLsslset ssl_set =
                curl_global_sslset(CURLSSLBACKEND_OPENSSL, nullptr, nullptr);
            if (ssl_set != CURLSSLSET_OK && ssl_set != CURLSSLSET_TOO_LATE)
            {
                bridge::set_last_global_error(
                    "curl_global_sslset(OpenSSL) failed: the linked libcurl "
                    "has no OpenSSL backend");
                g_init_result = CURL_BRIDGE_UNSUPPORTED;
                return;
            }

            const CURLcode code = curl_global_init(CURL_GLOBAL_DEFAULT);
            if (code != CURLE_OK)
            {
                bridge::set_last_global_error(
                    std::string("curl_global_init failed: ") + curl_easy_strerror(code));
                g_init_result = CURL_BRIDGE_INTERNAL_ERROR;
                return;
            }
            std::string error;
            g_init_result = validate_build(error);
            if (g_init_result != CURL_BRIDGE_OK)
            {
                bridge::set_last_global_error(std::move(error));
            }
        });
        return g_init_result;
    }
    catch (const std::exception& ex)
    {
        bridge::set_last_global_error(std::string("global_initialize: ") + ex.what());
        return CURL_BRIDGE_INTERNAL_ERROR;
    }
    catch (...)
    {
        bridge::set_last_global_error("global_initialize: unknown native exception");
        return CURL_BRIDGE_INTERNAL_ERROR;
    }
}

CURL_BRIDGE_API size_t CURL_BRIDGE_CALL
curl_bridge_get_version_info(char* buffer, size_t buffer_length)
{
    try
    {
        const curl_version_info_data* info = curl_version_info(CURLVERSION_NOW);

        std::string json = "{";
        json += "\"bridge_version\":\"";
        append_json_escaped(json, kBridgeVersion);
        json += "\",\"curl_version\":\"";
        append_json_escaped(json, info != nullptr ? info->version : nullptr);
        json += "\",\"ssl_version\":\"";
        append_json_escaped(json, info != nullptr ? info->ssl_version : nullptr);
        json += "\",\"features\":[";
        if (info != nullptr)
        {
            struct { unsigned int bit; const char* name; } flags[] =
            {
                { CURL_VERSION_ASYNCHDNS, "AsynchDNS" },
                { CURL_VERSION_HTTP2,     "HTTP2" },
                { CURL_VERSION_LIBZ,      "libz" },
                { CURL_VERSION_BROTLI,    "brotli" },
                { CURL_VERSION_IPV6,      "IPv6" },
                { CURL_VERSION_SSL,       "SSL" },
                { CURL_VERSION_NTLM,      "NTLM" },
                { CURL_VERSION_SSPI,      "SSPI" },
                { CURL_VERSION_LARGEFILE, "Largefile" },
            };
            bool first = true;
            for (const auto& f : flags)
            {
                if ((info->features & f.bit) != 0)
                {
                    if (!first) json += ",";
                    json += "\"";
                    json += f.name;
                    json += "\"";
                    first = false;
                }
            }
        }
        json += "],\"protocols\":[";
        if (info != nullptr && info->protocols != nullptr)
        {
            bool first = true;
            for (const char* const* p = info->protocols; *p != nullptr; ++p)
            {
                if (!first) json += ",";
                json += "\"";
                append_json_escaped(json, *p);
                json += "\"";
                first = false;
            }
        }
        json += "]}";

        if (buffer != nullptr && buffer_length > 0)
        {
            const size_t to_copy =
                json.size() < buffer_length - 1 ? json.size() : buffer_length - 1;
            std::memcpy(buffer, json.data(), to_copy);
            buffer[to_copy] = '\0';
        }
        return json.size();
    }
    catch (...)
    {
        if (buffer != nullptr && buffer_length > 0)
        {
            buffer[0] = '\0';
        }
        return 0;
    }
}

CURL_BRIDGE_API const char* CURL_BRIDGE_CALL
curl_bridge_get_last_global_error(void)
{
    return g_last_global_error.c_str();
}

} /* extern "C" */
