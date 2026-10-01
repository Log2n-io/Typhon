// The C ABI over Client: every entry point catches, so no exception crosses into C, and maps the exception to a status and a message.

#include <atomic>
#include <cstring>
#include <new>
#include <stdexcept>
#include <string>
#include <vector>

#include "capi/capi_internal.hpp"
#include "client/client.hpp"
#include "motion/motion.hpp"
#include "wire/errors.hpp"
#include "wire/realm_frame.hpp"

struct typhon_client {
    std::unique_ptr<typhon::client::Client> client;
    typhon_client_config config{};
    std::string lastError;
    // A destroy asked for from one of this client's callbacks: carried out once the poll that runs them has returned.
    bool destroyPending = false;
};

namespace typhon::client::capi {

namespace {

thread_local std::string t_lastError;

// Clients alive in the process: the allocator hooks may not change under one, or a block would be freed by a hook that did not allocate it.
std::atomic<int> g_liveClients{0};

typhon_status Fail(typhon_client* handle, typhon_status status, const char* what)
{
    (handle != nullptr ? handle->lastError : t_lastError) = what;
    return status;
}

// Runs `body`, turning any exception into a status: nothing thrown in C++ reaches a C caller.
template <class Body>
typhon_status Guard(typhon_client* handle, Body&& body)
{
    try
    {
        return body();
    }
    catch (const std::bad_alloc&)
    {
        return Fail(handle, TYPHON_ERROR_OUT_OF_MEMORY, "out of memory");
    }
    catch (const std::length_error& e)
    {
        return Fail(handle, TYPHON_ERROR_TOO_LARGE, e.what());
    }
    catch (const std::out_of_range& e)
    {
        return Fail(handle, TYPHON_ERROR_OUT_OF_RANGE, e.what());
    }
    catch (const std::invalid_argument& e)
    {
        return Fail(handle, TYPHON_ERROR_INVALID_ARGUMENT, e.what());
    }
    catch (const std::logic_error& e)
    {
        return Fail(handle, TYPHON_ERROR_STATE, e.what());
    }
    catch (const std::exception& e)
    {
        return Fail(handle, TYPHON_ERROR_INTERNAL, e.what());
    }
    catch (...)
    {
        return Fail(handle, TYPHON_ERROR_INTERNAL, "unknown failure");
    }
}

typhon_realm ToC(const RealmFrame& f)
{
    typhon_realm r{};
    r.realm_id = f.realmId;
    r.generation = f.generation;
    r.kind_idx = f.kindIdx;
    r.app_tag = f.appTag;
    r.position_bits = f.positionBits;
    r.deep = f.deep ? 1 : 0;
    r.cell_m = f.cellM;
    for (std::size_t i = 0; i < 3; i++)
    {
        r.min[i] = f.min[i];
        r.max[i] = f.max[i];
    }

    return r;
}

FrameApplier* ApplierOf(const typhon_client* handle)
{
    if (handle == nullptr)
    {
        throw std::invalid_argument("a null client");
    }

    FrameApplier* applier = handle->client->Applier();
    if (applier == nullptr)
    {
        throw std::logic_error("no session has opened yet: the catalog is unknown");
    }

    return applier;
}

const ArchetypeStore& StoreOf(const typhon_client* handle, std::uint32_t archetype)
{
    const FrameApplier* applier = ApplierOf(handle);
    if (archetype >= applier->World().ArchetypeCount())
    {
        throw std::invalid_argument("no archetype " + std::to_string(archetype));
    }

    return applier->World().Archetype(archetype);
}

void RequireField(const ArchetypeStore& store, std::uint32_t field)
{
    if (field >= store.Schema().fields.size())
    {
        throw std::invalid_argument("archetype '" + store.Schema().name + "' has no field " + std::to_string(field));
    }
}

void RequireSlot(const ArchetypeStore& store, std::uint32_t slot)
{
    if (slot >= store.Capacity())
    {
        throw std::invalid_argument("slot " + std::to_string(slot) + " is beyond archetype '" + store.Schema().name + "'");
    }
}

template <class T>
void RequireOut(T* p)
{
    if (p == nullptr)
    {
        throw std::invalid_argument("a null output pointer");
    }
}

const EventRecord& EventOf(const typhon_event* event) { return *reinterpret_cast<const EventRecord*>(event); }

void RequireEventField(const EventRecord& record, std::uint32_t field)
{
    if (field >= record.Type().fields.size())
    {
        throw std::invalid_argument("event '" + record.Type().name + "' has no field " + std::to_string(field));
    }
}

}  // namespace

typhon_status CreateWithTransport(const typhon_client_config* config, std::function<std::unique_ptr<Transport>()> transport, NowFn now,
                                  typhon_client** out)
{
    if (out != nullptr)
    {
        *out = nullptr;
    }

    return Guard(nullptr,
                 [&]
                 {
                     if (config == nullptr || out == nullptr)
                     {
                         throw std::invalid_argument("a null config or output pointer");
                     }

                     if (config->struct_size < sizeof(typhon_client_config))
                     {
                         throw std::invalid_argument("the config's struct_size is not sizeof(typhon_client_config)");
                     }

                     auto handle = std::make_unique<typhon_client>();
                     handle->config = *config;
                     const typhon_client_config& c = handle->config;
                     ClientOptions options;
                     if (!transport)
                     {
                         if (c.host == nullptr || c.port == 0)
                         {
                             throw std::invalid_argument("a client needs a host and a port");
                         }

                         options.host = c.host;
                         options.port = c.port;
                     }

                     options.transport = std::move(transport);
                     options.now = std::move(now);
                     options.kind = c.kind != nullptr ? c.kind : "";
                     options.token = c.token != nullptr ? c.token : "";
                     options.caps = c.caps;
                     if (c.hello_payload != nullptr)
                     {
                         options.helloPayload.assign(c.hello_payload, c.hello_payload + c.hello_payload_len);
                     }

                     options.maxAttempts = c.max_attempts;
                     if (c.max_net_id != 0)
                     {
                         options.maxNetId = c.max_net_id;
                     }

                     if (c.min_delay_ms > 0)
                     {
                         options.clock.minDelayMs = c.min_delay_ms;
                     }

                     if (c.max_delay_ms > 0)
                     {
                         options.clock.maxDelayMs = c.max_delay_ms;
                     }

                     void* user = c.user;
                     if (c.on_welcome != nullptr)
                     {
                         options.onWelcome = [f = c.on_welcome, user](const SessionInfo& s) { f(user, s.sessionId, s.resumed ? 1 : 0); };
                     }

                     if (c.on_close != nullptr)
                     {
                         options.onClose = [f = c.on_close, user](const ConnectionClose& close)
                         { f(user, close.code, close.reason.c_str(), close.reason.size(), close.wasClean ? 1 : 0); };
                     }

                     if (c.on_give_up != nullptr)
                     {
                         options.onGiveUp = [f = c.on_give_up, user](const ConnectionClose& close, ReconnectRule) { f(user, close.code); };
                     }

                     if (c.on_reset != nullptr)
                     {
                         options.onReset = [f = c.on_reset, user] { f(user); };
                     }

                     if (c.on_realm_changed != nullptr)
                     {
                         options.onRealmChanged = [f = c.on_realm_changed, user](const RealmFrame* previous, const RealmFrame* current)
                         {
                             typhon_realm p{};
                             typhon_realm n{};
                             if (previous != nullptr)
                             {
                                 p = ToC(*previous);
                             }

                             if (current != nullptr)
                             {
                                 n = ToC(*current);
                             }

                             f(user, previous != nullptr ? &p : nullptr, current != nullptr ? &n : nullptr);
                         };
                     }

                     if (c.on_event != nullptr)
                     {
                         options.onEvent = [f = c.on_event, user](const EventRecord& record)
                         { f(user, reinterpret_cast<const typhon_event*>(&record)); };
                     }

                     if (c.should_reconnect != nullptr)
                     {
                         options.shouldReconnect = [f = c.should_reconnect, user](const ConnectionClose& close) { return f(user, close.code) != 0; };
                     }

                     typhon_client* raw = handle.get();
                     if (c.on_frame != nullptr)
                     {
                         options.onFrame = [f = c.on_frame, user, raw]
                         { f(user, static_cast<std::uint32_t>(raw->client->Applier()->Tick())); };
                     }

                     // The config's strings are the caller's: they are copied, and the handle's copy never points at them.
                     handle->config.host = nullptr;
                     handle->config.kind = nullptr;
                     handle->config.token = nullptr;
                     handle->config.hello_payload = nullptr;
                     handle->client = std::make_unique<Client>(std::move(options));
                     *out = handle.release();
                     g_liveClients++;
                     return TYPHON_OK;
                 });
}

}  // namespace typhon::client::capi

