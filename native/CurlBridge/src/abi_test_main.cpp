/* Native ABI contract tests for curl_http_bridge.dll — no managed runtime,
 * no network. Exercises exactly the guarantees documented in curl_bridge.h.
 * Exit code 0 = all contracts hold. */

#include "curl_bridge.h"

#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

static int g_failures = 0;

#define CHECK(condition, message)                                          \
    do                                                                     \
    {                                                                      \
        if (!(condition))                                                  \
        {                                                                  \
            std::fprintf(stderr, "FAIL: %s (%s:%d)\n", message, __FILE__,  \
                         __LINE__);                                        \
            ++g_failures;                                                  \
        }                                                                  \
    } while (0)

/* Both JSON exports share one buffer contract: return required size,
 * NUL-terminate whenever length > 0, never overrun. */
static void test_json_buffer_contract(size_t (CURL_BRIDGE_CALL *fn)(char*, size_t),
                                      const char* name)
{
    const size_t required = fn(nullptr, 0);
    CHECK(required > 2, name);

    char tiny[8];
    std::memset(tiny, 'X', sizeof(tiny));
    const size_t reported = fn(tiny, sizeof(tiny));
    CHECK(reported == required, "short-buffer call must report the same size");
    CHECK(tiny[7] == '\0' || std::strlen(tiny) < sizeof(tiny),
          "short buffer must be NUL-terminated");

    std::vector<char> full(required + 1);
    const size_t written = fn(full.data(), full.size());
    CHECK(written == required, "exact-buffer call must report the same size");
    CHECK(std::strlen(full.data()) == required, "output length must match the report");
    CHECK(full[0] == '{', "output must be a JSON object");
}

int main()
{
    /* ---- global initialization ---- */
    CHECK(curl_bridge_global_initialize() == CURL_BRIDGE_OK, "global initialize");
    CHECK(curl_bridge_global_initialize() == CURL_BRIDGE_OK, "initialize is idempotent");

    /* ---- version info + cipher inventory buffer contracts ---- */
    test_json_buffer_contract(curl_bridge_get_version_info, "version info size probe");
    test_json_buffer_contract(curl_bridge_enumerate_ciphers, "cipher inventory size probe");

    std::vector<char> info(curl_bridge_get_version_info(nullptr, 0) + 1);
    curl_bridge_get_version_info(info.data(), info.size());
    CHECK(std::strstr(info.data(), "\"ssl_version\":\"OpenSSL") != nullptr,
          "TLS backend must report OpenSSL");
    CHECK(std::strstr(info.data(), "AsynchDNS") != nullptr, "threaded resolver required");

    std::vector<char> ciphers(curl_bridge_enumerate_ciphers(nullptr, 0) + 1);
    curl_bridge_enumerate_ciphers(ciphers.data(), ciphers.size());
    CHECK(std::strstr(ciphers.data(), "TLS_AES_256_GCM_SHA384") != nullptr,
          "inventory must contain the mandatory TLS 1.3 suite");
    CHECK(std::strstr(ciphers.data(), "ECDHE-RSA-AES128-GCM-SHA256") != nullptr,
          "inventory must contain the canonical TLS 1.2 suite");
    CHECK(std::strstr(ciphers.data(), "DES-CBC3") == nullptr,
          "3DES must be compiled out of this build");
    CHECK(std::strstr(ciphers.data(), "\"openssl_version\":\"OpenSSL") != nullptr,
          "inventory must self-identify its OpenSSL version");

    /* ---- NULL-handle guarantees ---- */
    curl_bridge_client_destroy(nullptr);
    curl_bridge_request_destroy(nullptr);
    curl_bridge_request_cancel(nullptr);
    CHECK(curl_bridge_request_is_cancelled(nullptr) == 0, "is_cancelled(NULL) is 0");
    CHECK(curl_bridge_request_create(nullptr) == nullptr, "request_create(NULL client)");
    CHECK(curl_bridge_client_create(nullptr) == nullptr, "client_create(NULL options)");
    CHECK(std::strlen(curl_bridge_get_last_global_error()) > 0,
          "client_create failure must leave an error message");

    /* ---- client refuses to start without a trust source ---- */
    curl_bridge_client_options no_trust = {};
    no_trust.struct_size = sizeof(no_trust);
    CHECK(curl_bridge_client_create(&no_trust) == nullptr,
          "client without any trust source must be refused");

    /* ---- request lifecycle + argument validation ---- */
    curl_bridge_client_options options = {};
    options.struct_size = sizeof(options);
    options.use_native_ca = 1;
    curl_bridge_client* client = curl_bridge_client_create(&options);
    CHECK(client != nullptr, "client with native CA trust must be created");

    curl_bridge_request* request = curl_bridge_request_create(client);
    CHECK(request != nullptr, "request_create");

    CHECK(curl_bridge_request_set_method(request, nullptr) == CURL_BRIDGE_INVALID_ARGUMENT,
          "NULL method rejected");
    CHECK(curl_bridge_request_set_url(request, "") == CURL_BRIDGE_INVALID_ARGUMENT,
          "empty URL rejected");
    CHECK(curl_bridge_request_set_method(request, "GET") == CURL_BRIDGE_OK, "set method");
    CHECK(curl_bridge_request_set_url(request, "http://127.0.0.1:1/") == CURL_BRIDGE_OK,
          "set URL");

    /* send refuses to run until the proxy has been configured explicitly */
    curl_bridge_response_info response = {};
    response.struct_size = sizeof(response);
    CHECK(curl_bridge_request_send(request, &response) == CURL_BRIDGE_INVALID_ARGUMENT,
          "send without explicit proxy configuration must be refused");
    CHECK(std::strstr(curl_bridge_request_get_last_error(request), "proxy") != nullptr,
          "proxy refusal must be explained");

    /* cancel is idempotent and observable before send */
    curl_bridge_request_cancel(request);
    curl_bridge_request_cancel(request);
    CHECK(curl_bridge_request_is_cancelled(request) == 1, "cancel flag observable");

    curl_bridge_request_destroy(request);
    curl_bridge_client_destroy(client);

    if (g_failures == 0)
    {
        std::printf("OK: all native ABI contracts hold\n");
        return 0;
    }
    std::fprintf(stderr, "%d ABI contract failure(s)\n", g_failures);
    return 1;
}
