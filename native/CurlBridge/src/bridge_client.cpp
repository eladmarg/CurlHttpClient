/* Client object: validated configuration, DNS/TLS-session share handle,
 * and the reusable easy-handle pool. */

#include "bridge_internal.h"

#include <memory>
#include <thread>

namespace
{
    void share_lock_cb(CURL*, curl_lock_data data, curl_lock_access, void* userptr)
    {
        auto* client = static_cast<curl_bridge_client*>(userptr);
        client->share_locks[data].lock();
    }

    void share_unlock_cb(CURL*, curl_lock_data data, void* userptr)
    {
        auto* client = static_cast<curl_bridge_client*>(userptr);
        client->share_locks[data].unlock();
    }

    bool copy_string(const char* src, std::string& dst)
    {
        if (src != nullptr)
        {
            dst = src;
            return true;
        }
        return false;
    }

    bool build_config(const curl_bridge_client_options& o, bridge::ClientConfig& c,
                      std::string& error)
    {
        if (o.struct_size < sizeof(curl_bridge_client_options))
        {
            error = "client_options.struct_size is smaller than expected; "
                    "managed/native struct mismatch";
            return false;
        }
        /* struct_size gates access to the appended ca_bundle_path field so an
         * older caller (smaller struct) still works. */
        if (o.struct_size >= offsetof(curl_bridge_client_options, ca_bundle_path) +
                sizeof(o.ca_bundle_path) &&
            o.ca_bundle_path != nullptr && o.ca_bundle_path[0] != '\0')
        {
            c.ca_bundle_path = o.ca_bundle_path;
        }
        if (o.ca_bundle_pem != nullptr && o.ca_bundle_pem_length > 0)
        {
            c.ca_bundle_pem.assign(o.ca_bundle_pem,
                                   o.ca_bundle_pem + o.ca_bundle_pem_length);
        }
        c.use_native_ca = o.use_native_ca != 0;
        if (c.ca_bundle_path.empty() && c.ca_bundle_pem.empty() && !c.use_native_ca)
        {
            error = "no trust source configured: a CA bundle or the native "
                    "certificate store is required (verification cannot be disabled)";
            return false;
        }
        switch (o.min_tls_version)
        {
        case CURL_BRIDGE_TLS_DEFAULT:
        case CURL_BRIDGE_TLS_1_2:
        case CURL_BRIDGE_TLS_1_3:
            c.min_tls_version = o.min_tls_version;
            break;
        default:
            error = "min_tls_version must be 0, 12 or 13";
            return false;
        }
        copy_string(o.tls12_cipher_list, c.tls12_cipher_list);
        copy_string(o.tls13_cipher_suites, c.tls13_cipher_suites);
        copy_string(o.client_cert_path, c.client_cert_path);
        copy_string(o.client_cert_type, c.client_cert_type);
        copy_string(o.client_key_path, c.client_key_path);
        copy_string(o.client_key_password, c.client_key_password);

        c.connect_timeout_ms = o.connect_timeout_ms;
        c.request_timeout_ms = o.request_timeout_ms;
        c.follow_redirects = o.follow_redirects != 0;
        c.max_redirects = o.max_redirects;
        c.enable_decompression = o.enable_decompression != 0;
        c.enable_http2 = o.enable_http2 != 0;
        c.enable_cookie_engine = o.enable_cookie_engine != 0;
        c.verbose = o.verbose != 0;
        if (o.buffer_size > 0)
        {
            c.buffer_size = o.buffer_size;
        }
        c.max_easy_handles = o.max_easy_handles;
        c.connection_idle_timeout_secs = o.connection_idle_timeout_secs;
        c.connection_max_lifetime_secs = o.connection_max_lifetime_secs;
        if (o.struct_size >= offsetof(curl_bridge_client_options, upload_buffer_size) +
                sizeof(o.upload_buffer_size) &&
            o.upload_buffer_size > 0)
        {
            c.upload_buffer_size = o.upload_buffer_size;
        }
        return true;
    }

    /* Applies the TLS options that accept free-form strings to a throwaway
     * easy handle so bad cipher configuration fails at handler construction,
     * not on the first request. */
    bool validate_tls_config(const bridge::ClientConfig& c, std::string& error)
    {
        CURL* h = curl_easy_init();
        if (h == nullptr)
        {
            error = "curl_easy_init failed during TLS validation";
            return false;
        }
        bool ok = true;
        if (!c.tls12_cipher_list.empty() &&
            curl_easy_setopt(h, CURLOPT_SSL_CIPHER_LIST,
                             c.tls12_cipher_list.c_str()) != CURLE_OK)
        {
            error = "TLS 1.2 cipher list rejected by libcurl";
            ok = false;
        }
        if (ok && !c.tls13_cipher_suites.empty() &&
            curl_easy_setopt(h, CURLOPT_TLS13_CIPHERS,
                             c.tls13_cipher_suites.c_str()) != CURLE_OK)
        {
            error = "TLS 1.3 cipher suites rejected by libcurl "
                    "(requires an OpenSSL 1.1.1+ backend)";
            ok = false;
        }
        curl_easy_cleanup(h);
        return ok;
    }
} // namespace

