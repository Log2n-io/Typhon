/*
 * The Typhon client SDK's C ABI (design/Subscriptions/13 § 7, 05 § 3a): a typhon.3 session over TCP, its replica, its render clock,
 * its commands. Every language with an FFI binds this header; the C++ behind it is an implementation detail.
 *
 * Rules of the ABI:
 * - Opaque handles, plain structs, typhon_status return codes. No C++ type, exception or container crosses it.
 * - No thread inside: the caller drives typhon_client_poll. A handle is used from one thread at a time.
 * - Every pointer the SDK hands out (a column, a text, an event) stays valid until the next call that can change it: the next
 *   typhon_client_poll for replica data, the end of the callback for anything a callback receives. Growth moves columns; a binding
 *   re-reads them when typhon_archetype_view's `version` moved.
 * - Callbacks run inside typhon_client_poll. From one, typhon_client_poll returns TYPHON_ERROR_STATE (it is not re-entrant);
 *   typhon_client_stop, typhon_client_start, the replica readers and the command calls are allowed; typhon_client_destroy is deferred
 *   until the poll running the callback returns, and the handle must not be used after that poll.
 * - Strings are UTF-8, NUL-terminated on input, (pointer, length) on output.
 */
#ifndef TYPHON_CLIENT_H
#define TYPHON_CLIENT_H

#include <stddef.h>
#include <stdint.h>

#if defined(TYPHON_CLIENT_SHARED)
#if defined(_WIN32)
#if defined(TYPHON_CLIENT_BUILD)
#define TYPHON_API __declspec(dllexport)
#else
#define TYPHON_API __declspec(dllimport)
#endif
#else
#define TYPHON_API __attribute__((visibility("default")))
#endif
#else
#define TYPHON_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef enum typhon_status
{
    TYPHON_OK = 0,
    /* A null handle or pointer, an unknown name, an index out of range. */
    TYPHON_ERROR_INVALID_ARGUMENT = 1,
    /* The call needs a state the client is not in: no session yet, no realm, not open. */
    TYPHON_ERROR_STATE = 2,
    /* A value the codec cannot represent: an enum outside its names, an over-cap string. */
    TYPHON_ERROR_OUT_OF_RANGE = 3,
    /* A command above the message cap on its own. */
    TYPHON_ERROR_TOO_LARGE = 4,
    TYPHON_ERROR_OUT_OF_MEMORY = 5,
    /* The local token bucket of the command's type is empty (a pre-check: the server's bucket decides). */
    TYPHON_RATE_LIMITED = 6,
    TYPHON_ERROR_INTERNAL = 7
} typhon_status;

typedef enum typhon_client_status
{
    TYPHON_CLIENT_STOPPED = 0,
    TYPHON_CLIENT_CONNECTING = 1,
    TYPHON_CLIENT_OPEN = 2,
    TYPHON_CLIENT_WAITING_TO_RECONNECT = 3,
    TYPHON_CLIENT_GAVE_UP = 4
} typhon_client_status;

/* The storage of a decoded field: a typed column, or text and bytes read by slot. */
typedef enum typhon_field_kind
{
    TYPHON_FIELD_U8 = 0,
    TYPHON_FIELD_I8 = 1,
    TYPHON_FIELD_U16 = 2,
    TYPHON_FIELD_I16 = 3,
    TYPHON_FIELD_U32 = 4,
    TYPHON_FIELD_I32 = 5,
    TYPHON_FIELD_U64 = 6,
    TYPHON_FIELD_I64 = 7,
    TYPHON_FIELD_F32 = 8,
    TYPHON_FIELD_F64 = 9,
    TYPHON_FIELD_TEXT = 10,
    TYPHON_FIELD_BYTES = 11,
    /* W34: a collection per slot. The C API reports the kind only; its elements are read through the C++ store (CollectionValue). */
    TYPHON_FIELD_COLLECTION = 12
} typhon_field_kind;

/* The update-mask bit a motion segment sets, above the eight change-group bits. */
#define TYPHON_MOTION_CHANGE_BIT 0x100u
/* typhon_locate's answer for a netId the replica does not hold. */
#define TYPHON_NOT_FOUND 0xFFFFFFFFu
/* The longest p[dims] v[dims] an evaluation writes. */
#define TYPHON_MAX_MOTION_STRIDE 6

typedef struct typhon_client typhon_client;
/* An event, valid only during the on_event callback. */
typedef struct typhon_event typhon_event;

/* A realm's frame (typhon.3): what positions are decoded over. */
typedef struct typhon_realm
{
    uint32_t realm_id;
    uint32_t generation;
    uint32_t kind_idx;
    uint32_t app_tag;
    int32_t position_bits;
    int32_t deep;
    double cell_m;
    double min[3];
    double max[3];
} typhon_realm;

