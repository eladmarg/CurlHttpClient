/* Request object: setters, per-transfer easy-handle configuration, and the
 * blocking send. One send per request object; the managed layer creates a
 * fresh request per HttpRequestMessage. */

#include "bridge_internal.h"

#include <climits>
#include <cstring>
#include <thread>

namespace
{
    curl_bridge_result fail(curl_bridge_request* request, std::string message,
                            curl_bridge_result result)
    {
        request->last_error = std::move(message);
        return result;
    }

    /* Records an error message without ever throwing — for use inside catch
     * handlers, where the caught exception may itself be std::bad_alloc and a
     * throwing string assignment would escape the export across the C ABI
     * (std::terminate under /EHsc). Best-effort: if even assigning the literal
     * fails, the error text is simply left as-is. */
    curl_bridge_result fail_noexcept(curl_bridge_request* request, const char* message,
                                     curl_bridge_result result) noexcept
    {
        try
        {
            request->last_error = message;
        }
        catch (...)
        {
        }
        return result;
    }

    bool has_body(const curl_bridge_request& request)
    {
        return request.content_length != CURL_BRIDGE_NO_BODY;
    }

    /* libcurl's timeout/age options take a `long`, which is 32-bit on LLP64
     * (Win64). A managed TimeSpan can exceed LONG_MAX ms/s; casting straight
     * to long wraps negative (rejected by setopt -> whole request fails) or,
     * above 2^32, silently truncates to a small positive value. Clamp into
     * [0, LONG_MAX] so a very large "effectively infinite" timeout becomes the
     * largest value libcurl accepts rather than a wrong or rejected one.
     * Managed-side validation (Options.Validate) rejects the extreme cases
     * before they reach here; this is the last-line native guard. */
    long clamp_to_long(long long value)
    {
        if (value > static_cast<long long>(LONG_MAX))
        {
            return LONG_MAX;
        }
        if (value < 0)
        {
            return 0;
        }
        return static_cast<long>(value);
    }

    /* Applies HTTP-method semantics. libcurl couples methods to transfer
     * modes, so this is more subtle than CURLOPT_CUSTOMREQUEST alone:
     *  - POST uses CURLOPT_POST so libcurl's own 301/302/303 POST->GET
     *    rewrite (matching .NET redirect behaviour) stays active.
     *  - PUT and every other verb with a body use the upload path; for
     *    non-PUT verbs CURLOPT_CUSTOMREQUEST overrides the method name,
     *    which pins the method across redirects (documented divergence).
     *  - Bodyless custom verbs ride on the GET transfer mode. */
    CURLcode apply_method(CURL* h, const curl_bridge_request& request)
    {
        const std::string& m = request.method;
        const bool body = has_body(request);

        if (m == "GET" && !body)
        {
            return curl_easy_setopt(h, CURLOPT_HTTPGET, 1L);
        }
        if (m == "HEAD")
        {
            return curl_easy_setopt(h, CURLOPT_NOBODY, 1L);
        }
        if (m == "POST")
        {
            CURLcode c = curl_easy_setopt(h, CURLOPT_POST, 1L);
            if (c != CURLE_OK) return c;
            /* Body always flows through the read callback; never POSTFIELDS. */
            if (request.content_length >= 0)
            {
                c = curl_easy_setopt(h, CURLOPT_POSTFIELDSIZE_LARGE,
                                     static_cast<curl_off_t>(request.content_length));
            }
            else if (!body)
            {
                c = curl_easy_setopt(h, CURLOPT_POSTFIELDSIZE_LARGE,
                                     static_cast<curl_off_t>(0));
            }
            return c;
        }
        if (body)
        {
            CURLcode c = curl_easy_setopt(h, CURLOPT_UPLOAD, 1L);
            if (c != CURLE_OK) return c;
            if (request.content_length >= 0)
            {
                c = curl_easy_setopt(h, CURLOPT_INFILESIZE_LARGE,
                                     static_cast<curl_off_t>(request.content_length));
                if (c != CURLE_OK) return c;
            }
            if (m != "PUT")
            {
                c = curl_easy_setopt(h, CURLOPT_CUSTOMREQUEST, m.c_str());
            }
            return c;
        }
        /* Bodyless non-GET verb (DELETE, OPTIONS, custom). */
        return curl_easy_setopt(h, CURLOPT_CUSTOMREQUEST, m.c_str());
    }

