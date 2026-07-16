/*
 * curl_bridge.h — public C ABI of curl_http_bridge.dll
 *
 * A small, purpose-built bridge around libcurl (easy interface) for the
 * managed CurlHttpMessageHandler. Not a general libcurl wrapper.
 *
 * ABI rules:
 *  - Plain C, __cdecl, fixed-width types, opaque handles.
 *  - Strings crossing INTO the bridge are UTF-8, NUL-terminated, copied by
 *    the bridge before the call returns (caller may free immediately).
 *  - Strings returned by the bridge are owned by the object they were read
 *    from and remain valid until that object is destroyed.
 *  - Every *_create has a matching *_destroy; destroy(NULL) is a no-op.
 *  - No C++ exception ever crosses this boundary; failures surface as
 *    curl_bridge_result plus a per-request/per-call error message.
 *  - Structs passed in/out start with a uint32_t struct_size for versioning.
 *
 * Threading:
 *  - curl_bridge_global_initialize: call once before anything else (idempotent,
 *    thread-safe).
 *  - A client is thread-safe: many threads may create/send requests through it
 *    concurrently. Each blocking curl_bridge_request_send call runs on the
 *    caller's thread.
 *  - A request object must be driven by one thread (send), but
 *    curl_bridge_request_cancel may be called from any thread at any time
 *    between create and destroy.
 *  - Connection reuse comes from a pool of easy handles inside the client
 *    (per-handle connection caches survive reuse). DNS results and TLS
 *    sessions are shared client-wide via a curl share handle. Connections
 *    themselves are deliberately NOT shared across concurrent transfers —
 *    libcurl does not support that outside the multi interface.
 */

#ifndef CURL_BRIDGE_H
#define CURL_BRIDGE_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#  if defined(CURL_BRIDGE_EXPORTS)
#    define CURL_BRIDGE_API __declspec(dllexport)
#  else
#    define CURL_BRIDGE_API __declspec(dllimport)
#  endif
#  define CURL_BRIDGE_CALL __cdecl
#else
#  define CURL_BRIDGE_API __attribute__((visibility("default")))
#  define CURL_BRIDGE_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* ------------------------------------------------------------------ */
/* Opaque handles                                                      */
/* ------------------------------------------------------------------ */

typedef struct curl_bridge_client curl_bridge_client;
typedef struct curl_bridge_request curl_bridge_request;

/* ------------------------------------------------------------------ */
/* Result codes                                                        */
/* ------------------------------------------------------------------ */

typedef enum curl_bridge_result
{
    CURL_BRIDGE_OK                 = 0,
    CURL_BRIDGE_CANCELLED          = 1,  /* cancel flag observed */
    CURL_BRIDGE_TIMEOUT            = 2,  /* total request timeout elapsed */
    CURL_BRIDGE_CONNECT_TIMEOUT    = 3,  /* timed out before a connection was established */
    CURL_BRIDGE_TLS_ERROR          = 4,  /* TLS handshake / engine failure */
    CURL_BRIDGE_CERT_ERROR         = 5,  /* peer certificate or hostname verification failed */
    CURL_BRIDGE_DNS_ERROR          = 6,  /* name resolution failed */
    CURL_BRIDGE_CONNECT_ERROR      = 7,  /* TCP/proxy connect failed */
    CURL_BRIDGE_NETWORK_ERROR      = 8,  /* send/recv failure mid-transfer */
    CURL_BRIDGE_PROTOCOL_ERROR     = 9,  /* malformed / unexpected HTTP data */
    CURL_BRIDGE_TOO_MANY_REDIRECTS = 10,
    CURL_BRIDGE_CALLBACK_ERROR     = 11, /* a managed callback reported failure */
    CURL_BRIDGE_INVALID_ARGUMENT   = 12,
    CURL_BRIDGE_UNSUPPORTED        = 13, /* build lacks a required feature (e.g. OpenSSL backend) */
    CURL_BRIDGE_INTERNAL_ERROR     = 100
} curl_bridge_result;

/* ------------------------------------------------------------------ */
/* Callbacks (implemented in managed code, invoked on the thread that   */
/* is blocked inside curl_bridge_request_send)                          */
/* ------------------------------------------------------------------ */

/* Header line flags forwarded with each header callback invocation. */
#define CURL_BRIDGE_HEADER_STATUS_LINE   0x1u /* line begins a response block (HTTP/... status line) */
#define CURL_BRIDGE_HEADER_INFORMATIONAL 0x2u /* line belongs to a 1xx block */
#define CURL_BRIDGE_HEADER_TRAILER       0x4u /* line arrived after the first body byte (trailer) */
#define CURL_BRIDGE_HEADER_BLOCK_END     0x8u /* line is the blank CRLF terminating a block */