/* Process-wide allocator hooks for every buffer the replica, the arenas and the queue grow. Set before the first client is created:
 * while one is alive, typhon_set_allocator returns TYPHON_ERROR_STATE. */
typedef struct typhon_allocator
{
    void* (*alloc)(size_t size, size_t alignment, void* user);
    void (*free)(void* block, void* user);
    void* user;
} typhon_allocator;

typedef struct typhon_client_config
{
    /* sizeof(typhon_client_config), so a newer SDK can tell an older caller's struct. */
    uint32_t struct_size;
    const char* host;
    uint16_t port;
    /* The application-declared session kind (<= 32 bytes) and the opaque admission token (<= 8 KiB); NULL for none. */
    const char* kind;
    const char* token;
    /* Capabilities asked for (W23). */
    uint32_t caps;
    const uint8_t* hello_payload;
    size_t hello_payload_len;
    /* Attempts before giving up, from the last open session; negative: unlimited. */
    int32_t max_attempts;
    /* Largest netId the replica accepts; 0: 2^22. */
    uint32_t max_net_id;
    /* The render delay's bounds in ms; 0: 150 and 300. */
    double min_delay_ms;
    double max_delay_ms;

    /* Callbacks, all optional, all called synchronously from inside typhon_client_poll with `user`. */
    void* user;
    void (*on_welcome)(void* user, uint32_t session_id, int32_t resumed);
    void (*on_close)(void* user, int32_t code, const char* reason, size_t reason_len, int32_t was_clean);
    void (*on_give_up)(void* user, int32_t code);
    void (*on_reset)(void* user);
    void (*on_realm_changed)(void* user, const typhon_realm* previous, const typhon_realm* current);
    void (*on_event)(void* user, const typhon_event* event);
    /* After each frame applied: the replica is consistent here. */
    void (*on_frame)(void* user, uint32_t tick);
    /* Decides for a clean close and for application codes (4100-4999): non-zero to reconnect. */
    int32_t (*should_reconnect)(void* user, int32_t code);
} typhon_client_config;

/* One archetype's replica, as of the latest frame. Every array is valid until the next typhon_client_poll. */
typedef struct typhon_archetype_view
{
    uint32_t capacity;
    /* Moves whenever the columns are reallocated: re-read every column pointer when it does. */
    uint32_t version;
    /* netId per slot (capacity entries), 0 for a free slot. */
    const uint32_t* net_ids;
    /* Occupied slots, dense. */
    const uint32_t* live;
    uint32_t live_count;
    /* This frame's changes: slots entered and updated (filter with the slot's net id: a slot that left is 0), netIds that left. */
    const uint32_t* entered;
    uint32_t entered_count;
    const uint32_t* updated;
    uint32_t updated_count;
    const uint32_t* left;
    uint32_t left_count;
    /* 0 when not spatial, else 2 or 3; doubles per slot an evaluation writes (2 x dims). */
    int32_t dims;
    int32_t motion_stride;
} typhon_archetype_view;

typedef struct typhon_column
{
    /* A typhon_field_kind, as a fixed-width integer: an enum's size is the compiler's choice, a bad thing to share across an FFI. */
    uint32_t kind;
    /* Values per slot: 1 to 16 (a count, W33). A U64 / I64 column holds 64-bit integers exactly (W32). */
    int32_t components;
    /* capacity x components values of `kind`; NULL for text, bytes and a collection. */
    const void* data;
} typhon_column;

/* A command field's value: text when `text` is set, else bytes when `bytes` is set, else 64-bit integers when `integers` is set (W32:
 * a u64/varu64 field's values, an i64/vari64 field's as two's-complement bit patterns), else numbers (a scalar, the components of a
 * vector or a count, a list's flattened elements). An empty text or bytes value still passes a non-NULL pointer, with a length of 0. */
typedef struct typhon_value
{
    const char* name;
    const double* numbers;
    size_t number_count;
    const char* text;
    size_t text_len;
    const uint8_t* bytes;
    size_t bytes_len;
    const uint64_t* integers;
    size_t integer_count;
} typhon_value;

TYPHON_API typhon_status typhon_set_allocator(const typhon_allocator* allocator);

/* Creates a client; nothing connects before typhon_client_start. On failure *out is NULL and typhon_client_last_error(NULL) says why. */
TYPHON_API typhon_status typhon_client_create(const typhon_client_config* config, typhon_client** out);
TYPHON_API void typhon_client_destroy(typhon_client* client);

/* The latest failure's message on this handle, or on this thread for a NULL handle; never NULL. Valid until the next call. */
TYPHON_API const char* typhon_client_last_error(const typhon_client* client);

