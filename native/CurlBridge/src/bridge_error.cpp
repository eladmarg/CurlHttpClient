/* CURLcode -> stable bridge result mapping plus failure descriptions.
 * The managed CurlErrorMapper turns bridge results into .NET exceptions;
 * this file owns the native half of that contract. */

#include "bridge_internal.h"

namespace bridge
{

curl_bridge_result map_curl_code(CURLcode code,
                                 const curl_bridge_request* request,
                                 const curl_bridge_response_info* info)
{
    const bool cancelled =
        request != nullptr &&
        request->cancel_requested.load(std::memory_order_acquire);

    switch (code)
    {
    case CURLE_OK:
        return CURL_BRIDGE_OK;

    /* Abort paths. Managed callbacks abort by returning an error indicator;
     * whether that was a cancellation or a genuine callback failure is
     * decided by the cancel flag. */
    case CURLE_ABORTED_BY_CALLBACK:
    case CURLE_WRITE_ERROR:
    case CURLE_READ_ERROR:
        return cancelled ? CURL_BRIDGE_CANCELLED : CURL_BRIDGE_CALLBACK_ERROR;

    case CURLE_OPERATION_TIMEDOUT:
        if (cancelled)
        {
            /* Cancellation raced the timeout; the caller's intent wins. */
            return CURL_BRIDGE_CANCELLED;
        }
        /* connect_time == 0 means no TCP connection was ever established, so
         * the connect timeout (not the total timeout) is what expired. */
        if (info != nullptr && info->connect_time_us == 0)
        {
            return CURL_BRIDGE_CONNECT_TIMEOUT;
        }
        return CURL_BRIDGE_TIMEOUT;

    case CURLE_COULDNT_RESOLVE_HOST:
    case CURLE_COULDNT_RESOLVE_PROXY:
        return CURL_BRIDGE_DNS_ERROR;

    case CURLE_COULDNT_CONNECT:
    case CURLE_INTERFACE_FAILED:
        return CURL_BRIDGE_CONNECT_ERROR;

    case CURLE_PEER_FAILED_VERIFICATION: /* includes hostname mismatch */
    case CURLE_SSL_CERTPROBLEM:          /* problem with the CLIENT cert */
    case CURLE_SSL_INVALIDCERTSTATUS:
    case CURLE_SSL_ISSUER_ERROR:
    case CURLE_SSL_PINNEDPUBKEYNOTMATCH:
        return CURL_BRIDGE_CERT_ERROR;

    case CURLE_SSL_CONNECT_ERROR:
    case CURLE_SSL_ENGINE_NOTFOUND:
    case CURLE_SSL_ENGINE_SETFAILED:
    case CURLE_SSL_ENGINE_INITFAILED:
    case CURLE_SSL_CIPHER:
    case CURLE_USE_SSL_FAILED:
    case CURLE_SSL_CACERT_BADFILE:
    case CURLE_SSL_CRL_BADFILE:
    case CURLE_SSL_CLIENTCERT:
        return CURL_BRIDGE_TLS_ERROR;

    case CURLE_TOO_MANY_REDIRECTS:
        return CURL_BRIDGE_TOO_MANY_REDIRECTS;

    case CURLE_SEND_ERROR:
    case CURLE_RECV_ERROR:
    case CURLE_PARTIAL_FILE:
    case CURLE_GOT_NOTHING:
        return CURL_BRIDGE_NETWORK_ERROR;

    case CURLE_WEIRD_SERVER_REPLY:
    case CURLE_HTTP2:
    case CURLE_HTTP2_STREAM:
    case CURLE_BAD_CONTENT_ENCODING:
    case CURLE_RANGE_ERROR:
    case CURLE_HTTP_RETURNED_ERROR: /* not used (no CURLOPT_FAILONERROR), kept for completeness */
        return CURL_BRIDGE_PROTOCOL_ERROR;

    case CURLE_URL_MALFORMAT:
    case CURLE_UNSUPPORTED_PROTOCOL:
    case CURLE_BAD_FUNCTION_ARGUMENT:
    case CURLE_LOGIN_DENIED:
        return CURL_BRIDGE_INVALID_ARGUMENT;

    case CURLE_OUT_OF_MEMORY:
    case CURLE_FAILED_INIT:
    default:
        return CURL_BRIDGE_INTERNAL_ERROR;
    }
}

std::string describe_failure(CURLcode code,
                             curl_bridge_result mapped,
                             const char* error_buffer)
{
    std::string message = "libcurl error ";
    message += std::to_string(static_cast<int>(code));
    message += " (";
    message += curl_easy_strerror(code);
    message += ")";
    if (error_buffer != nullptr && error_buffer[0] != '\0')
    {
        message += ": ";
        message += error_buffer;
    }
    switch (mapped)
    {
    case CURL_BRIDGE_CONNECT_TIMEOUT:
        message += " [no connection was established before the connect timeout]";
        break;
    case CURL_BRIDGE_CERT_ERROR:
        message += " [server certificate or hostname verification failed; "
                   "verification is always enforced by this handler]";
        break;
    default:
        break;
    }
    return message;
}

} // namespace bridge
