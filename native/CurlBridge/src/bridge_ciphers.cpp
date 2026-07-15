/* Exact-build cipher inventory, enumerated through the statically linked
 * OpenSSL's own API. This is the authoritative source for the cipher test
 * manifest: it can never drift from the binary actually shipped. */

#include "bridge_internal.h"

#include <openssl/obj_mac.h>
#include <openssl/opensslv.h>
#include <openssl/ssl.h>

#include <cstdio>
#include <cstring>
#include <set>
#include <string>

namespace
{
    const char* kx_name(int nid)
    {
        switch (nid)
        {
        case NID_kx_rsa:      return "RSA";
        case NID_kx_ecdhe:    return "ECDHE";
        case NID_kx_dhe:      return "DHE";
        case NID_kx_any:      return "any";       /* all TLS 1.3 suites */
        case NID_kx_psk:
        case NID_kx_ecdhe_psk:
        case NID_kx_dhe_psk:
        case NID_kx_rsa_psk:  return "PSK";
        case NID_kx_srp:      return "SRP";
        default:              return "other";
        }
    }

    const char* auth_name(int nid)
    {
        switch (nid)
        {
        case NID_auth_rsa:   return "RSA";
        case NID_auth_ecdsa: return "ECDSA";
        case NID_auth_any:   return "any";        /* all TLS 1.3 suites */
        case NID_auth_null:  return "NULL";
        case NID_auth_psk:   return "PSK";
        case NID_auth_srp:   return "SRP";
        case NID_auth_dss:   return "DSS";
        default:             return "other";
        }
    }

    void append_escaped(std::string& out, const char* value)
    {
        if (value == nullptr)
        {
            return;
        }
        for (const char* p = value; *p != '\0'; ++p)
        {
            const unsigned char c = static_cast<unsigned char>(*p);
            if (c == '"' || c == '\\')
            {
                out += '\\';
                out += static_cast<char>(c);
            }
            else if (c >= 0x20)
            {
                out += static_cast<char>(c);
            }
        }
    }

    constexpr const char* kAllTls13Suites =
        "TLS_AES_256_GCM_SHA384:TLS_CHACHA20_POLY1305_SHA256:"
        "TLS_AES_128_GCM_SHA256:TLS_AES_128_CCM_SHA256:TLS_AES_128_CCM_8_SHA256";

    /* Collects the cipher ids offerable by a ctx configured as given. */
    bool collect_ids(bool everything, std::set<uint32_t>* ids,
                     STACK_OF(SSL_CIPHER)** out_stack, SSL** out_ssl, SSL_CTX** out_ctx)
    {
        SSL_CTX* ctx = SSL_CTX_new(TLS_client_method());
        if (ctx == nullptr)
        {
            return false;
        }
        if (everything)
        {
            SSL_CTX_set_security_level(ctx, 0);
            if (SSL_CTX_set_cipher_list(ctx, "ALL:COMPLEMENTOFALL") != 1 ||
                SSL_CTX_set_ciphersuites(ctx, kAllTls13Suites) != 1)
            {
                SSL_CTX_free(ctx);
                return false;
            }
        }
        SSL* ssl = SSL_new(ctx);
        if (ssl == nullptr)
        {
            SSL_CTX_free(ctx);
            return false;
        }
        STACK_OF(SSL_CIPHER)* stack = SSL_get1_supported_ciphers(ssl);
        if (stack == nullptr)
        {
            SSL_free(ssl);
            SSL_CTX_free(ctx);
            return false;
        }
        if (ids != nullptr)
        {
            for (int i = 0; i < sk_SSL_CIPHER_num(stack); ++i)
            {
                ids->insert(SSL_CIPHER_get_id(sk_SSL_CIPHER_value(stack, i)));
            }
        }
        if (out_stack != nullptr)
        {
            *out_stack = stack;
            *out_ssl = ssl;
            *out_ctx = ctx;
        }
        else
        {
            sk_SSL_CIPHER_free(stack); /* stack only — ciphers are static tables */
            SSL_free(ssl);
            SSL_CTX_free(ctx);
        }
        return true;
    }
} // namespace

extern "C" {

CURL_BRIDGE_API size_t CURL_BRIDGE_CALL
curl_bridge_enumerate_ciphers(char* buffer, size_t buffer_length)
{
    try
    {
        /* Pass 1: ids enabled under the DEFAULT configuration (default
         * security level, default cipher list). */
        std::set<uint32_t> default_ids;
        if (!collect_ids(/*everything=*/false, &default_ids, nullptr, nullptr, nullptr))
        {
            if (buffer != nullptr && buffer_length > 0)
            {
                buffer[0] = '\0';
            }
            return 0;
        }

        /* Pass 2: the complete offerable inventory at seclevel 0. */
        STACK_OF(SSL_CIPHER)* stack = nullptr;
        SSL* ssl = nullptr;
        SSL_CTX* ctx = nullptr;
        if (!collect_ids(/*everything=*/true, nullptr, &stack, &ssl, &ctx))
        {
            if (buffer != nullptr && buffer_length > 0)
            {
                buffer[0] = '\0';
            }
            return 0;
        }

        std::string json = "{\"openssl_version\":\"";
        append_escaped(json, OpenSSL_version(OPENSSL_VERSION));
        char hex[32];
        std::snprintf(hex, sizeof(hex), "0x%08lX",
                      static_cast<unsigned long>(OPENSSL_VERSION_NUMBER));
        json += "\",\"openssl_version_hex\":\"";
        json += hex;
        json += "\",\"ciphers\":[";

        for (int i = 0; i < sk_SSL_CIPHER_num(stack); ++i)
        {
            const SSL_CIPHER* cipher = sk_SSL_CIPHER_value(stack, i);
            if (i > 0)
            {
                json += ',';
            }
            int alg_bits = 0;
            const int bits = SSL_CIPHER_get_bits(cipher, &alg_bits);
            json += "{\"name\":\"";
            append_escaped(json, SSL_CIPHER_get_name(cipher));
            json += "\",\"standard_name\":\"";
            append_escaped(json, SSL_CIPHER_standard_name(cipher));
            json += "\",\"protocol\":\"";
            append_escaped(json, SSL_CIPHER_get_version(cipher));
            json += "\",\"kx\":\"";
            json += kx_name(SSL_CIPHER_get_kx_nid(cipher));
            json += "\",\"auth\":\"";
            json += auth_name(SSL_CIPHER_get_auth_nid(cipher));
            json += "\",\"bits\":";
            json += std::to_string(bits);
            json += ",\"aead\":";
            json += SSL_CIPHER_is_aead(cipher) ? "true" : "false";
            json += ",\"enabled_default\":";
            json += default_ids.count(SSL_CIPHER_get_id(cipher)) != 0 ? "true" : "false";
            json += '}';
        }
        json += "]}";

        sk_SSL_CIPHER_free(stack);
        SSL_free(ssl);
        SSL_CTX_free(ctx);

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

} /* extern "C" */