TYPHON_API typhon_status typhon_client_start(typhon_client* client);
/* Stops reconnecting and leaves with BYE `code` (1000, or 4000-4999). */
TYPHON_API typhon_status typhon_client_stop(typhon_client* client, int32_t code);
/* One step: a due reconnect, one transport event (a frame is applied before this returns), a due PING. `handled` may be NULL. */
TYPHON_API typhon_status typhon_client_poll(typhon_client* client, int32_t timeout_ms, int32_t* handled);
TYPHON_API typhon_client_status typhon_client_get_status(const typhon_client* client);

/* The newest applied tick, or -1; the session's realm (TYPHON_ERROR_STATE outside one). */
TYPHON_API int64_t typhon_client_tick(const typhon_client* client);
TYPHON_API typhon_status typhon_client_realm(const typhon_client* client, typhon_realm* out);
/* Protocol inconsistencies the replica absorbed (03 § 10). */
TYPHON_API uint64_t typhon_client_anomalies(const typhon_client* client);

/* Catalog introspection, from the first WELCOME on. Indices are wire indices; a name not found is TYPHON_ERROR_INVALID_ARGUMENT. */
TYPHON_API typhon_status typhon_archetype_index(const typhon_client* client, const char* name, uint32_t* out);
TYPHON_API typhon_status typhon_field_index(const typhon_client* client, uint32_t archetype, const char* name, uint32_t* out);
TYPHON_API typhon_status typhon_command_index(const typhon_client* client, const char* name, uint32_t* out);

/* The replica. */
TYPHON_API typhon_status typhon_archetype_view_get(const typhon_client* client, uint32_t archetype, typhon_archetype_view* out);
TYPHON_API typhon_status typhon_field_column(const typhon_client* client, uint32_t archetype, uint32_t field, typhon_column* out);
TYPHON_API typhon_status typhon_field_text(const typhon_client* client, uint32_t archetype, uint32_t field, uint32_t slot, const char** data,
                                           size_t* length);
TYPHON_API typhon_status typhon_field_bytes(const typhon_client* client, uint32_t archetype, uint32_t field, uint32_t slot,
                                            const uint8_t** data, size_t* length);
/* `archetype << 24 | slot` of a held netId, or TYPHON_NOT_FOUND. */
TYPHON_API uint32_t typhon_locate(const typhon_client* client, uint32_t net_id);

/* Render time: local monotonic ms, advancing the clock, and the time motion is evaluated at. */
TYPHON_API double typhon_client_now_ms(const typhon_client* client);
TYPHON_API typhon_status typhon_clock_update(typhon_client* client, double now_ms);
TYPHON_API typhon_status typhon_clock_render_time(const typhon_client* client, int64_t* tick, double* frac);

/* Motion at the clock's render time (03 § 6): p[dims] v[dims] per slot; for every live slot, in live order, `out` holding
 * live_count x motion_stride doubles. */
TYPHON_API typhon_status typhon_motion_evaluate_slot(const typhon_client* client, uint32_t archetype, uint32_t slot, double* out,
                                                     size_t out_len);
TYPHON_API typhon_status typhon_motion_evaluate_live(const typhon_client* client, uint32_t archetype, double* out, size_t out_len);

/* Commands: queued (copied), then sent in one batch per rendered frame. */
TYPHON_API typhon_status typhon_command_enqueue(typhon_client* client, uint32_t command, const typhon_value* values, size_t count,
                                                int32_t* seq);
TYPHON_API typhon_status typhon_commands_flush(typhon_client* client, int32_t* messages);

/* An event, from inside on_event. */
TYPHON_API uint32_t typhon_event_type(const typhon_event* event);
TYPHON_API const char* typhon_event_type_name(const typhon_event* event);
TYPHON_API uint32_t typhon_event_tick(const typhon_event* event);
TYPHON_API typhon_status typhon_event_field_index(const typhon_event* event, const char* name, uint32_t* out);
TYPHON_API typhon_status typhon_event_numbers(const typhon_event* event, uint32_t field, const double** values, size_t* count);
/* A 64-bit integer field's components (W32), as bit patterns: cast to int64_t for an i64 or vari64 field. */
TYPHON_API typhon_status typhon_event_integers(const typhon_event* event, uint32_t field, const uint64_t** values, size_t* count);
TYPHON_API typhon_status typhon_event_text(const typhon_event* event, uint32_t field, const char** data, size_t* length);
TYPHON_API typhon_status typhon_event_bytes(const typhon_event* event, uint32_t field, const uint8_t** data, size_t* length);

#ifdef __cplusplus
}
#endif

#endif /* TYPHON_CLIENT_H */
