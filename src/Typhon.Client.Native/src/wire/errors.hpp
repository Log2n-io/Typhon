#pragma once

#include <stdexcept>
#include <string>

#include "wire/constants.hpp"

namespace typhon::client {

// Bytes received from the other side do not form a valid message. It carries the close code the connection must be closed with,
// so a transport never classifies a decoding failure itself. Only a decoder throws it, and only for input it received; an encoder
// handed a value it cannot represent throws std::out_of_range instead — a bug on the sending side, not a hostile peer.
class WireFormatError : public std::runtime_error {
public:
    WireFormatError(int closeCode, const std::string& message) : std::runtime_error(message), closeCode_(closeCode) {}

    int CloseCode() const noexcept { return closeCode_; }

private:
    int closeCode_;
};

// A payload inconsistent with its type: close 1007.
inline WireFormatError Malformed(const std::string& message) { return WireFormatError(CloseCode::MalformedPayload, message); }

// Framing, or an unknown or out-of-state message type: close 1002.
inline WireFormatError ProtocolError(const std::string& message) { return WireFormatError(CloseCode::ProtocolError, message); }

}  // namespace typhon::client