    curl_bridge_result configure_impl(CURL* h, curl_bridge_request* request,
                                      curl_slist** extra_headers)
    {
        const bridge::ClientConfig& cfg = request->client->config;

        request->error_buffer[0] = '\0';

        struct OptFail { CURLoption opt; };
        auto set = [&](CURLoption opt, auto value) -> bool
        {
            return curl_easy_setopt(h, opt, value) == CURLE_OK;
        };

        bool ok = true;
        ok = ok && set(CURLOPT_ERRORBUFFER, request->error_buffer);
        ok = ok && set(CURLOPT_URL, request->url.c_str());
        ok = ok && set(CURLOPT_NOSIGNAL, 1L);
        ok = ok && set(CURLOPT_PROTOCOLS_STR, "http,https");
        ok = ok && set(CURLOPT_SUPPRESS_CONNECT_HEADERS, 1L);
        /* Custom headers must go to the origin only, never onto proxy
         * CONNECT requests. */
        ok = ok && set(CURLOPT_HEADEROPT, CURLHEADER_SEPARATE);
        ok = ok && set(CURLOPT_BUFFERSIZE, static_cast<long>(cfg.buffer_size));
        if (cfg.upload_buffer_size > 0)
        {
            ok = ok && set(CURLOPT_UPLOAD_BUFFERSIZE,
                           static_cast<long>(cfg.upload_buffer_size));
        }
        ok = ok && set(CURLOPT_HTTP_VERSION,
                       cfg.enable_http2 ? CURL_HTTP_VERSION_2TLS
                                        : CURL_HTTP_VERSION_1_1);
        if (cfg.enable_http2)
        {
            /* Wait for an existing multiplexable h2 connection rather than
             * racing a second one open — improves connection reuse. */
            ok = ok && set(CURLOPT_PIPEWAIT, 1L);
        }

        /* Callbacks. */
        ok = ok && set(CURLOPT_WRITEFUNCTION, bridge::curl_write_cb);
        ok = ok && set(CURLOPT_WRITEDATA, request);
        ok = ok && set(CURLOPT_HEADERFUNCTION, bridge::curl_header_cb);
        ok = ok && set(CURLOPT_HEADERDATA, request);
        ok = ok && set(CURLOPT_NOPROGRESS, 0L);
        ok = ok && set(CURLOPT_XFERINFOFUNCTION, bridge::curl_xferinfo_cb);
        ok = ok && set(CURLOPT_XFERINFODATA, request);
        if (has_body(*request))
        {
            ok = ok && set(CURLOPT_READFUNCTION, bridge::curl_read_cb);
            ok = ok && set(CURLOPT_READDATA, request);
            ok = ok && set(CURLOPT_SEEKFUNCTION, bridge::curl_seek_cb);
            ok = ok && set(CURLOPT_SEEKDATA, request);
        }
        if (cfg.verbose && request->callbacks.on_debug != nullptr)
        {
            ok = ok && set(CURLOPT_DEBUGFUNCTION, bridge::curl_debug_cb);
            ok = ok && set(CURLOPT_DEBUGDATA, request);
            ok = ok && set(CURLOPT_VERBOSE, 1L);
        }

        /* Method + body mode. */
        if (ok && apply_method(h, *request) != CURLE_OK)
        {
            return fail(request, "failed to apply HTTP method options",
                        CURL_BRIDGE_INVALID_ARGUMENT);
        }
        if (request->content_length == -1)
        {
            /* Streaming body of unknown size: libcurl requires an explicit
             * chunked TE header on HTTP/1.1 (ignored/translated under h2). */
            *extra_headers =
                curl_slist_append(*extra_headers, "Transfer-Encoding: chunked");
        }

        /* Headers: request headers first, then bridge-generated ones. */
        curl_slist* all_headers = request->headers;
        if (*extra_headers != nullptr)
        {
            /* Chain: last request header -> extra headers. Restored before free. */
            if (all_headers == nullptr)
            {
                all_headers = *extra_headers;
            }
            else
            {
                curl_slist* tail = all_headers;
                while (tail->next != nullptr) tail = tail->next;
                tail->next = *extra_headers;
            }
        }
        if (all_headers != nullptr)
        {
            ok = ok && set(CURLOPT_HTTPHEADER, all_headers);
        }

        /* Redirects. */
        ok = ok && set(CURLOPT_FOLLOWLOCATION, cfg.follow_redirects ? 1L : 0L);
        ok = ok && set(CURLOPT_MAXREDIRS, static_cast<long>(cfg.max_redirects));
        if (!request->redirect_protocols.empty())
        {
            ok = ok && set(CURLOPT_REDIR_PROTOCOLS_STR,
                           request->redirect_protocols.c_str());
        }
        /* CURLOPT_UNRESTRICTED_AUTH stays at its default (0): libcurl strips
         * Authorization and Cookie headers when a redirect changes host,
         * scheme or port. */

        /* Proxy: always explicit; "" also disables environment lookup. */
        ok = ok && set(CURLOPT_PROXY, request->proxy.c_str());
        if (request->proxy_userpwd_set)
        {
            ok = ok && set(CURLOPT_PROXYUSERPWD, request->proxy_userpwd.c_str());
            ok = ok && set(CURLOPT_PROXYAUTH, static_cast<long>(CURLAUTH_ANY));
        }

        /* Timeouts. (clamp_to_long: libcurl `long` is 32-bit on Win64.) */
        if (cfg.connect_timeout_ms > 0)
        {
            ok = ok && set(CURLOPT_CONNECTTIMEOUT_MS,
                           clamp_to_long(cfg.connect_timeout_ms));
        }
        const long long total_timeout_ms = request->timeout_override_ms >= 0
            ? request->timeout_override_ms
            : cfg.request_timeout_ms;
        if (total_timeout_ms > 0)
        {
            ok = ok && set(CURLOPT_TIMEOUT_MS, clamp_to_long(total_timeout_ms));
        }

        /* Connection lifetime. */
        if (cfg.connection_idle_timeout_secs > 0)
        {
            ok = ok && set(CURLOPT_MAXAGE_CONN,
                           clamp_to_long(cfg.connection_idle_timeout_secs));
        }
        if (cfg.connection_max_lifetime_secs > 0)
        {
            ok = ok && set(CURLOPT_MAXLIFETIME_CONN,
                           clamp_to_long(cfg.connection_max_lifetime_secs));
        }

        /* Decompression. */
        if (cfg.enable_decompression)
        {
            ok = ok && set(CURLOPT_ACCEPT_ENCODING, cfg.accept_encoding.c_str());
        }

        /* Cookies (per-request-chain engine; scrubbed on handle release). */
        if (cfg.enable_cookie_engine)
        {
            ok = ok && set(CURLOPT_COOKIEFILE, "");
        }

        /* TLS. Verification is unconditional. */
        ok = ok && set(CURLOPT_SSL_VERIFYPEER, 1L);
        ok = ok && set(CURLOPT_SSL_VERIFYHOST, 2L);
        if (!cfg.ca_bundle_path.empty())
        {
            /* Path (not blob): lets OpenSSL cache the parsed X509 store across
             * new connections (CURLOPT_CA_CACHE_TIMEOUT default 24 h). The
             * blob form disables that cache and re-parses the ~200 KB bundle
             * on every TLS handshake. */
            ok = ok && set(CURLOPT_CAINFO, cfg.ca_bundle_path.c_str());
        }
        else if (!cfg.ca_bundle_pem.empty())
        {
            curl_blob blob;
            blob.data = const_cast<uint8_t*>(cfg.ca_bundle_pem.data());
            blob.len = cfg.ca_bundle_pem.size();
            /* The client outlives every transfer; no copy needed. */
            blob.flags = CURL_BLOB_NOCOPY;
            ok = ok && set(CURLOPT_CAINFO_BLOB, &blob);
        }
        long ssl_options = 0;
        if (cfg.use_native_ca)
        {
            ssl_options |= CURLSSLOPT_NATIVE_CA;
        }
        if (ssl_options != 0)
        {
            ok = ok && set(CURLOPT_SSL_OPTIONS, ssl_options);
        }
        ok = ok && set(CURLOPT_SSLVERSION,
                       cfg.min_tls_version == CURL_BRIDGE_TLS_1_3
                           ? CURL_SSLVERSION_TLSv1_3
                           : CURL_SSLVERSION_TLSv1_2);
        if (!cfg.tls12_cipher_list.empty())
        {
            ok = ok && set(CURLOPT_SSL_CIPHER_LIST, cfg.tls12_cipher_list.c_str());
        }
        if (!cfg.tls13_cipher_suites.empty())
        {
            ok = ok && set(CURLOPT_TLS13_CIPHERS, cfg.tls13_cipher_suites.c_str());
        }
        if (!cfg.client_cert_path.empty())
        {
            ok = ok && set(CURLOPT_SSLCERT, cfg.client_cert_path.c_str());
            ok = ok && set(CURLOPT_SSLCERTTYPE,
                           cfg.client_cert_type.empty() ? "PEM"
                                                        : cfg.client_cert_type.c_str());
            if (!cfg.client_key_path.empty())
            {
                ok = ok && set(CURLOPT_SSLKEY, cfg.client_key_path.c_str());
            }
            if (!cfg.client_key_password.empty())
            {
                ok = ok && set(CURLOPT_KEYPASSWD, cfg.client_key_password.c_str());
            }
        }

        if (!ok)
        {
            return fail(request, "curl_easy_setopt failed while configuring the transfer",
                        CURL_BRIDGE_INTERNAL_ERROR);
        }
        return CURL_BRIDGE_OK;
    }

