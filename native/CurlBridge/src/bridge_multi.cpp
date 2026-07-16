/* Event-loop (curl_multi) engine: one dedicated native thread drives all
 * transfers through a single curl_multi handle. Benefits over the blocking
 * worker-pool engine: connections are shared across ALL requests (one pool,
 * not one per handle), HTTP/2 streams multiplex onto one connection,
 * cancellation is instant (curl_multi_wakeup, not the ~1 s progress cadence),
 * and there is no dedicated thread per in-flight request.
 *
 * Threading contract:
 *  - Everything that touches the multi handle or an easy handle runs on the
 *    loop thread. Other threads only enqueue commands + curl_multi_wakeup.
 *  - Callbacks (write/read/header/xferinfo) run on the loop thread and MUST
 *    NOT block: the write callback returns CURL_WRITEFUNC_PAUSE when the
 *    managed buffer is full; the read callback returns CURL_READFUNC_PAUSE
 *    when no upload bytes are ready. The consumer/pump later requests unpause.
 *  - The managed layer owns each request object and destroys it only after its
 *    on_complete callback has fired.
 */

#include "bridge_internal.h"

#include <deque>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_set>

namespace
{
    enum class CommandType
    {
        Submit,
        Cancel,
        UnpauseWrite,
        UnpauseRead,
        Shutdown,
    };

    struct Command
    {
        CommandType type;
        curl_bridge_request* request; /* null for Shutdown */
    };
} // namespace

struct curl_bridge_multi_client
{
    curl_bridge_client* base = nullptr; /* owns config, easy-handle pool, share */
    CURLM* multi = nullptr;

    std::mutex cmd_mutex;
    std::deque<Command> commands;

    std::thread loop_thread;
    std::atomic<bool> running{false};
    /* Cleared by multi_destroy before it enqueues Shutdown: once false, a
     * submit that has not yet been accepted by the loop is completed with
     * CANCELLED rather than being silently dropped. */
    std::atomic<bool> accepting{true};

    /* Loop-thread-only: requests currently added to the multi. */
    std::unordered_set<curl_bridge_request*> active;

    void enqueue(CommandType type, curl_bridge_request* request)
    {
        {
            std::lock_guard<std::mutex> guard(cmd_mutex);
            commands.push_back(Command{type, request});
        }
        if (multi != nullptr)
        {
            curl_multi_wakeup(multi);
        }
    }

    void run();
    void process_commands();
    void start_request(curl_bridge_request* request);
    void finish_request(curl_bridge_request* request, CURLcode code);
};

namespace
{
    void complete_error(curl_bridge_request* request, curl_bridge_result result)
    {
        if (request->callbacks.on_complete != nullptr)
        {
            curl_bridge_response_info info = {};
            info.struct_size = sizeof(info);
            info.content_length = -1;
            request->callbacks.on_complete(
                request->callbacks.context, result, &info);
        }
    }
} // namespace

void curl_bridge_multi_client::start_request(curl_bridge_request* request)
{
    if (request->cancel_requested.load(std::memory_order_acquire))
    {
        complete_error(request, CURL_BRIDGE_CANCELLED);
        return;
    }

    std::string error;
    CURL* handle = base->acquire_handle(error);
    if (handle == nullptr)
    {
        request->last_error = std::move(error);
        complete_error(request, CURL_BRIDGE_INTERNAL_ERROR);
        return;
    }
    request->easy = handle;
    request->body_started.store(false, std::memory_order_relaxed);
    request->current_block_status = 0;
    request->current_block_informational = false;

    curl_slist* extra_headers = nullptr;
    const curl_bridge_result configured = bridge::configure(handle, request, &extra_headers);
    request->extra_headers = extra_headers;
    if (configured != CURL_BRIDGE_OK)
    {
        finish_request(request, CURLE_FAILED_INIT);
        return;
    }
    curl_easy_setopt(handle, CURLOPT_PRIVATE, request);

    /* Insert into `active` only after the handle is actually added to the
     * multi, so a failed add does not leave a phantom entry and finish_request
     * does not call curl_multi_remove_handle on a never-added easy. */
    const CURLMcode added = curl_multi_add_handle(multi, handle);
    if (added != CURLM_OK)
    {
        request->last_error = curl_multi_strerror(added);
        finish_request(request, CURLE_FAILED_INIT);
        return;
    }
    active.insert(request);
}

