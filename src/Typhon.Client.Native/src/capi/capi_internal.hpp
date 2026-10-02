#pragma once

#include <functional>
#include <memory>

#include "net/connection.hpp"
#include "net/transport.hpp"
#include "typhon/client.h"

// The C ABI's test seam: a client over another transport and clock, so the C surface is exercised without a socket. Not exported.
namespace typhon::client::capi {

typhon_status CreateWithTransport(const typhon_client_config* config, std::function<std::unique_ptr<Transport>()> transport, NowFn now,
                                  typhon_client** out);

}  // namespace typhon::client::capi
