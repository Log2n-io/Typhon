// Hostile input: every golden message corrupted byte by byte and truncated at every length must be refused with the protocol's own error —
// WireFormatError for a frame, CatalogError for a catalog — and nothing else: no other exception, no crash, and (on the gate's ASan/UBSan
// leg) no undefined behaviour. Deterministic, so a failure names the vector, the offset and the mutation.

#include <cstring>
#include <functional>
#include <iterator>
#include <typeinfo>
#include <string>
#include <vector>

#include "apply/frame_applier.hpp"
#include "golden_support.hpp"
#include "json/json.hpp"
#include "snapshot.hpp"
#include "test_framework.hpp"
#include "wire/errors.hpp"
#include "wire/messages.hpp"
#include "wire/tick_reader.hpp"

using namespace typhon::client;
using namespace typhon::test;

namespace {

// The byte values a mutation writes: bit flips at both ends, and the extremes.
constexpr std::uint8_t Masks[] = {0x01, 0x80, 0xFF};
constexpr std::uint8_t Values[] = {0x00, 0xFF, 0x7F};

// How thoroughly a sweep corrupts: every mutation at every byte and every truncation, or one mutation per byte (rotating through them, so
// neighbouring bytes see different ones) and a truncation every 16 bytes plus the last 64 — for the large vectors, whose exhaustive sweep
// costs tens of seconds in a Debug build for little more coverage.
enum class Depth
{
    Exhaustive,
    Rotating,
};

// Runs `decode` over the corruptions of `message`; fails on any exception but `Allowed`.
template <class Allowed>
void Sweep(const std::string& name, const std::vector<std::uint8_t>& message, const std::function<void(std::span<const std::uint8_t>)>& decode,
           std::size_t& runs, Depth depth = Depth::Exhaustive)
{
    const auto attempt = [&](std::span<const std::uint8_t> input, const std::string& what)
    {
        runs++;
        try
        {
            decode(input);
        }
        catch (const Allowed&)
        {
        }
        catch (const TestFailure&)
        {
            throw;
        }
        catch (const std::exception& e)
        {
            Fail(__FILE__, __LINE__, name + " " + what + ": " + typeid(e).name() + ": " + e.what());
        }
    };

    constexpr std::size_t MutationCount = std::size(Masks) + std::size(Values);
    std::vector<std::uint8_t> copy = message;
    for (std::size_t i = 0; i < message.size(); i++)
    {
        for (std::size_t m = 0; m < MutationCount; m++)
        {
            if (depth == Depth::Rotating && m != i % MutationCount)
            {
                continue;
            }

            const bool isMask = m < std::size(Masks);
            copy[i] = isMask ? static_cast<std::uint8_t>(message[i] ^ Masks[m]) : Values[m - std::size(Masks)];
            attempt(copy, "byte " + std::to_string(i) + (isMask ? " ^ " : " = ") + std::to_string(isMask ? Masks[m] : Values[m - std::size(Masks)]));
        }

        copy[i] = message[i];
    }

    for (std::size_t length = 0; length < message.size(); length++)
    {
        if (depth == Depth::Rotating && length % 16 != 0 && length + 64 < message.size())
        {
            continue;
        }

        attempt(std::span<const std::uint8_t>(message.data(), length), "truncated to " + std::to_string(length));
    }
}

}  // namespace

TEST(Corruption_EveryTickVectorRefusesOnlyWithAWireFormatError)
{
    std::size_t runs = 0;
    for (const std::string& name : GoldenNames("tick-"))
    {
        const Value vector = GoldenJson(name);
        const auto plan = PlanOf(vector.Find("catalog")->AsString());
        const auto frame = FrameFromJson(vector.Find("frame"));
        TickReader reader(plan);
        RecordingSink sink;
        Sweep<WireFormatError>(name, GoldenBin(name),
                               [&](std::span<const std::uint8_t> input)
                               {
                                   reader.realm = frame;
                                   sink.log.clear();
                                   reader.Read(input, sink);
                               },
                               runs, Depth::Rotating);
    }

    CHECK(runs > 1000);
}

TEST(Corruption_EveryStreamFrameRefusesOnlyWithAWireFormatError)
{
    // Through the applier: the decoder AND the replica it writes into. Each corruption is applied to a replica that already holds the
    // stream up to that frame, so enters, leaves and updates land on live state.
    std::size_t runs = 0;
    for (const char* name : {"stream-kitchen-sink", "stream-motion", "stream-engine", "stream-engine-3d"})
    {
        const auto messages = Unframe(GoldenBin(name));
        std::shared_ptr<const CatalogPlan> plan;
        std::size_t first = 0;
        if (messages[0][0] == MessageType::Welcome)
        {
            const WelcomeMessage welcome = ParseWelcome(messages[0]);
            plan = CatalogPlan::Compile(std::make_shared<const Catalog>(ParseCatalog(std::span<const std::uint8_t>(welcome.catalogJson))));
            first = 1;
        }
        else
        {
            plan = PlanOf(GoldenJson(name).Find("catalog")->AsString());
        }

        for (std::size_t f = first; f < messages.size(); f++)
        {
            // One replica per frame, carried through every corruption of it: a refused frame leaves part of itself applied, and the next
            // corruption lands on that — harder on the store than a fresh replica each time, and the property is the same.
            FrameApplier applier(plan);
            for (std::size_t k = first; k < f; k++)
            {
                applier.Apply(messages[k]);
            }

            Sweep<WireFormatError>(std::string(name) + " frame " + std::to_string(f), messages[f],
                                   [&](std::span<const std::uint8_t> input) { applier.Apply(input); }, runs);
        }
    }

    CHECK(runs > 1000);
}

TEST(Corruption_EveryCatalogRefusesOnlyWithACatalogError)
{
    std::size_t runs = 0;
    for (const char* name : {"catalog-kitchen-sink", "catalog-swg"})
    {
        Sweep<CatalogError>(name, GoldenBin(name),
                            [](std::span<const std::uint8_t> input)
                            {
                                auto catalog = std::make_shared<const Catalog>(ParseCatalog(input));
                                (void)CatalogPlan::Compile(std::move(catalog));
                            },
                            runs, Depth::Rotating);
    }

    CHECK(runs > 1000);
}