void curl_bridge_multi_client::finish_request(curl_bridge_request* request, CURLcode code)
{
    /* Idempotent: a request can be reached by both a Cancel command and a
     * CURLMSG_DONE. The `finished` flag guards the whole body — critically the
     * on_complete callback — because after the first finish the managed layer
     * frees the request's GCHandle and destroys the request object. */
    if (request->finished)
    {
        return;
    }
    request->finished = true;
    const bool was_active = active.erase(request) != 0;

    curl_bridge_response_info info = {};
    info.struct_size = sizeof(info);
    info.content_length = -1;
    if (request->easy != nullptr)
    {
        bridge::fill_info(request->easy, code, &info);

        const char* effective = nullptr;
        if (curl_easy_getinfo(request->easy, CURLINFO_EFFECTIVE_URL, &effective) == CURLE_OK &&
            effective != nullptr && request->url != effective)
        {
            request->effective_url = effective;
        }
    }

    const curl_bridge_result result = bridge::map_curl_code(code, request, &info);
    if (result != CURL_BRIDGE_OK && request->last_error.empty())
    {
        /* Preserve a specific message set by configure/start (e.g. a
         * setopt/add_handle failure); only synthesize one when none exists. */
        request->last_error = bridge::describe_failure(code, result, request->error_buffer);
    }

    if (request->easy != nullptr)
    {
        if (was_active)
        {
            curl_multi_remove_handle(multi, request->easy);
        }
        /* Detach the bridge-generated header tail before freeing it so the
         * caller-owned header list stays intact. */
        if (request->extra_headers != nullptr)
        {
            if (request->headers != nullptr)
            {
                curl_slist* tail = request->headers;
                while (tail->next != nullptr && tail->next != request->extra_headers)
                {
                    tail = tail->next;
                }
                tail->next = nullptr;
            }
            curl_slist_free_all(request->extra_headers);
            request->extra_headers = nullptr;
        }
        base->release_handle(request->easy);
        request->easy = nullptr;
    }

    /* Managed completion runs on the loop thread; after it returns the managed
     * layer may destroy the request. */
    if (request->callbacks.on_complete != nullptr)
    {
        request->callbacks.on_complete(request->callbacks.context, result, &info);
    }
}

void curl_bridge_multi_client::process_commands()
{
    std::deque<Command> local;
    {
        std::lock_guard<std::mutex> guard(cmd_mutex);
        local.swap(commands);
    }
    for (const Command& command : local)
    {
        /* Per-command containment: a throw here (e.g. bad_alloc from a
         * container op inside start_request) must never reach the thread
         * entry, which would std::terminate the process. */
        try
        {
            switch (command.type)
            {
            case CommandType::Submit:
                start_request(command.request);
                break;
            case CommandType::Cancel:
                /* Stale-pointer safety: a queued Cancel/Unpause may name a
                 * request that already finished and been destroyed. FIFO
                 * ordering (single cmd_mutex, push_back, front-to-back drain)
                 * guarantees such a stale command is processed — as a no-op,
                 * active.count == 0 — before its address can be reused by a
                 * newly submitted request. So the raw pointer is only hashed
                 * and compared against `active`, never dereferenced unless the
                 * request is genuinely still active. */
                if (active.count(command.request) != 0)
                {
                    command.request->cancel_requested.store(true, std::memory_order_release);
                    finish_request(command.request, CURLE_ABORTED_BY_CALLBACK);
                }
                break;
            case CommandType::UnpauseWrite:
            case CommandType::UnpauseRead:
                if (active.count(command.request) != 0 && command.request->easy != nullptr)
                {
                    curl_easy_pause(command.request->easy, CURLPAUSE_CONT);
                }
                break;
            case CommandType::Shutdown:
                running.store(false, std::memory_order_release);
                break;
            }
        }
        catch (...)
        {
            /* Unblock a Submit whose start threw; other command failures are
             * benign (a missed cancel/unpause is caught by shutdown). */
            if (command.type == CommandType::Submit)
            {
                try { finish_request(command.request, CURLE_OUT_OF_MEMORY); }
                catch (...) {}
            }
        }
    }
}

