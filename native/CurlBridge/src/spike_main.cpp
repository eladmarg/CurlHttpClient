/* Stage-0 spike / deployment smoke test.
 *
 * Asserts that the libcurl statically linked into curl_http_bridge.dll is
 * the build this project requires (OpenSSL TLS backend, threaded resolver,
 * HTTP/2, brotli, zlib) and prints the version JSON.
 *
 * Exit codes: 0 = ok, 1 = initialization/validation failed.
 * Optional argument: a URL to fetch as an end-to-end smoke test.
 */

#include "curl_bridge.h"

#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

namespace
{
    int32_t CURL_BRIDGE_CALL on_body(void*, const uint8_t*, size_t length)
    {
        std::printf("  body chunk: %zu bytes\n", length);
        return 0;
    }

    int32_t CURL_BRIDGE_CALL on_header(void*, const uint8_t* line, size_t length,
                                       uint32_t flags, int32_t status)
    {
        std::string text(reinterpret_cast<const char*>(line), length);
        while (!text.empty() && (text.back() == '\r' || text.back() == '\n'))
        {
            text.pop_back();
        }
        std::printf("  header [flags=0x%x status=%d]: %s\n", flags, status, text.c_str());
        return 0;
    }
}

int main(int argc, char** argv)
{
    const curl_bridge_result init = curl_bridge_global_initialize();
    if (init != CURL_BRIDGE_OK)
    {
        std::fprintf(stderr, "FAIL: global initialize -> %d: %s\n",
                     static_cast<int>(init), curl_bridge_get_last_global_error());
        return 1;
    }

    char info[4096];
    curl_bridge_get_version_info(info, sizeof(info));
    std::printf("version info: %s\n", info);

    if (std::strstr(info, "\"ssl_version\":\"OpenSSL") == nullptr)
    {
        std::fprintf(stderr, "FAIL: TLS backend is not OpenSSL\n");
        return 1;
    }
    for (const char* feature : { "AsynchDNS", "HTTP2", "brotli", "libz" })
    {
        if (std::strstr(info, feature) == nullptr)
        {
            std::fprintf(stderr, "FAIL: required feature missing: %s\n", feature);
            return 1;
        }
    }
    std::printf("OK: OpenSSL backend + AsynchDNS + HTTP2 + brotli + libz present\n");

    if (argc > 1)
    {
        /* End-to-end fetch. Uses the Windows certificate store for trust so
         * the spike does not depend on a cacert.pem path. */
        curl_bridge_client_options options = {};
        options.struct_size = sizeof(options);
        options.use_native_ca = 1;
        options.follow_redirects = 1;
        options.max_redirects = 10;
        options.enable_decompression = 1;
        options.connect_timeout_ms = 15000;
        options.request_timeout_ms = 60000;

        curl_bridge_client* client = curl_bridge_client_create(&options);
        if (client == nullptr)
        {
            std::fprintf(stderr, "FAIL: client_create: %s\n",
                         curl_bridge_get_last_global_error());
            return 1;
        }

        curl_bridge_request* request = curl_bridge_request_create(client);
        curl_bridge_request_set_method(request, "GET");
        curl_bridge_request_set_url(request, argv[1]);
        curl_bridge_request_set_proxy(request, "", nullptr);

        curl_bridge_callbacks callbacks = {};
        callbacks.struct_size = sizeof(callbacks);
        callbacks.on_body_data = on_body;
        callbacks.on_header_line = on_header;
        curl_bridge_request_set_callbacks(request, &callbacks);

        curl_bridge_response_info response = {};
        response.struct_size = sizeof(response);

        std::printf("fetching %s ...\n", argv[1]);
        const curl_bridge_result result = curl_bridge_request_send(request, &response);
        std::printf("result=%d status=%d http=%d reused_connection=%s effective=%s\n",
                    static_cast<int>(result), response.status_code,
                    response.http_version,
                    response.num_connects == 0 ? "yes" : "no",
                    curl_bridge_request_get_effective_url(request));
        if (result != CURL_BRIDGE_OK)
        {
            std::fprintf(stderr, "FAIL: %s\n", curl_bridge_request_get_last_error(request));
        }

        curl_bridge_request_destroy(request);
        curl_bridge_client_destroy(client);
        return result == CURL_BRIDGE_OK ? 0 : 1;
    }

    return 0;
}