/* Return 0 to continue, non-zero to abort the transfer. */
typedef int32_t (CURL_BRIDGE_CALL *curl_bridge_write_callback)(
    void* context,
    const uint8_t* data,
    size_t data_length);

/* Return 0 to continue, non-zero to abort the transfer.
 * `line` includes the trailing CRLF exactly as received. `status_code` is the
 * status of the block the line belongs to (0 while unknown). */
typedef int32_t (CURL_BRIDGE_CALL *curl_bridge_header_callback)(
    void* context,
    const uint8_t* line,
    size_t line_length,
    uint32_t flags,
    int32_t status_code);

/* Return >0 = bytes produced, 0 = end of body, -1 = abort the transfer. */
typedef int64_t (CURL_BRIDGE_CALL *curl_bridge_read_callback)(
    void* context,
    uint8_t* destination,
    size_t destination_length);

/* origin: 0 = SEEK_SET (only value libcurl uses).
 * Return 0 = ok, 1 = cannot seek (libcurl may work around), 2 = fail. */
typedef int32_t (CURL_BRIDGE_CALL *curl_bridge_seek_callback)(
    void* context,
    int64_t offset,
    int32_t origin);

/* Verbose diagnostics. `kind` mirrors curl_infotype but only TEXT(0),
 * HEADER_IN(1), HEADER_OUT(2) are ever forwarded; body and TLS payload
 * types are dropped inside the bridge. */
typedef void (CURL_BRIDGE_CALL *curl_bridge_debug_callback)(
    void* context,
    int32_t kind,
    const char* data,
    size_t data_length);

typedef struct curl_bridge_callbacks
{
    uint32_t struct_size;
    void* context;
    curl_bridge_write_callback  on_body_data;
    curl_bridge_header_callback on_header_line;
    curl_bridge_read_callback   on_read_body;   /* NULL when the request has no body */
    curl_bridge_seek_callback   on_seek_body;   /* NULL when the body is not seekable */
    curl_bridge_debug_callback  on_debug;       /* NULL unless verbose logging enabled */
} curl_bridge_callbacks;

/* ------------------------------------------------------------------ */
/* Client options (handler-lifetime configuration)                     */
/* ------------------------------------------------------------------ */

/* min_tls_version values */
#define CURL_BRIDGE_TLS_DEFAULT 0
#define CURL_BRIDGE_TLS_1_2     12
#define CURL_BRIDGE_TLS_1_3     13

typedef struct curl_bridge_client_options
{
    uint32_t struct_size;

    /* TLS. Peer and hostname verification are ALWAYS enforced; there is
     * deliberately no switch to disable them. */
    const uint8_t* ca_bundle_pem;      /* PEM blob; copied. NULL => use native CA only if use_native_ca */
    uint64_t ca_bundle_pem_length;
    int32_t use_native_ca;             /* also trust the Windows certificate store (CURLSSLOPT_NATIVE_CA) */
    int32_t min_tls_version;           /* CURL_BRIDGE_TLS_* */
    const char* tls12_cipher_list;     /* optional OpenSSL cipher list */
    const char* tls13_cipher_suites;   /* optional TLS 1.3 cipher suites */
    const char* client_cert_path;      /* optional client certificate (PEM or PKCS#12 file) */
    const char* client_cert_type;      /* "PEM" or "P12"; NULL => PEM */
    const char* client_key_path;       /* optional (PEM cert with separate key) */
    const char* client_key_password;   /* optional */

    /* Timeouts / transfer behaviour defaults. */
    int64_t connect_timeout_ms;        /* 0 => libcurl default */
    int64_t request_timeout_ms;        /* 0 => no total timeout */
    int32_t follow_redirects;
    int32_t max_redirects;
    int32_t enable_decompression;      /* advertise+decode gzip/br(/zlib) */
    int32_t enable_http2;              /* negotiate h2 via ALPN (no multiplexing across threads) */
    int32_t enable_cookie_engine;      /* per-request-chain cookie engine (scrubbed between requests) */
    int32_t verbose;                   /* forward curl verbose TEXT/HEADER lines to on_debug */
    int32_t buffer_size;               /* CURLOPT_BUFFERSIZE; 0 => bridge default (256 KiB) */

    /* Connection pool tuning. */
    int32_t max_easy_handles;          /* soft pre-allocation hint; pool grows on demand */
    int64_t connection_idle_timeout_secs;   /* 0 => libcurl default (118 s) */
    int64_t connection_max_lifetime_secs;   /* 0 => unlimited */

    /* CA bundle FILE path. Strongly preferred over ca_bundle_pem: passing a
     * path lets OpenSSL cache the parsed X509 store across connections
     * (CURLOPT_CA_CACHE_TIMEOUT, 24 h), whereas CURLOPT_CAINFO_BLOB disables
     * that cache and re-parses the bundle on every new TLS handshake. When
     * set (non-NULL, non-empty), the path is used and ca_bundle_pem ignored. */
    const char* ca_bundle_path;

    /* CURLOPT_UPLOAD_BUFFERSIZE (bytes). 0 => libcurl default (64 KiB). A
     * larger buffer means fewer read-callback round trips on big uploads.
     * Range enforced by libcurl: 16 KiB..2 MiB. */
    int32_t upload_buffer_size;
} curl_bridge_client_options;

