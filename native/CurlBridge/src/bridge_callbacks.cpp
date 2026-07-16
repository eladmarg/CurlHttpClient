/* libcurl-facing callbacks. Each forwards to the managed function pointers
 * carried by the request, translating libcurl's conventions into the small
 * stable contract of curl_bridge.h. All of these run on the thread that is
 * blocked inside curl_bridge_request_send.
 *
 * None of these may let an exception escape: they are called from inside
 * libcurl (C code). Managed callbacks are [UnmanagedCallersOnly] and cannot
 * throw across the boundary either, so only defensive catch(...) remains. */

#include "bridge_internal.h"

#include <cctype>
#include <cstring>

namespace
{
    bool is_status_line(const char* data, size_t length)
    {
        return length >= 5 && std::strncmp(data, "HTTP/", 5) == 0;
    }

    bool is_blank_line(const char* data, size_t length)
    {
        return (length == 2 && data[0] == '\r' && data[1] == '\n') ||
               (length == 1 && data[0] == '\n');
    }

    /* "HTTP/1.1 200 OK" / "HTTP/2 200" -> 200; 0 when unparsable. */
    int parse_status_code(const char* data, size_t length)
    {
        size_t i = 5; /* past "HTTP/" */
        while (i < length && data[i] != ' ')
        {
            ++i;
        }
        while (i < length && data[i] == ' ')
        {
            ++i;
        }
        int status = 0;
        int digits = 0;
        while (i < length && std::isdigit(static_cast<unsigned char>(data[i])) && digits < 3)
        {
            status = status * 10 + (data[i] - '0');
            ++i;
            ++digits;
        }
        return digits == 3 ? status : 0;
    }
} // namespace

namespace bridge
{

size_t curl_write_cb(char* ptr, size_t size, size_t nmemb, void* userdata)
{
    auto* request = static_cast<curl_bridge_request*>(userdata);
    const size_t total = size * nmemb;
    try
    {
        request->body_started.store(true, std::memory_order_relaxed);
        if (request->cancel_requested.load(std::memory_order_acquire))
        {
            return CURL_WRITEFUNC_ERROR;
        }
        if (request->callbacks.on_body_data == nullptr)
        {
            return CURL_WRITEFUNC_ERROR;
        }
        const int32_t rc = request->callbacks.on_body_data(
            request->callbacks.context,
            reinterpret_cast<const uint8_t*>(ptr),
            total);
        switch (rc)
        {
        case CURL_BRIDGE_CB_OK:    return total;
        case CURL_BRIDGE_CB_PAUSE: return CURL_WRITEFUNC_PAUSE; /* event-loop backpressure */
        default:                   return CURL_WRITEFUNC_ERROR;
        }
    }
    catch (...)
    {
        return CURL_WRITEFUNC_ERROR;
    }
}

size_t curl_header_cb(char* ptr, size_t size, size_t nmemb, void* userdata)
{
    auto* request = static_cast<curl_bridge_request*>(userdata);
    const size_t total = size * nmemb;
    try
    {
        if (request->cancel_requested.load(std::memory_order_acquire))
        {
            return 0; /* abort */
        }

        uint32_t flags = 0;
        if (is_status_line(ptr, total))
        {
            request->current_block_status = parse_status_code(ptr, total);
            request->current_block_informational =
                request->current_block_status >= 100 &&
                request->current_block_status < 200;
            flags |= CURL_BRIDGE_HEADER_STATUS_LINE;
        }
        else if (is_blank_line(ptr, total))
        {
            flags |= CURL_BRIDGE_HEADER_BLOCK_END;
        }
        if (request->current_block_informational)
        {
            flags |= CURL_BRIDGE_HEADER_INFORMATIONAL;
        }
        if (request->body_started.load(std::memory_order_relaxed))
        {
            flags |= CURL_BRIDGE_HEADER_TRAILER;
        }

        if (request->callbacks.on_header_line == nullptr)
        {
            return 0;
        }
        const int32_t rc = request->callbacks.on_header_line(
            request->callbacks.context,
            reinterpret_cast<const uint8_t*>(ptr),
            total,
            flags,
            request->current_block_status);
        return rc == 0 ? total : 0;
    }
    catch (...)
    {
        return 0;
    }
}

size_t curl_read_cb(char* buffer, size_t size, size_t nitems, void* userdata)
{
    auto* request = static_cast<curl_bridge_request*>(userdata);
    try
    {
        if (request->cancel_requested.load(std::memory_order_acquire))
        {
            return CURL_READFUNC_ABORT;
        }
        if (request->callbacks.on_read_body == nullptr)
        {
            return 0; /* no body: immediate EOF */
        }
        const int64_t produced = request->callbacks.on_read_body(
            request->callbacks.context,
            reinterpret_cast<uint8_t*>(buffer),
            size * nitems);
        if (produced == CURL_BRIDGE_READ_PAUSE)
        {
            return CURL_READFUNC_PAUSE; /* event-loop: no upload bytes yet */
        }
        if (produced < 0)
        {
            return CURL_READFUNC_ABORT;
        }
        /* Defense-in-depth: a managed callback must never claim to have written
         * more than the buffer holds. libcurl also rejects this, but do not
         * depend on that — an out-of-bounds size would corrupt the upload. */
        if (produced > static_cast<int64_t>(size * nitems))
        {
            return CURL_READFUNC_ABORT;
        }
        return static_cast<size_t>(produced);
    }
    catch (...)
    {
        return CURL_READFUNC_ABORT;
    }
}

int curl_seek_cb(void* userdata, curl_off_t offset, int origin)
{
    auto* request = static_cast<curl_bridge_request*>(userdata);
    try
    {
        if (request->callbacks.on_seek_body == nullptr)
        {
            return CURL_SEEKFUNC_CANTSEEK;
        }
        switch (request->callbacks.on_seek_body(
            request->callbacks.context,
            static_cast<int64_t>(offset),
            origin))
        {
        case 0:  return CURL_SEEKFUNC_OK;
        case 1:  return CURL_SEEKFUNC_CANTSEEK;
        default: return CURL_SEEKFUNC_FAIL;
        }
    }
    catch (...)
    {
        return CURL_SEEKFUNC_FAIL;
    }
}

int curl_xferinfo_cb(void* userdata, curl_off_t, curl_off_t, curl_off_t, curl_off_t)
{
    auto* request = static_cast<curl_bridge_request*>(userdata);
    /* Non-zero aborts the transfer with CURLE_ABORTED_BY_CALLBACK. This is
     * the fallback cancellation path for phases where no data callback runs
     * (connecting, TLS handshake, awaiting first byte); libcurl invokes it
     * roughly once per second in those phases. */
    return request->cancel_requested.load(std::memory_order_acquire) ? 1 : 0;
}

int curl_debug_cb(CURL*, curl_infotype type, char* data, size_t size, void* userdata)
{
    auto* request = static_cast<curl_bridge_request*>(userdata);
    try
    {
        /* Bodies and raw TLS payloads never leave the bridge: they are both
         * hot-path expensive and sensitive. Header redaction happens in the
         * managed logger, which owns the redaction policy. */
        int32_t kind;
        switch (type)
        {
        case CURLINFO_TEXT:       kind = 0; break;
        case CURLINFO_HEADER_IN:  kind = 1; break;
        case CURLINFO_HEADER_OUT: kind = 2; break;
        default:
            return 0;
        }
        if (request->callbacks.on_debug != nullptr)
        {
            request->callbacks.on_debug(request->callbacks.context, kind, data, size);
        }
        return 0;
    }
    catch (...)
    {
        return 0;
    }
}

} // namespace bridge
