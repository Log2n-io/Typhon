#pragma once

#include <array>
#include <cstdint>
#include <memory>
#include <optional>

namespace typhon::client {

class WireReader;
class WireWriter;

// The realm a session's positions are framed in (typhon.3, 12-realms § 5.2): what the REALM block carries, and what every
// realm-framed codec — pos2, pos3, the AGG grid — is quantized over and decoded with (SUB-30). Immutable once built.
//
// REALM := u16 realmId | u16 generation | u8 flags [unless NONE: varu kindIdx | u32 appTag | u8 posBits | f64 cellM | f64 min[3] |
// f64 max[3]] — always three axes; a flat realm has deep false and a pos2 uses axes 0 and 1.
class RealmFrame {
public:
    static constexpr std::uint16_t NoRealm = 0xFFFF;

    // Throws std::out_of_range for a frame § 5.2 refuses.
    RealmFrame(std::uint32_t realmId, std::uint32_t generation, std::uint32_t kindIdx, std::uint32_t appTag, int positionBits, double cellM,
               bool deep, const double min[3], const double max[3]);

    std::uint32_t realmId;
    std::uint32_t generation;
    std::uint32_t kindIdx;
    std::uint32_t appTag;
    int positionBits;
    double cellM;
    bool deep;
    std::array<double, 3> min;
    std::array<double, 3> max;
    // The position quantum per axis at positionBits.
    std::array<double, 3> step;
    // The top code, 2^positionBits - 1.
    double top;

    // This frame at the command width (32 bits): what a COMMANDS message's realm-framed fields travel over (SUB-05).
    std::shared_ptr<const RealmFrame> ForCommands() const;

    // An AGG grid's cell count on `axis`: ceil((max - min) / (tileCells * cellM)), and 1 on axis 2 of a flat realm.
    double AggregateDim(int axis, int tileCells) const;

    // An AGG grid's cell count over all three axes, or +infinity past 2^31.
    double AggregateCellCount(int tileCells) const;

    bool Equals(const RealmFrame& other) const;

    // Decodes a REALM block's content: the frame, or null for REALM(NONE). A value § 5.2 refuses is 1007.
    static std::shared_ptr<const RealmFrame> Read(WireReader& r, std::size_t realmKindCount);

    void Write(WireWriter& w) const;
    static void WriteNone(WireWriter& w);

private:
    mutable std::shared_ptr<const RealmFrame> commands_;
};

}  // namespace typhon::client