using namespace typhon::client;
using typhon::client::capi::Guard;

extern "C" {

typhon_status typhon_set_allocator(const typhon_allocator* allocator)
{
    return Guard(nullptr,
                 [&]
                 {
                     if (capi::g_liveClients.load() > 0)
                     {
                         throw std::logic_error("the allocator hooks cannot change while a client is alive");
                     }

                     AllocatorHooks hooks;
                     if (allocator != nullptr)
                     {
                         hooks.alloc = allocator->alloc;
                         hooks.free = allocator->free;
                         hooks.user = allocator->user;
                     }

                     SetAllocatorHooks(hooks);
                     return TYPHON_OK;
                 });
}

typhon_status typhon_client_create(const typhon_client_config* config, typhon_client** out)
{
    return capi::CreateWithTransport(config, nullptr, nullptr, out);
}

void typhon_client_destroy(typhon_client* client)
{
    if (client == nullptr)
    {
        return;
    }

    // The session ends with BYE either way. From inside a callback the poll running it still uses the client: the stop also ends that
    // poll's drain, and the delete waits for the poll to return.
    if (client->destroyPending)
    {
        return;
    }

    try
    {
        client->client->Stop();
    }
    catch (...)
    {
        // A BYE that cannot be written changes nothing about the destroy.
    }

    if (client->client->InPoll())
    {
        client->destroyPending = true;
        return;
    }

    delete client;
    capi::g_liveClients--;
}

const char* typhon_client_last_error(const typhon_client* client)
{
    return client != nullptr ? client->lastError.c_str() : capi::t_lastError.c_str();
}

typhon_status typhon_client_start(typhon_client* client)
{
    return Guard(client,
                 [&]
                 {
                     if (client == nullptr)
                     {
                         throw std::invalid_argument("a null client");
                     }

                     client->client->Start();
                     return TYPHON_OK;
                 });
}

typhon_status typhon_client_stop(typhon_client* client, int32_t code)
{
    return Guard(client,
                 [&]
                 {
                     if (client == nullptr)
                     {
                         throw std::invalid_argument("a null client");
                     }

                     if (!IsValidClientCloseCode(code))
                     {
                         throw std::invalid_argument("a client leaves with 1000 or a code in 4000-4999");
                     }

                     client->client->Stop(code);
                     return TYPHON_OK;
                 });
}

static typhon_status typhon_client_poll_step(typhon_client* client, int32_t timeout_ms, int32_t* handled)
{
    return Guard(client,
                 [&]
                 {
                     if (client == nullptr)
                     {
                         throw std::invalid_argument("a null client");
                     }

                     const bool any = client->client->Poll(timeout_ms < 0 ? 0 : timeout_ms);
                     if (handled != nullptr)
                     {
                         *handled = any ? 1 : 0;
                     }

                     return TYPHON_OK;
                 });
}

// The poll above ends here: a destroy one of its callbacks asked for is carried out now, and the handle is gone when this returns.
static typhon_status PollAndReap(typhon_client* client, int32_t timeout_ms, int32_t* handled)
{
    const typhon_status status = typhon_client_poll_step(client, timeout_ms, handled);
    if (client != nullptr && client->destroyPending && !client->client->InPoll())
    {
        delete client;
        capi::g_liveClients--;
    }

    return status;
}

typhon_status typhon_client_poll(typhon_client* client, int32_t timeout_ms, int32_t* handled) { return PollAndReap(client, timeout_ms, handled); }

typhon_client_status typhon_client_get_status(const typhon_client* client)
{
    return client == nullptr ? TYPHON_CLIENT_STOPPED : static_cast<typhon_client_status>(client->client->Status());
}

int64_t typhon_client_tick(const typhon_client* client)
{
    return client == nullptr || client->client->Applier() == nullptr ? -1 : client->client->Applier()->Tick();
}

typhon_status typhon_client_realm(const typhon_client* client, typhon_realm* out)
{
    return Guard(const_cast<typhon_client*>(client),
                 [&]
                 {
                     capi::RequireOut(out);
                     const RealmFrame* realm = capi::ApplierOf(client)->Realm();
                     if (realm == nullptr)
                     {
                         throw std::logic_error("the session holds no realm");
                     }

                     *out = capi::ToC(*realm);
                     return TYPHON_OK;
                 });
}

uint64_t typhon_client_anomalies(const typhon_client* client)
{
    return client == nullptr || client->client->Applier() == nullptr ? 0 : client->client->Applier()->World().anomalies;
}

typhon_status typhon_archetype_index(const typhon_client* client, const char* name, uint32_t* out)
{
    return Guard(const_cast<typhon_client*>(client),
                 [&]
                 {
                     capi::RequireOut(out);
                     const ArchetypePlan* a = capi::ApplierOf(client)->Plan().ArchetypeByName(name != nullptr ? name : "");
                     if (a == nullptr)
                     {
                         throw std::invalid_argument(std::string("no archetype '") + (name != nullptr ? name : "") + "'");
                     }

                     *out = static_cast<uint32_t>(a->idx);
                     return TYPHON_OK;
                 });
}

typhon_status typhon_field_index(const typhon_client* client, uint32_t archetype, const char* name, uint32_t* out)
{
    return Guard(const_cast<typhon_client*>(client),
                 [&]
                 {
                     capi::RequireOut(out);
                     const int index = capi::StoreOf(client, archetype).FieldIndex(name != nullptr ? name : "");
                     if (index < 0)
                     {
                         throw std::invalid_argument(std::string("no stored field '") + (name != nullptr ? name : "") + "'");
                     }

                     *out = static_cast<uint32_t>(index);
                     return TYPHON_OK;
                 });
}

typhon_status typhon_command_index(const typhon_client* client, const char* name, uint32_t* out)
{
    return Guard(const_cast<typhon_client*>(client),
                 [&]
                 {
                     capi::RequireOut(out);
                     const MessagePlan* command = capi::ApplierOf(client)->Plan().CommandByName(name != nullptr ? name : "");
                     if (command == nullptr)
                     {
                         throw std::invalid_argument(std::string("no command '") + (name != nullptr ? name : "") + "'");
                     }

                     *out = static_cast<uint32_t>(command->idx);
                     return TYPHON_OK;
                 });
}

typhon_status typhon_archetype_view_get(const typhon_client* client, uint32_t archetype, typhon_archetype_view* out)
{
    return Guard(const_cast<typhon_client*>(client),
                 [&]
                 {
                     capi::RequireOut(out);
                     const ArchetypeStore& s = capi::StoreOf(client, archetype);
                     out->capacity = s.Capacity();
                     out->version = s.Version();
                     out->net_ids = s.NetIds().data();
                     out->live = s.Live().data();
                     out->live_count = s.LiveCount();
                     out->entered = s.Entered().data();
                     out->entered_count = static_cast<uint32_t>(s.Entered().size());
                     out->updated = s.Updated().data();
                     out->updated_count = static_cast<uint32_t>(s.Updated().size());
                     out->left = s.Left().data();
                     out->left_count = static_cast<uint32_t>(s.Left().size());
                     out->dims = s.Dims();
                     out->motion_stride = s.MotionStride();
                     return TYPHON_OK;
                 });
}

typhon_status typhon_field_column(const typhon_client* client, uint32_t archetype, uint32_t field, typhon_column* out)
{
    return Guard(const_cast<typhon_client*>(client),
                 [&]
                 {
                     capi::RequireOut(out);
                     const ArchetypeStore& s = capi::StoreOf(client, archetype);
                     capi::RequireField(s, field);
                     const FieldSchema& f = s.Schema().fields[field];
                     out->kind = static_cast<uint32_t>(f.kind);
                     out->components = IsNumericKind(f.kind) ? f.components : 0;
                     out->data = IsNumericKind(f.kind) ? s.ColumnData(static_cast<int>(field)) : nullptr;
                     return TYPHON_OK;
                 });
}

typhon_status typhon_field_text(const typhon_client* client, uint32_t archetype, uint32_t field, uint32_t slot, const char** data,
                                size_t* length)
{
    return Guard(const_cast<typhon_client*>(client),
                 [&]
                 {
                     capi::RequireOut(data);
                     capi::RequireOut(length);
                     const ArchetypeStore& s = capi::StoreOf(client, archetype);
                     capi::RequireField(s, field);
                     capi::RequireSlot(s, slot);
                     if (s.Schema().fields[field].kind != FieldKind::Text)
                     {
                         throw std::invalid_argument("field " + std::to_string(field) + " is not text");
                     }

                     const std::string_view text = s.TextAt(static_cast<int>(field), slot);
                     *data = text.data();
                     *length = text.size();
                     return TYPHON_OK;
                 });
}

typhon_status typhon_field_bytes(const typhon_client* client, uint32_t archetype, uint32_t field, uint32_t slot, const uint8_t** data,
                                 size_t* length)
{
    return Guard(const_cast<typhon_client*>(client),
                 [&]
                 {
                     capi::RequireOut(data);
                     capi::RequireOut(length);
                     const ArchetypeStore& s = capi::StoreOf(client, archetype);
                     capi::RequireField(s, field);
                     capi::RequireSlot(s, slot);
                     if (s.Schema().fields[field].kind != FieldKind::Bytes)
                     {
                         throw std::invalid_argument("field " + std::to_string(field) + " is not bytes");
                     }

                     const auto bytes = s.BytesAt(static_cast<int>(field), slot);
                     *data = bytes.data();
                     *length = bytes.size();
                     return TYPHON_OK;
                 });
}

uint32_t typhon_locate(const typhon_client* client, uint32_t net_id)
{
    return client == nullptr || client->client->Applier() == nullptr ? TYPHON_NOT_FOUND : client->client->Applier()->World().Locate(net_id);
}

double typhon_client_now_ms(const typhon_client* client) { return client == nullptr ? 0 : client->client->NowMs(); }

typhon_status typhon_clock_update(typhon_client* client, double now_ms)
{
    return Guard(client,
                 [&]
                 {
                     capi::ApplierOf(client);
                     client->client->RenderClock()->Update(now_ms);
                     return TYPHON_OK;
                 });
}

typhon_status typhon_clock_render_time(const typhon_client* client, int64_t* tick, double* frac)
{
    return Guard(const_cast<typhon_client*>(client),
                 [&]
                 {
                     capi::RequireOut(tick);
                     capi::RequireOut(frac);
                     capi::ApplierOf(client);
                     const Clock& clock = *const_cast<typhon_client*>(client)->client->RenderClock();
                     *tick = clock.RenderTick();
                     *frac = clock.RenderFrac();
                     return TYPHON_OK;
                 });
}

typhon_status typhon_motion_evaluate_slot(const typhon_client* client, uint32_t archetype, uint32_t slot, double* out, size_t out_len)
{
    return Guard(const_cast<typhon_client*>(client),
                 [&]
                 {
                     capi::RequireOut(out);
                     const ArchetypeStore& s = capi::StoreOf(client, archetype);
                     if (!s.IsLive(slot))
                     {
                         throw std::invalid_argument("slot " + std::to_string(slot) + " is not live");
                     }

                     const Clock& clock = *const_cast<typhon_client*>(client)->client->RenderClock();
                     EvaluateSlot(s, slot, clock.RenderTick(), clock.RenderFrac(), {out, out_len});
                     return TYPHON_OK;
                 });
}

typhon_status typhon_motion_evaluate_live(const typhon_client* client, uint32_t archetype, double* out, size_t out_len)
{
    return Guard(const_cast<typhon_client*>(client),
                 [&]
                 {
                     const ArchetypeStore& s = capi::StoreOf(client, archetype);
                     if (out == nullptr && s.LiveCount() > 0)
                     {
                         throw std::invalid_argument("a null output buffer");
                     }

                     const Clock& clock = *const_cast<typhon_client*>(client)->client->RenderClock();
                     EvaluateLive(s, clock.RenderTick(), clock.RenderFrac(), {out, out_len});
                     return TYPHON_OK;
                 });
}

typhon_status typhon_command_enqueue(typhon_client* client, uint32_t command, const typhon_value* values, size_t count, int32_t* seq)
{
    return Guard(client,
                 [&]
                 {
                     const FrameApplier* applier = capi::ApplierOf(client);
                     const MessagePlan* found = nullptr;
                     try
                     {
                         found = &applier->Plan().Command(command);
                     }
                     catch (const WireFormatError&)
                     {
                         // The plan refuses an unknown index as a peer's error; from the caller it is a bad argument.
                         throw std::invalid_argument("no command " + std::to_string(command));
                     }

                     const MessagePlan& type = *found;
                     if (values == nullptr && count > 0)
                     {
                         throw std::invalid_argument("a null value array");
                     }

                     // A view per value, on the caller's buffers: the queue copies them before this call returns. Small batches stay
                     // on the stack.
                     constexpr std::size_t Inline = 16;
                     NamedValue inlineValues[Inline];
                     std::vector<NamedValue> heap;
                     NamedValue* named = inlineValues;
                     if (count > Inline)
                     {
                         heap.resize(count);
                         named = heap.data();
                     }

                     for (std::size_t i = 0; i < count; i++)
                     {
                         const typhon_value& v = values[i];
                         FieldValue value;
                         if (v.text != nullptr)
                         {
                             value.kind = FieldValue::Kind::Text;
                             value.text = {v.text, v.text_len};
                         }
                         else if (v.bytes != nullptr)
                         {
                             value.kind = FieldValue::Kind::Bytes;
                             value.bytes = {v.bytes, v.bytes_len};
                         }
                         else
                         {
                             value.kind = FieldValue::Kind::Numbers;
                             value.numbers = {v.numbers, v.number_count};
                         }

                         named[i] = {v.name != nullptr ? v.name : "", value};
                     }

                     const std::int32_t s = client->client->Commands()->Enqueue(type, {named, count});
                     if (seq != nullptr)
                     {
                         *seq = s;
                     }

                     return s == CommandRateLimited ? TYPHON_RATE_LIMITED : TYPHON_OK;
                 });
}

typhon_status typhon_commands_flush(typhon_client* client, int32_t* messages)
{
    return Guard(client,
                 [&]
                 {
                     capi::ApplierOf(client);
                     const int sent = client->client->FlushCommands();
                     if (messages != nullptr)
                     {
                         *messages = sent;
                     }

                     return TYPHON_OK;
                 });
}

uint32_t typhon_event_type(const typhon_event* event) { return event == nullptr ? 0 : static_cast<uint32_t>(capi::EventOf(event).Type().idx); }

const char* typhon_event_type_name(const typhon_event* event) { return event == nullptr ? "" : capi::EventOf(event).Type().name.c_str(); }

uint32_t typhon_event_tick(const typhon_event* event) { return event == nullptr ? 0 : capi::EventOf(event).tick; }

typhon_status typhon_event_field_index(const typhon_event* event, const char* name, uint32_t* out)
{
    return Guard(nullptr,
                 [&]
                 {
                     capi::RequireOut(event);
                     capi::RequireOut(out);
                     const int index = capi::EventOf(event).FieldIndex(name != nullptr ? name : "");
                     if (index < 0)
                     {
                         throw std::invalid_argument(std::string("no event field '") + (name != nullptr ? name : "") + "'");
                     }

                     *out = static_cast<uint32_t>(index);
                     return TYPHON_OK;
                 });
}

typhon_status typhon_event_numbers(const typhon_event* event, uint32_t field, const double** values, size_t* count)
{
    return Guard(nullptr,
                 [&]
                 {
                     capi::RequireOut(event);
                     capi::RequireOut(values);
                     capi::RequireOut(count);
                     const EventRecord& record = capi::EventOf(event);
                     capi::RequireEventField(record, field);
                     const auto numbers = record.Numbers(static_cast<int>(field));
                     *values = numbers.data();
                     *count = numbers.size();
                     return TYPHON_OK;
                 });
}

typhon_status typhon_event_text(const typhon_event* event, uint32_t field, const char** data, size_t* length)
{
    return Guard(nullptr,
                 [&]
                 {
                     capi::RequireOut(event);
                     capi::RequireOut(data);
                     capi::RequireOut(length);
                     const EventRecord& record = capi::EventOf(event);
                     capi::RequireEventField(record, field);
                     const std::string_view text = record.Text(static_cast<int>(field));
                     *data = text.data();
                     *length = text.size();
                     return TYPHON_OK;
                 });
}

typhon_status typhon_event_bytes(const typhon_event* event, uint32_t field, const uint8_t** data, size_t* length)
{
    return Guard(nullptr,
                 [&]
                 {
                     capi::RequireOut(event);
                     capi::RequireOut(data);
                     capi::RequireOut(length);
                     const EventRecord& record = capi::EventOf(event);
                     capi::RequireEventField(record, field);
                     const auto bytes = record.Bytes(static_cast<int>(field));
                     *data = bytes.data();
                     *length = bytes.size();
                     return TYPHON_OK;
                 });
}

}  // extern "C"
