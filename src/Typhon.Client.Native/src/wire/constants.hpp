#pragma once

#include <cstdint>

// Values fixed by the protocol major (design/Subscriptions/03-wire-protocol.md § 2-3), which a client needs before it has any
// catalog. The TypeScript SDK's protocol/constants.ts, value for value.
namespace typhon::client::protocol {

inline constexpr int Major = 3;
inline constexpr int Minor = 0;
inline constexpr int HelloMaxBytes = 16 * 1024;
inline constexpr int WelcomeMaxBytes = 1024 * 1024;
inline constexpr int TokenMaxBytes = 8 * 1024;
inline constexpr int SessionKindMaxBytes = 32;
inline constexpr int HelloPayloadMaxBytes = 256;
inline constexpr int KickReasonMaxBytes = 123;
inline constexpr int HelloTimeoutMs = 5000;
inline constexpr int FirstAppCommandIdx = 16;
inline constexpr int FirstAppEventIdx = 16;
inline constexpr int FirstAppMetricIdx = 32;
inline constexpr int MaxGroups = 8;
inline constexpr int MaxArchetypes = 255;
inline constexpr int MaxPackedBits = 24;
inline constexpr int MaxListCount = 255;
// The most values one count field carries (W33).
inline constexpr int MaxCount = 16;
// The longest shape hint, in UTF-8 bytes (W33).
inline constexpr int ShapeMaxBytes = 32;
inline constexpr int MaxGridCells = 1 << 24;
inline constexpr int MaxMessageIndex = 0xFFFF;
inline constexpr int MinVelocityUnitExp = -40;
inline constexpr int MaxVelocityUnitExp = 16;
inline constexpr int MaxRealmKinds = 128;
// Bits per axis of a realm-framed field a client SENDS: always 32, because the server's transport parses a command without
// reading the session's realm (SUB-05).
inline constexpr int CommandPositionBits = 32;

inline constexpr std::uint8_t TcpPreamble[4] = {0x54, 0x59, 0x50, 0x33};  // ASCII "TYP3"

}  // namespace typhon::client::protocol

namespace typhon::client {

namespace MessageType {
inline constexpr std::uint8_t Welcome = 0x01;
inline constexpr std::uint8_t Tick = 0x02;
inline constexpr std::uint8_t Pong = 0x03;
inline constexpr std::uint8_t Kick = 0x04;
inline constexpr std::uint8_t Hello = 0x81;
inline constexpr std::uint8_t Commands = 0x83;
inline constexpr std::uint8_t Ping = 0x84;
inline constexpr std::uint8_t Bye = 0x85;
}  // namespace MessageType

namespace BlockType {
inline constexpr std::uint8_t Entities = 0x01;
inline constexpr std::uint8_t Events = 0x02;
inline constexpr std::uint8_t Self = 0x03;
inline constexpr std::uint8_t Agg = 0x04;
inline constexpr std::uint8_t Stats = 0x05;
inline constexpr std::uint8_t Debug = 0x06;
inline constexpr std::uint8_t Acks = 0x07;
inline constexpr std::uint8_t Sources = 0x08;
inline constexpr std::uint8_t Realm = 0x09;
inline constexpr std::uint8_t Ext = 0x7F;
}  // namespace BlockType

namespace TickFlags {
inline constexpr std::uint8_t ViewComplete = 1;
inline constexpr std::uint8_t Reset = 2;
inline constexpr std::uint8_t Overload = 4;
inline constexpr std::uint8_t Period = 8;
}  // namespace TickFlags

namespace Capabilities {
inline constexpr std::uint32_t None = 0;
inline constexpr std::uint32_t Stats = 1;
inline constexpr std::uint32_t Debug = 2;
}  // namespace Capabilities

namespace SourceStatus {
inline constexpr std::uint8_t Applied = 0;
inline constexpr std::uint8_t Error = 1;
}  // namespace SourceStatus

namespace CloseCode {
inline constexpr int Normal = 1000;
inline constexpr int GoingAway = 1001;
inline constexpr int ProtocolError = 1002;
inline constexpr int MalformedPayload = 1007;
inline constexpr int PolicyViolation = 1008;
inline constexpr int MessageTooBig = 1009;
inline constexpr int InternalError = 1011;
inline constexpr int TryAgainLater = 1013;
inline constexpr int NoAcknowledgement = 4001;
inline constexpr int HelloTimeout = 4002;
inline constexpr int AuthenticationRejected = 4003;
inline constexpr int ClientRefusedTheStream = 4004;
inline constexpr int FirstApplicationCode = 4100;
inline constexpr int LastApplicationCode = 4999;
}  // namespace CloseCode

namespace BuiltIn {
inline constexpr const char* EventsLost = "EventsLost";
inline constexpr int EventsLostIdx = 0;
inline constexpr const char* ClientRegion = "ClientRegion";
inline constexpr int ClientRegionIdx = 0;
inline constexpr const char* SubscribeRequest = "SubscribeRequest";
inline constexpr int SubscribeRequestIdx = 1;
inline constexpr const char* RegionVerticesField = "vertices";
inline constexpr const char* RegionAltitudeField = "altitudeM";
inline constexpr const char* RegionBudgetField = "budgetKiBps";
}  // namespace BuiltIn

// Whether a client may send `code` in BYE: 1000, or 4000-4999.
constexpr bool IsValidClientCloseCode(int code) { return code == CloseCode::Normal || (code >= 4000 && code <= 4999); }

}  // namespace typhon::client
