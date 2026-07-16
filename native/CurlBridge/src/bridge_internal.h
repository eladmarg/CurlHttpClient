/* Internal declarations shared by the bridge translation units.
 * Nothing in this header is part of the public ABI. */
#ifndef CURL_BRIDGE_INTERNAL_H
#define CURL_BRIDGE_INTERNAL_H

#include "curl_bridge.h"

#include <curl/curl.h>

#include <atomic>
#include <cstdint>
#include <mutex>
#include <string>
#include <vector>

namespace bridge
{
    /* Deep copy of curl_bridge_client_options with owned strings. */
    struct ClientConfig
    {
        std::vector<uint8_t> ca_bundle_pem;
        std::string ca_bundle_path;
        bool use_native_ca = false;
        int min_tls_version = CURL_BRIDGE_TLS_DEFAULT;
        std::string tls12_cipher_list;
        std::string tls13_cipher_suites;
        std::string client_cert_path;
        std::string client_cert_type;
        std::string client_key_path;
        std::string client_key_password;

        long long connect_timeout_ms = 0;
        long long request_timeout_ms = 0;
        bool follow_redirects = true;
        int max_redirects = 10;
        bool enable_decompression = true;
        std::string accept_encoding; /* computed from built-in codecs at client create */
        bool enable_http2 = false;
        bool enable_cookie_engine = false;
        bool verbose = false;
        long buffer_size = 256 * 1024;
        long upload_buffer_size = 0; /* 0 => libcurl default (64 KiB) */

        int max_easy_handles = 0;
        long long connection_idle_timeout_secs = 0;
        long long connection_max_lifetime_secs = 0;
    };

    void set_last_global_error(std::string message);

    /* CURLcode + request state -> stable bridge result. */
    curl_bridge_result map_curl_code(CURLcode code,
                                     const curl_bridge_request* request,
                                     const curl_bridge_response_info* info);

    /* Human-readable annotation for a mapped failure. */
    std::string describe_failure(CURLcode code,
                                 curl_bridge_result mapped,
                                 const char* error_buffer);

    /* libcurl-facing callbacks (bridge_callbacks.cpp); userdata is the
     * curl_bridge_request*. */
    size_t curl_write_cb(char* ptr, size_t size, size_t nmemb, void* userdata);
    size_t curl_header_cb(char* ptr, size_t size, size_t nmemb, void* userdata);
    size_t curl_read_cb(char* buffer, size_t size, size_t nitems, void* userdata);
    int curl_seek_cb(void* userdata, curl_off_t offset, int origin);
    int curl_xferinfo_cb(void* userdata, curl_off_t dltotal, curl_off_t dlnow,
                         curl_off_t ultotal, curl_off_t ulnow);
    int curl_debug_cb(CURL* handle, curl_infotype type, char* data, size_t size,
                      void* userdata);

    /* Shared request configuration + result extraction (bridge_request.cpp),
     * reused by both the blocking-send and event-loop engines. configure()
     * appends bridge-generated headers to *extra_headers (caller frees +
     * detaches from request->headers after the transfer). */
    curl_bridge_result configure(CURL* h, curl_bridge_request* request,
                                 curl_slist** extra_headers);
    void fill_info(CURL* h, CURLcode code, curl_bridge_response_info* info);
} // namespace bridge

/* Sentinel for "request has no body at all" (vs -1 = streaming, unknown size). */
#define CURL_BRIDGE_NO_BODY INT64_MIN

struct curl_bridge_client
{
    bridge::ClientConfig config;

    CURLSH* share = nullptr;
    /* One exclusive lock per curl_lock_data slot. The share unlock callback
     * carries no access-type, so shared/exclusive locking is not possible. */
    std::mutex share_locks[CURL_LOCK_DATA_LAST];

    /* Pool of reusable easy handles. Each handle keeps its own live
     * connection cache across curl_easy_reset, which is where HTTP
     * keep-alive reuse comes from. */
    std::mutex pool_mutex;
    std::vector<CURL*> free_handles;
    size_t total_handles = 0;

    std::atomic<int> active_requests{0};
    std::atomic<bool> shutting_down{false};

    CURL* acquire_handle(std::string& error);
    void release_handle(CURL* handle);
};

struct curl_bridge_request
{
    explicit curl_bridge_request(curl_bridge_client* owner) : client(owner) {}
    ~curl_bridge_request()
    {
        /* During a transfer, extra_headers is physically linked onto the tail
         * of `headers`, so freeing `headers` frees it too. It is standalone
         * only when the request has no caller headers (headers == nullptr) and
         * was destroyed without a clean finish (see the event-loop orphan
         * paths); free it explicitly then to avoid a leak. finish_request /
         * request_send null it out on the normal path so this never runs then. */
        if (extra_headers != nullptr && headers == nullptr)
        {
            curl_slist_free_all(extra_headers);
        }
        if (headers != nullptr)
        {
            curl_slist_free_all(headers);
        }
    }

    curl_bridge_client* client;

    /* Configuration (set before send; not thread-safe). */
    std::string method;
    std::string url;
    curl_slist* headers = nullptr;
    int64_t content_length = CURL_BRIDGE_NO_BODY;
    long long timeout_override_ms = -1; /* <0 => inherit client default */
    std::string proxy;
    bool proxy_configured = false; /* send refuses to run until set (env-var proxying must never leak in) */
    std::string proxy_userpwd;
    bool proxy_userpwd_set = false;
    std::string redirect_protocols;
    curl_bridge_callbacks callbacks{};

    /* Cross-thread signals. */
    std::atomic<bool> cancel_requested{false};
    std::atomic<bool> in_send{false};

    /* Transfer-thread state (only touched by callbacks + send). */
    std::atomic<bool> body_started{false};
    int current_block_status = 0;
    bool current_block_informational = false;

    /* Results. */
    char error_buffer[CURL_ERROR_SIZE] = {0};
    std::string last_error;
    std::string effective_url;

    /* Event-loop engine only (owned by the loop thread once submitted).
     * easy is the handle currently running this request; extra_headers is the
     * bridge-generated slist tail to free on completion. */
    CURL* easy = nullptr;
    curl_slist* extra_headers = nullptr;
    bool submitted = false;
    /* Loop-thread-only guard: finish_request runs its cleanup + on_complete at
     * most once even if reached by both a Cancel command and CURLMSG_DONE. */
    bool finished = false;
};

#endif /* CURL_BRIDGE_INTERNAL_H */