/* ------------------------------------------------------------------ */
/* Response metadata (filled by curl_bridge_request_send)              */
/* ------------------------------------------------------------------ */

typedef struct curl_bridge_response_info
{
    uint32_t struct_size;
    int32_t status_code;          /* final HTTP status */
    int32_t http_version;         /* 10, 11, 20, 30 (CURLINFO_HTTP_VERSION) */
    int32_t curl_error_code;      /* raw CURLcode for diagnostics */
    int32_t num_connects;         /* CURLINFO_NUM_CONNECTS: 0 => connection was reused */
    int32_t redirect_count;       /* CURLINFO_REDIRECT_COUNT */
    int32_t reserved0;
    int64_t namelookup_time_us;   /* CURLINFO_NAMELOOKUP_TIME_T */
    int64_t connect_time_us;      /* CURLINFO_CONNECT_TIME_T (0 => never connected) */
    int64_t appconnect_time_us;   /* CURLINFO_APPCONNECT_TIME_T (TLS done) */
    int64_t starttransfer_time_us;
    int64_t total_time_us;
    int64_t content_length;       /* CURLINFO_CONTENT_LENGTH_DOWNLOAD_T; -1 unknown */
} curl_bridge_response_info;

/* ------------------------------------------------------------------ */
/* Global                                                              */
/* ------------------------------------------------------------------ */

/* Idempotent. Also validates the linked libcurl: fails with
 * CURL_BRIDGE_UNSUPPORTED unless the TLS backend is OpenSSL and the
 * threaded resolver (AsynchDNS) is present. */
CURL_BRIDGE_API curl_bridge_result CURL_BRIDGE_CALL
curl_bridge_global_initialize(void);

/* Writes a JSON document describing the linked libcurl build:
 * {"bridge_version":..,"curl_version":..,"ssl_version":..,"features":[..],
 *  "protocols":[..]}.
 * Returns the number of bytes (excluding NUL) that were or would be written;
 * output is always NUL-terminated when buffer_length > 0. */
CURL_BRIDGE_API size_t CURL_BRIDGE_CALL
curl_bridge_get_version_info(char* buffer, size_t buffer_length);

/* Enumerates every TLS cipher suite the STATICALLY LINKED OpenSSL build can
 * offer for client use, evaluated at security level 0 with
 * "ALL:COMPLEMENTOFALL" plus all five TLS 1.3 suites — i.e. the complete
 * client-offerable inventory of this exact binary. PSK/SRP suites are
 * filtered by OpenSSL itself (not client-offerable without PSK/SRP state);
 * RC4/3DES are compiled out (OPENSSL_NO_WEAK_SSL_CIPHERS).
 *
 * JSON shape:
 * {"openssl_version":"...","openssl_version_hex":"0x...",
 *  "ciphers":[{"name":..,"standard_name":..,"protocol":..,"kx":..,
 *              "auth":..,"bits":N,"aead":bool,"enabled_default":bool}]}
 *
 * Buffer contract identical to curl_bridge_get_version_info. */
CURL_BRIDGE_API size_t CURL_BRIDGE_CALL
curl_bridge_enumerate_ciphers(char* buffer, size_t buffer_length);

/* ------------------------------------------------------------------ */
/* Client                                                              */
/* ------------------------------------------------------------------ */

/* Returns NULL on failure; call curl_bridge_get_last_global_error for the
 * reason. Options are validated eagerly (including TLS cipher strings, via a
 * dry configuration of an easy handle). */
CURL_BRIDGE_API curl_bridge_client* CURL_BRIDGE_CALL
curl_bridge_client_create(const curl_bridge_client_options* options);

/* Blocks until no request is actively using the client, then frees the easy
 * handle pool and the share handle. All requests must be destroyed first in
 * normal operation; this is a backstop. */
CURL_BRIDGE_API void CURL_BRIDGE_CALL
curl_bridge_client_destroy(curl_bridge_client* client);