void curl_bridge_multi_client::run()
{
    /* Whole-loop containment: no exception may escape a std::thread entry
     * (that is an unconditional std::terminate). A throw here breaks the loop
     * and falls through to the shutdown sweep so every request is still
     * completed rather than orphaned. */
    try
    {
        while (running.load(std::memory_order_acquire))
        {
            process_commands();

            int still_running = 0;
            curl_multi_perform(multi, &still_running);

            int in_queue = 0;
            CURLMsg* message;
            while ((message = curl_multi_info_read(multi, &in_queue)) != nullptr)
            {
                if (message->msg == CURLMSG_DONE)
                {
                    curl_bridge_request* request = nullptr;
                    curl_easy_getinfo(message->easy_handle, CURLINFO_PRIVATE, &request);
                    if (request != nullptr)
                    {
                        finish_request(request, message->data.result);
                    }
                }
            }

            if (!running.load(std::memory_order_acquire))
            {
                break;
            }

            /* Sleep until socket activity, a timeout, or curl_multi_wakeup. The
             * 1 s cap bounds libcurl's internal timers; poll (not wait) sleeps the
             * full timeout even with nothing to monitor. */
            curl_multi_poll(multi, nullptr, 0, 1000, nullptr);
        }
    }
    catch (...)
    {
        running.store(false, std::memory_order_release);
    }

    /* Shutdown: cancel everything still in flight. */
    std::vector<curl_bridge_request*> remaining(active.begin(), active.end());
    for (curl_bridge_request* request : remaining)
    {
        request->cancel_requested.store(true, std::memory_order_release);
        try { finish_request(request, CURLE_ABORTED_BY_CALLBACK); }
        catch (...) {}
    }

    /* Drain commands enqueued after the final process_commands swap: a Submit
     * that raced shutdown would otherwise never be completed, hanging its
     * caller forever. Complete each late Submit with CANCELLED. */
    std::deque<Command> leftover;
    {
        std::lock_guard<std::mutex> guard(cmd_mutex);
        leftover.swap(commands);
    }
    for (const Command& command : leftover)
    {
        if (command.type == CommandType::Submit && !command.request->finished)
        {
            try { complete_error(command.request, CURL_BRIDGE_CANCELLED); }
            catch (...) {}
        }
    }
}