CURL* curl_bridge_client::acquire_handle(std::string& error)
{
    {
        std::lock_guard<std::mutex> guard(pool_mutex);
        if (!free_handles.empty())
        {
            CURL* handle = free_handles.back();
            free_handles.pop_back();
            return handle;
        }
    }

    CURL* handle = curl_easy_init();
    if (handle == nullptr)
    {
        error = "curl_easy_init failed";
        return nullptr;
    }
    /* The share binding survives curl_easy_reset, so it is applied once per
     * handle lifetime. */
    if (curl_easy_setopt(handle, CURLOPT_SHARE, share) != CURLE_OK)
    {
        curl_easy_cleanup(handle);
        error = "failed to bind easy handle to the client share";
        return nullptr;
    }
    {
        std::lock_guard<std::mutex> guard(pool_mutex);
        ++total_handles;
    }
    return handle;
}

void curl_bridge_client::release_handle(CURL* handle)
{
    if (handle == nullptr)
    {
        return;
    }
    if (config.enable_cookie_engine)
    {
        /* curl_easy_reset does NOT clear cookie data. Purge before reset so
         * no cookie ever leaks from one request (or tenant) to the next. */
        curl_easy_setopt(handle, CURLOPT_COOKIELIST, "ALL");
    }
    /* Reset returns every option to default but keeps live connections, the
     * DNS cache, TLS session cache and the share binding — exactly the state
     * we want to carry between requests. */
    curl_easy_reset(handle);

    if (shutting_down.load(std::memory_order_acquire))
    {
        curl_easy_cleanup(handle);
        return;
    }
    std::lock_guard<std::mutex> guard(pool_mutex);
    free_handles.push_back(handle);
}

extern "C" {

CURL_BRIDGE_API curl_bridge_client* CURL_BRIDGE_CALL
curl_bridge_client_create(const curl_bridge_client_options* options)
{
    try
    {
        if (options == nullptr)
        {
            bridge::set_last_global_error("client_create: options is NULL");
            return nullptr;
        }
        if (curl_bridge_global_initialize() != CURL_BRIDGE_OK)
        {
            /* last global error already set */
            return nullptr;
        }

        auto client = std::make_unique<curl_bridge_client>();
        std::string error;
        if (!build_config(*options, client->config, error) ||
            !validate_tls_config(client->config, error))
        {
            bridge::set_last_global_error(std::move(error));
            return nullptr;
        }

        if (client->config.enable_decompression)
        {
            /* Only advertise codecs this libcurl build can actually decode;
             * an unsupported alias in ACCEPT_ENCODING fails the transfer. */
            const curl_version_info_data* vi = curl_version_info(CURLVERSION_NOW);
            std::string encodings = "gzip, deflate";
            if (vi != nullptr && (vi->features & CURL_VERSION_BROTLI) != 0)
            {
                encodings += ", br";
            }
            client->config.accept_encoding = std::move(encodings);
        }

        client->share = curl_share_init();
        if (client->share == nullptr)
        {
            bridge::set_last_global_error("curl_share_init failed");
            return nullptr;
        }
        /* DNS results and TLS sessions are safe to share across threads with
         * lock callbacks. CURL_LOCK_DATA_CONNECT is deliberately absent:
         * libcurl does not support sharing connections between concurrent
         * threads; connection reuse comes from the per-handle caches. */
        curl_share_setopt(client->share, CURLSHOPT_USERDATA, client.get());
        curl_share_setopt(client->share, CURLSHOPT_LOCKFUNC, share_lock_cb);
        curl_share_setopt(client->share, CURLSHOPT_UNLOCKFUNC, share_unlock_cb);
        curl_share_setopt(client->share, CURLSHOPT_SHARE, CURL_LOCK_DATA_DNS);
        curl_share_setopt(client->share, CURLSHOPT_SHARE, CURL_LOCK_DATA_SSL_SESSION);

        /* Pre-create handles up to the hint so first requests skip init cost. */
        const int prealloc = client->config.max_easy_handles;
        for (int i = 0; i < prealloc; ++i)
        {
            std::string ignored;
            CURL* handle = client->acquire_handle(ignored);
            if (handle == nullptr)
            {
                break;
            }
            client->free_handles.push_back(handle);
        }

        return client.release();
    }
    catch (const std::exception& ex)
    {
        bridge::set_last_global_error(std::string("client_create: ") + ex.what());
        return nullptr;
    }
    catch (...)
    {
        bridge::set_last_global_error("client_create: unknown native exception");
        return nullptr;
    }
}

CURL_BRIDGE_API void CURL_BRIDGE_CALL
curl_bridge_client_destroy(curl_bridge_client* client)
{
    if (client == nullptr)
    {
        return;
    }
    try
    {
        client->shutting_down.store(true, std::memory_order_release);

        /* Backstop: the managed layer cancels and drains all requests before
         * destroying the client. Spin briefly for stragglers; handles owned
         * by an active request are cleaned up on release (shutting_down). */
        while (client->active_requests.load(std::memory_order_acquire) > 0)
        {
            std::this_thread::yield();
        }

        {
            std::lock_guard<std::mutex> guard(client->pool_mutex);
            for (CURL* handle : client->free_handles)
            {
                curl_easy_cleanup(handle);
            }
            client->free_handles.clear();
        }
        if (client->share != nullptr)
        {
            curl_share_cleanup(client->share);
            client->share = nullptr;
        }
        delete client;
    }
    catch (...)
    {
        /* Swallow: destroy must never throw across the ABI. The process is
         * usually shutting down when this could even happen. */
    }
}

} /* extern "C" */
