/* The C ABI compiled AS C (13 § 7): the header must parse under a C compiler, and the entry points that need no server must answer
 * without one. Exit code 0 on success; each failure prints its line. */

#include <stdio.h>
#include <string.h>

#include "typhon/client.h"

_Static_assert(sizeof(typhon_realm) >= 2 * 3 * sizeof(double), "a realm carries its six bounds");
_Static_assert(TYPHON_MOTION_CHANGE_BIT == 0x100u, "motion sits just above the eight group bits");

static int failures = 0;

#define EXPECT(condition)                                               \
    do                                                                  \
    {                                                                   \
        if (!(condition))                                               \
        {                                                               \
            fprintf(stderr, "%s:%d: EXPECT(%s)\n", __FILE__, __LINE__, #condition); \
            failures++;                                                 \
        }                                                               \
    } while (0)

int main(void)
{
    typhon_client* client = (typhon_client*)1;
    typhon_client_config config;
    uint32_t index = 0;
    int64_t tick = 0;
    double frac = 0;

    /* A null config is refused, and the reason is on this thread. */
    EXPECT(typhon_client_create(NULL, &client) == TYPHON_ERROR_INVALID_ARGUMENT);
    EXPECT(client == NULL);
    EXPECT(strlen(typhon_client_last_error(NULL)) > 0);

    /* A struct_size that is not this header's is refused: an older caller's struct is never read past its end. */
    memset(&config, 0, sizeof config);
    config.struct_size = 4;
    config.host = "127.0.0.1";
    config.port = 1;
    EXPECT(typhon_client_create(&config, &client) == TYPHON_ERROR_INVALID_ARGUMENT);

    /* No host: refused. */
    config.struct_size = sizeof config;
    config.host = NULL;
    EXPECT(typhon_client_create(&config, &client) == TYPHON_ERROR_INVALID_ARGUMENT);

    /* A valid config creates a client that has not connected: nothing about the catalog is known yet. */
    config.host = "127.0.0.1";
    config.port = 1;
    config.kind = "probe";
    config.max_attempts = 0;
    EXPECT(typhon_client_create(&config, &client) == TYPHON_OK);
    EXPECT(client != NULL);
    EXPECT(typhon_client_get_status(client) == TYPHON_CLIENT_STOPPED);
    EXPECT(typhon_client_tick(client) == -1);
    EXPECT(typhon_locate(client, 5) == TYPHON_NOT_FOUND);
    EXPECT(typhon_archetype_index(client, "Drone", &index) == TYPHON_ERROR_STATE);
    EXPECT(strlen(typhon_client_last_error(client)) > 0);
    EXPECT(typhon_clock_render_time(client, &tick, &frac) == TYPHON_ERROR_STATE);
    EXPECT(typhon_archetype_index(client, "Drone", NULL) == TYPHON_ERROR_INVALID_ARGUMENT);
    EXPECT(typhon_client_stop(client, 1007) == TYPHON_ERROR_INVALID_ARGUMENT);
    EXPECT(typhon_client_now_ms(client) > 0);
    typhon_client_destroy(client);
    typhon_client_destroy(NULL);

    if (failures == 0)
    {
        printf("C ABI header test: ok\n");
    }

    return failures == 0 ? 0 : 1;
}