extern "C" {

CURL_BRIDGE_API curl_bridge_multi_client* CURL_BRIDGE_CALL
curl_bridge_multi_create(const curl_bridge_client_options* options)
{
    try
    {
        curl_bridge_client* base = curl_bridge_client_create(options);
        if (base == nullptr)
        {
            return nullptr; /* last global error already set */
        }

        auto client = std::make_unique<curl_bridge_multi_client>();
        client->base = base;
        client->multi = curl_multi_init();
        if (client->multi == nullptr)
        {
            curl_bridge_client_destroy(base);
            bridge::set_last_global_error("curl_multi_init failed");
            return nullptr;
        }
        /* Multiplex HTTP/2 streams onto one connection (default in 8.x, set
         * explicitly for clarity). */
        curl_multi_setopt(client->multi, CURLMOPT_PIPELINING, CURLPIPE_MULTIPLEX);

        client->running.store(true, std::memory_order_release);
        curl_bridge_multi_client* raw = client.release();
        raw->loop_thread = std::thread([raw]() { raw->run(); });
        return raw;
    }
    catch (const std::exception& ex)
    {
        /* No string concatenation in the handler: ex may be bad_alloc. */
        try { bridge::set_last_global_error(ex.what()); } catch (...) {}
        return nullptr;
    }
    catch (...)
    {
        try { bridge::set_last_global_error("multi_create: unknown native exception"); }
        catch (...) {}
        return nullptr;
    }
}

CURL_BRIDGE_API void CURL_BRIDGE_CALL
curl_bridge_multi_destroy(curl_bridge_multi_client* client)
{
    if (client == nullptr)
    {
        return;
    }
    try
    {
        /* Stop accepting new work before signalling shutdown so a submit that
         * races this destroy is completed (CANCELLED) rather than dropped. */
        client->accepting.store(false, std::memory_order_release);
        client->enqueue(CommandType::Shutdown, nullptr);
        if (client->loop_thread.joinable())
        {
            client->loop_thread.join();
        }
        if (client->multi != nullptr)
        {
            curl_multi_cleanup(client->multi);
        }
        curl_bridge_client_destroy(client->base);
        delete client;
    }
    catch (...)
    {
    }
}

CURL_BRIDGE_API curl_bridge_request* CURL_BRIDGE_CALL
curl_bridge_multi_request_create(curl_bridge_multi_client* client)
{
    if (client == nullptr)
    {
        return nullptr;
    }
    return curl_bridge_request_create(client->base);
}

CURL_BRIDGE_API curl_bridge_result CURL_BRIDGE_CALL
curl_bridge_multi_submit(curl_bridge_multi_client* client, curl_bridge_request* request)
{
    if (client == nullptr || request == nullptr)
    {
        return CURL_BRIDGE_INVALID_ARGUMENT;
    }
    if (request->url.empty() || request->method.empty())
    {
        request->last_error = "method and URL must be set before submit";
        return CURL_BRIDGE_INVALID_ARGUMENT;
    }
    if (!request->proxy_configured)
    {
        request->last_error =
            "proxy must be configured explicitly (\"\" to disable)";
        return CURL_BRIDGE_INVALID_ARGUMENT;
    }
    if (request->callbacks.on_body_data == nullptr ||
        request->callbacks.on_header_line == nullptr ||
        request->callbacks.on_complete == nullptr)
    {
        request->last_error = "body, header and completion callbacks are required";
        return CURL_BRIDGE_INVALID_ARGUMENT;
    }
    /* Refuse work once shutdown has begun: the loop may already be past its
     * final command drain, in which case the submit would never complete. */
    if (!client->accepting.load(std::memory_order_acquire))
    {
        request->last_error = "client is shutting down";
        return CURL_BRIDGE_CANCELLED;
    }
    try
    {
        request->submitted = true;
        client->enqueue(CommandType::Submit, request);
        return CURL_BRIDGE_OK;
    }
    catch (...)
    {
        /* enqueue allocates (deque push_back); never let it cross the ABI. */
        return CURL_BRIDGE_INTERNAL_ERROR;
    }
}

CURL_BRIDGE_API void CURL_BRIDGE_CALL
curl_bridge_multi_cancel(curl_bridge_multi_client* client, curl_bridge_request* request)
{
    if (client != nullptr && request != nullptr)
    {
        request->cancel_requested.store(true, std::memory_order_release);
        /* enqueue allocates; a throw must not cross the C ABI. The cancel flag
         * is already set, so the loop's xferinfo/next poll still observes it. */
        try { client->enqueue(CommandType::Cancel, request); } catch (...) {}
    }
}

CURL_BRIDGE_API void CURL_BRIDGE_CALL
curl_bridge_multi_unpause_write(curl_bridge_multi_client* client, curl_bridge_request* request)
{
    if (client != nullptr && request != nullptr)
    {
        try { client->enqueue(CommandType::UnpauseWrite, request); } catch (...) {}
    }
}

CURL_BRIDGE_API void CURL_BRIDGE_CALL
curl_bridge_multi_unpause_read(curl_bridge_multi_client* client, curl_bridge_request* request)
{
    if (client != nullptr && request != nullptr)
    {
        try { client->enqueue(CommandType::UnpauseRead, request); } catch (...) {}
    }
}

} /* extern "C" */