/* Message describing the most recent client-create/global failure on the
 * calling thread. Valid until the next failing call on the same thread. */
CURL_BRIDGE_API const char* CURL_BRIDGE_CALL
curl_bridge_get_last_global_error(void);

/* ------------------------------------------------------------------ */
/* Request                                                             */
/* ------------------------------------------------------------------ */

CURL_BRIDGE_API curl_bridge_request* CURL_BRIDGE_CALL
curl_bridge_request_create(curl_bridge_client* client);

/* Must not be called while curl_bridge_request_send is executing. */
CURL_BRIDGE_API void CURL_BRIDGE_CALL
curl_bridge_request_destroy(curl_bridge_request* request);

CURL_BRIDGE_API curl_bridge_result CURL_BRIDGE_CALL
curl_bridge_request_set_method(curl_bridge_request* request, const char* method_utf8);

CURL_BRIDGE_API curl_bridge_result CURL_BRIDGE_CALL
curl_bridge_request_set_url(curl_bridge_request* request, const char* url_utf8);

/* header_utf8 is a full "Name: value" line without CRLF. To send a header
 * with an empty value use "Name;" (libcurl convention, applied by the
 * bridge automatically when value is empty). To suppress a header libcurl
 * would add on its own, pass "Name:". */
CURL_BRIDGE_API curl_bridge_result CURL_BRIDGE_CALL
curl_bridge_request_add_header(curl_bridge_request* request, const char* header_utf8);

/* content_length: >=0 known size, -1 streaming with unknown size (chunked on
 * HTTP/1.1). Only meaningful when callbacks.on_read_body is set. */
CURL_BRIDGE_API curl_bridge_result CURL_BRIDGE_CALL
curl_bridge_request_set_body(curl_bridge_request* request, int64_t content_length);

/* Overrides the client-level total request timeout for this request.
 * 0 => no total timeout. Negative => inherit client default. */
CURL_BRIDGE_API curl_bridge_result CURL_BRIDGE_CALL
curl_bridge_request_set_timeout(curl_bridge_request* request, int64_t request_timeout_ms);

/* proxy_utf8: "" disables any proxy INCLUDING environment-variable lookup;
 * otherwise a curl proxy URL (http://host:port, socks5://...). Must always be
 * called — the bridge refuses to send otherwise, so proxy behaviour is never
 * silently inherited from the environment. userpwd_utf8 optional ("user:pass"). */
CURL_BRIDGE_API curl_bridge_result CURL_BRIDGE_CALL
curl_bridge_request_set_proxy(curl_bridge_request* request,
                              const char* proxy_utf8,
                              const char* userpwd_utf8);

/* Restricts protocols redirects may switch to, e.g. "https" for https
 * origins (blocks downgrade) or "http,https" for http origins. */
CURL_BRIDGE_API curl_bridge_result CURL_BRIDGE_CALL
curl_bridge_request_set_redirect_protocols(curl_bridge_request* request,
                                           const char* protocols_utf8);

CURL_BRIDGE_API curl_bridge_result CURL_BRIDGE_CALL
curl_bridge_request_set_callbacks(curl_bridge_request* request,
                                  const curl_bridge_callbacks* callbacks);

/* Blocking; runs the transfer on the calling thread using a pooled easy
 * handle from the request's client. Returns the mapped result and fills
 * `info` (also on failure, with whatever is known). */
CURL_BRIDGE_API curl_bridge_result CURL_BRIDGE_CALL
curl_bridge_request_send(curl_bridge_request* request,
                         curl_bridge_response_info* info);

/* Thread-safe, idempotent. Interrupts an in-flight send promptly (the
 * transfer aborts from the progress callback within ~1 s, or immediately
 * when a managed callback observes the cancellation). */
CURL_BRIDGE_API void CURL_BRIDGE_CALL
curl_bridge_request_cancel(curl_bridge_request* request);

/* 1 if cancel was requested. */
CURL_BRIDGE_API int32_t CURL_BRIDGE_CALL
curl_bridge_request_is_cancelled(const curl_bridge_request* request);

/* Human-readable failure detail for the last send on this request (libcurl
 * error buffer + bridge annotations). Valid until request destroy. */
CURL_BRIDGE_API const char* CURL_BRIDGE_CALL
curl_bridge_request_get_last_error(const curl_bridge_request* request);

/* Final effective URL after redirects. Valid until request destroy. */
CURL_BRIDGE_API const char* CURL_BRIDGE_CALL
curl_bridge_request_get_effective_url(const curl_bridge_request* request);

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* CURL_BRIDGE_H */