    void fill_info_impl(CURL* h, CURLcode code, curl_bridge_response_info* info)
    {
        if (info == nullptr || info->struct_size < sizeof(curl_bridge_response_info))
        {
            return;
        }
        info->curl_error_code = static_cast<int32_t>(code);

        long status = 0;
        curl_easy_getinfo(h, CURLINFO_RESPONSE_CODE, &status);
        info->status_code = static_cast<int32_t>(status);

        long version = 0;
        curl_easy_getinfo(h, CURLINFO_HTTP_VERSION, &version);
        switch (version)
        {
        case CURL_HTTP_VERSION_1_0: info->http_version = 10; break;
        case CURL_HTTP_VERSION_1_1: info->http_version = 11; break;
        case CURL_HTTP_VERSION_2_0: info->http_version = 20; break;
        case CURL_HTTP_VERSION_3:   info->http_version = 30; break;
        default:                    info->http_version = 0;  break;
        }

        long num_connects = 0;
        curl_easy_getinfo(h, CURLINFO_NUM_CONNECTS, &num_connects);
        info->num_connects = static_cast<int32_t>(num_connects);

        long redirects = 0;
        curl_easy_getinfo(h, CURLINFO_REDIRECT_COUNT, &redirects);
        info->redirect_count = static_cast<int32_t>(redirects);

        curl_off_t t = 0;
        if (curl_easy_getinfo(h, CURLINFO_NAMELOOKUP_TIME_T, &t) == CURLE_OK)
            info->namelookup_time_us = t;
        t = 0;
        if (curl_easy_getinfo(h, CURLINFO_CONNECT_TIME_T, &t) == CURLE_OK)
            info->connect_time_us = t;
        t = 0;
        if (curl_easy_getinfo(h, CURLINFO_APPCONNECT_TIME_T, &t) == CURLE_OK)
            info->appconnect_time_us = t;
        t = 0;
        if (curl_easy_getinfo(h, CURLINFO_STARTTRANSFER_TIME_T, &t) == CURLE_OK)
            info->starttransfer_time_us = t;
        t = 0;
        if (curl_easy_getinfo(h, CURLINFO_TOTAL_TIME_T, &t) == CURLE_OK)
            info->total_time_us = t;

        curl_off_t content_length = -1;
        if (curl_easy_getinfo(h, CURLINFO_CONTENT_LENGTH_DOWNLOAD_T, &content_length) == CURLE_OK)
            info->content_length = content_length;
        else
            info->content_length = -1;
    }
} // namespace

