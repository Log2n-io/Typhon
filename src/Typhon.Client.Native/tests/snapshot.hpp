#pragma once

#include <cstdint>
#include <span>
#include <string>
#include <string_view>
#include <vector>

#include "apply/frame_applier.hpp"
#include "golden_support.hpp"

// The replica rendered in the implementation-neutral shape of the .NET SDK's StreamSnapshot (every collection sorted by its key, numbers as IEEE bits,
// text and bytes as lower-case hex): what the stream-* golden vectors commit, and what the live differential test compares with the .NET client.
namespace typhon::test {

Value Num(double v);
Value Str(std::string s);
Value BitsOf(std::span<const double> values);
Value HexOf(std::string_view text);

// A TCP stream's messages: `u32 len` little-endian, excluding itself (03 § 10, W31).
std::vector<std::vector<std::uint8_t>> Unframe(const std::vector<std::uint8_t>& stream);

// An event as dispatched; an entityRef also records whether the store resolves it then (the apply order, observable).
Value RenderEvent(const client::EventRecord& event, const client::FrameApplier& applier);

// The replica after the latest frame, with that frame's events.
Value Render(const client::FrameApplier& applier, std::vector<Value> events);

}  // namespace typhon::test