namespace bridge
{
    /* Public-to-the-bridge wrappers so bridge_multi.cpp can reuse the request
     * configuration + result extraction without duplicating them. */
    curl_bridge_result configure(CURL* h, curl_bridge_request* request,
                                 curl_slist** extra_headers)
    {
        return configure_impl(h, request, extra_headers);
    }

    void fill_info(CURL* h, CURLcode code, curl_bridge_response_info* info)
    {
        fill_info_impl(h, code, info);
    }
} // namespace bridge

extern "C" {

CURL_BRIDGE_API curl_bridge_request* CURL_BRIDGE_CALL
curl_bridge_request_create(curl_bridge_client* client)
{
    try
    {
        if (client == nullptr ||
            client->shutting_down.load(std::memory_order_acquire))
        {
            return nullptr;
        }
        return new curl_bridge_request(client);
    }
    catch (...)
    {
        return nullptr;
    }
}

CURL_BRIDGE_API void CURL_BRIDGE_CALL
curl_bridge_request_destroy(curl_bridge_request* request)
{
    if (request == nullptr)
    {
        return;
    }
    try
    {
        /* Contract: destroy is not called while send runs. Defend anyway —
         * cancel and wait rather than free memory under a live transfer. */
        if (request->in_send.load(std::memory_order_acquire))
        {
            request->cancel_requested.store(true, std::memory_order_release);
            while (request->in_send.load(std::memory_order_acquire))
            {
                std::this_thread::yield();
            }
        }
        delete request;
    }
    catch (...)
    {
    }
}

CURL_BRIDGE_API curl_bridge_result CURL_BRIDGE_CALL
curl_bridge_request_set_method(curl_bridge_request* request, const char* method_utf8)
{
    if (request == nullptr || method_utf8 == nullptr || method_utf8[0] == '\0')
    {
        return CURL_BRIDGE_INVALID_ARGUMENT;
    }
    try
    {
        request->method = method_utf8;
        return CURL_BRIDGE_OK;
    }
    catch (...)
    {
        return CURL_BRIDGE_INTERNAL_ERROR;
    }
}

CURL_BRIDGE_API curl_bridge_result CURL_BRIDGE_CALL
curl_bridge_request_set_url(curl_bridge_request* request, const char* url_utf8)
{
    if (request == nullptr || url_utf8 == nullptr || url_utf8[0] == '\0')
    {
        return CURL_BRIDGE_INVALID_ARGUMENT;
    }
    try
    {
        request->url = url_utf8;
        return CURL_BRIDGE_OK;
    }
    catch (...)
    {
        return CURL_BRIDGE_INTERNAL_ERROR;
    }
}

CURL_BRIDGE_API curl_bridge_result CURL_BRIDGE_CALL
curl_bridge_request_add_header(curl_bridge_request* request, const char* header_utf8)
{
    if (request == nullptr || header_utf8 == nullptr || header_utf8[0] == '\0')
    {
        return CURL_BRIDGE_INVALID_ARGUMENT;
    }
    try
    {
        curl_slist* appended = curl_slist_append(request->headers, header_utf8);
        if (appended == nullptr)
        {
            return CURL_BRIDGE_INTERNAL_ERROR;
        }
        request->headers = appended;
        return CURL_BRIDGE_OK;
    }
    catch (...)
    {
        return CURL_BRIDGE_INTERNAL_ERROR;
    }
}

CURL_BRIDGE_API curl_bridge_result CURL_BRIDGE_CALL
curl_bridge_request_set_body(curl_bridge_request* request, int64_t content_length)
{
    if (request == nullptr || content_length < -1)
    {
        return CURL_BRIDGE_INVALID_ARGUMENT;
    }
    request->content_length = content_length;
    return CURL_BRIDGE_OK;
}

CURL_BRIDGE_API curl_bridge_result CURL_BRIDGE_CALL
curl_bridge_request_set_timeout(curl_bridge_request* request, int64_t request_timeout_ms)
{
    if (request == nullptr)
    {
        return CURL_BRIDGE_INVALID_ARGUMENT;
    }
    request->timeout_override_ms = request_timeout_ms;
    return CURL_BRIDGE_OK;
}

CURL_BRIDGE_API curl_bridge_result CURL_BRIDGE_CALL
curl_bridge_request_set_proxy(curl_bridge_request* request,
                              const char* proxy_utf8,
                              const char* userpwd_utf8)
{
    if (request == nullptr || proxy_utf8 == nullptr)
    {
        return CURL_BRIDGE_INVALID_ARGUMENT;
    }
    try
    {
        request->proxy = proxy_utf8;
        request->proxy_configured = true;
        if (userpwd_utf8 != nullptr && userpwd_utf8[0] != '\0')
        {
            request->proxy_userpwd = userpwd_utf8;
            request->proxy_userpwd_set = true;
        }
        return CURL_BRIDGE_OK;
    }
    catch (...)
    {
        return CURL_BRIDGE_INTERNAL_ERROR;
    }
}

CURL_BRIDGE_API curl_bridge_result CURL_BRIDGE_CALL
curl_bridge_request_set_redirect_protocols(curl_bridge_request* request,
                                           const char* protocols_utf8)
{
    if (request == nullptr || protocols_utf8 == nullptr)
    {
        return CURL_BRIDGE_INVALID_ARGUMENT;
    }
    try
    {
        request->redirect_protocols = protocols_utf8;
        return CURL_BRIDGE_OK;
    }
    catch (...)
    {
        return CURL_BRIDGE_INTERNAL_ERROR;
    }
}

CURL_BRIDGE_API curl_bridge_result CURL_BRIDGE_CALL
curl_bridge_request_set_callbacks(curl_bridge_request* request,
                                  const curl_bridge_callbacks* callbacks)
{
    if (request == nullptr || callbacks == nullptr ||
        callbacks->struct_size < sizeof(curl_bridge_callbacks))
    {
        return CURL_BRIDGE_INVALID_ARGUMENT;
    }
    request->callbacks = *callbacks;
    return CURL_BRIDGE_OK;
}

CURL_BRIDGE_API curl_bridge_result CURL_BRIDGE_CALL
curl_bridge_request_send(curl_bridge_request* request,
                         curl_bridge_response_info* info)
{
    if (request == nullptr)
    {
        return CURL_BRIDGE_INVALID_ARGUMENT;
    }
    try
    {
        if (request->url.empty() || request->method.empty())
        {
            return fail(request, "method and URL must be set before send",
                        CURL_BRIDGE_INVALID_ARGUMENT);
        }
        if (!request->proxy_configured)
        {
            return fail(request,
                        "proxy must be configured explicitly (\"\" to disable); "
                        "environment-variable proxies are never honored implicitly",
                        CURL_BRIDGE_INVALID_ARGUMENT);
        }
        if (request->callbacks.on_body_data == nullptr ||
            request->callbacks.on_header_line == nullptr)
        {
            return fail(request, "body and header callbacks must be set before send",
                        CURL_BRIDGE_INVALID_ARGUMENT);
        }
        if (has_body(*request) && request->callbacks.on_read_body == nullptr)
        {
            return fail(request, "a request with a body requires on_read_body",
                        CURL_BRIDGE_INVALID_ARGUMENT);
        }

        curl_bridge_client* client = request->client;
        client->active_requests.fetch_add(1, std::memory_order_acq_rel);
        if (client->shutting_down.load(std::memory_order_acquire))
        {
            client->active_requests.fetch_sub(1, std::memory_order_acq_rel);
            return fail(request, "client is shutting down", CURL_BRIDGE_INVALID_ARGUMENT);
        }

        request->in_send.store(true, std::memory_order_release);
        request->body_started.store(false, std::memory_order_relaxed);
        request->current_block_status = 0;
        request->current_block_informational = false;

        curl_bridge_result result;
        curl_slist* extra_headers = nullptr;
        std::string acquire_error;
        CURL* handle = client->acquire_handle(acquire_error);
        if (handle == nullptr)
        {
            result = fail(request, std::move(acquire_error), CURL_BRIDGE_INTERNAL_ERROR);
        }
        else
        {
            result = configure_impl(handle, request, &extra_headers);
            if (result == CURL_BRIDGE_OK)
            {
                const CURLcode code = curl_easy_perform(handle);
                fill_info_impl(handle, code, info);

                const char* effective = nullptr;
                if (curl_easy_getinfo(handle, CURLINFO_EFFECTIVE_URL, &effective) == CURLE_OK &&
                    effective != nullptr &&
                    /* Only copy when redirects actually changed the URL — the
                     * common no-redirect case skips a std::string assignment
                     * and the managed side already knows the request URL. */
                    request->url != effective)
                {
                    request->effective_url = effective;
                }

                result = bridge::map_curl_code(code, request, info);
                if (result != CURL_BRIDGE_OK)
                {
                    request->last_error = bridge::describe_failure(
                        code, result, request->error_buffer);
                }
            }

            /* Detach the bridge-generated tail before freeing it so the
             * caller-owned header list stays intact. */
            if (extra_headers != nullptr)
            {
                if (request->headers != nullptr)
                {
                    curl_slist* tail = request->headers;
                    while (tail->next != nullptr && tail->next != extra_headers)
                    {
                        tail = tail->next;
                    }
                    tail->next = nullptr;
                }
                curl_slist_free_all(extra_headers);
            }
            client->release_handle(handle);
        }

        request->in_send.store(false, std::memory_order_release);
        client->active_requests.fetch_sub(1, std::memory_order_acq_rel);
        return result;
    }
    catch (const std::exception& ex)
    {
        request->in_send.store(false, std::memory_order_release);
        request->client->active_requests.fetch_sub(1, std::memory_order_acq_rel);
        /* Do not concatenate ex.what() into a new string here: if ex is
         * std::bad_alloc, that allocation would throw again and escape. */
        return fail_noexcept(request, ex.what(), CURL_BRIDGE_INTERNAL_ERROR);
    }
    catch (...)
    {
        request->in_send.store(false, std::memory_order_release);
        request->client->active_requests.fetch_sub(1, std::memory_order_acq_rel);
        return fail_noexcept(request, "send: unknown native exception",
                             CURL_BRIDGE_INTERNAL_ERROR);
    }
}

CURL_BRIDGE_API void CURL_BRIDGE_CALL
curl_bridge_request_cancel(curl_bridge_request* request)
{
    if (request != nullptr)
    {
        request->cancel_requested.store(true, std::memory_order_release);
    }
}

CURL_BRIDGE_API int32_t CURL_BRIDGE_CALL
curl_bridge_request_is_cancelled(const curl_bridge_request* request)
{
    return request != nullptr &&
           request->cancel_requested.load(std::memory_order_acquire) ? 1 : 0;
}

CURL_BRIDGE_API const char* CURL_BRIDGE_CALL
curl_bridge_request_get_last_error(const curl_bridge_request* request)
{
    return request != nullptr ? request->last_error.c_str() : "";
}

CURL_BRIDGE_API const char* CURL_BRIDGE_CALL
curl_bridge_request_get_effective_url(const curl_bridge_request* request)
{
    return request != nullptr ? request->effective_url.c_str() : "";
}

} /* extern "C" */
