// The native SDK against a live server (13 § 7): connects to Typhon.Subscriptions.E2EHost over TCP, applies what it is sent, sends a command and waits
// for its echo, then writes what it received and what its replica held after every frame:
//   <out>/record.bin       every message, `u32 len` framed (WELCOME first)
//   <out>/snapshots.json   the replica after each TICK, in the StreamSnapshot shape
// The .NET client replays record.bin and must render the same snapshots, frame for frame (NativeLiveDifferentialTests): a differential check of two
// decoders over the same live bytes. Exit code 0 when the session behaved; each failed expectation is printed.
//
// Usage: typhon_client_live --port N [--host H] [--frames N] --out DIR

#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <string>

#include "alloc_counter.hpp"
#include "client/client.hpp"
#include "snapshot.hpp"
#include "test_framework.hpp"

using namespace typhon::client;
using namespace typhon::test;

// golden_support's checks report through the test runner's Fail; this driver has no runner, so a failed check is an exception.
[[noreturn]] void typhon::test::Fail(const char* file, int line, const std::string& what)
{
    throw TestFailure(std::string(file) + ":" + std::to_string(line) + ": " + what);
}

namespace {

// The echo's 64-bit token (W32): above 2⁵³ and with its top bit set, so a double or a signed misread would not bring it back intact.
constexpr std::uint64_t EchoToken = 0xFEDCBA9876543211ull;

int failures = 0;

// Whether the steady-state allocation measurement is running: this driver's own callbacks (recording, rendering JSON) allocate, and are paused out of it.
bool measuring = false;

struct NotMeasured {
    NotMeasured()
    {
        if (measuring)
        {
            PauseCounting();
        }
    }

    ~NotMeasured()
    {
        if (measuring)
        {
            ResumeCounting();
        }
    }
};

void Expect(bool condition, const std::string& what)
{
    if (!condition)
    {
        std::cerr << "FAIL " << what << "\n";
        failures++;
    }
}

}  // namespace

int main(int argc, char** argv)
{
    std::string host = "127.0.0.1";
    int port = 0;
    std::size_t frames = 60;
    std::filesystem::path out;
    for (int i = 1; i + 1 < argc; i += 2)
    {
        const std::string key = argv[i];
        const std::string value = argv[i + 1];
        if (key == "--host")
        {
            host = value;
        }
        else if (key == "--port")
        {
            port = std::stoi(value);
        }
        else if (key == "--frames")
        {
            frames = static_cast<std::size_t>(std::stoul(value));
        }
        else if (key == "--out")
        {
            out = value;
        }
    }

    if (port <= 0 || out.empty())
    {
        std::cerr << "usage: typhon_client_live --port N [--host H] [--frames N] --out DIR\n";
        return 2;
    }

    std::filesystem::create_directories(out);
    // Installed before the client exists: the hooks are process-wide and must outlive everything allocated under them.
    CountingHooks hooks;
    std::vector<std::uint8_t> record;
    std::vector<Value> snapshots;
    std::vector<Value> events;
    bool echoed = false;
    // State records seen per group bit set, so the run proves it applied group updates and not only motion and enters.
    std::size_t defaultGroupUpdates = 0;
    std::size_t vitalsGroupUpdates = 0;
    std::unique_ptr<Client> client;

    ClientOptions options;
    options.host = host;
    options.port = static_cast<std::uint16_t>(port);
    options.kind = "probe";
    options.maxAttempts = 0;
    options.onMessage = [&](std::span<const std::uint8_t> message, double)
    {
        NotMeasured guard;
        const auto length = static_cast<std::uint32_t>(message.size());
        for (int b = 0; b < 4; b++)
        {
            record.push_back(static_cast<std::uint8_t>(length >> (8 * b)));
        }

        record.insert(record.end(), message.begin(), message.end());
    };
    options.onEvent = [&](const EventRecord& event)
    {
        NotMeasured guard;
        events.push_back(RenderEvent(event, *client->Applier()));
        if (event.Type().name == "E2eEchoed")
        {
            const int token = event.FieldIndex("Token");
            echoed = event.Number("Value") == 4242 && event.Number("Code") == 7 && token >= 0 && event.Integers(token).size() == 1
                     && event.Integers(token)[0] == EchoToken;
        }
    };
    options.onFrame = [&]
    {
        NotMeasured guard;
        const FrameApplier& applier = *client->Applier();
        snapshots.push_back(Render(applier, events));
        events.clear();
        const ArchetypePlan* mover = applier.Plan().ArchetypeByName("E2eMover");
        if (mover != nullptr)
        {
            const ArchetypeStore& store = applier.World().Archetype(static_cast<std::size_t>(mover->idx));
            for (const std::uint32_t slot : store.Updated())
            {
                // Bit i is mover->groups[i]: the default group is listed first, vitals after it.
                for (std::size_t g = 0; g < mover->groups.size(); g++)
                {
                    if ((store.UpdateMask(slot) & (1u << g)) != 0)
                    {
                        (mover->groups[g] == "vitals" ? vitalsGroupUpdates : defaultGroupUpdates)++;
                    }
                }
            }
        }
    };
    options.onClose = [&](const ConnectionClose& close)
    {
        NotMeasured guard;
        if (!close.local)
        {
            std::cerr << "the server closed the session: " << close.code << " " << close.reason << "\n";
        }
    };

    client = std::make_unique<Client>(std::move(options));
    client->Start();
    bool sent = false;
    // E-14 on the live path: frames 30 to 55 — past the warm-up and the command — through Client::Poll over TCP must allocate nothing.
    constexpr std::size_t MeasureFrom = 30;
    constexpr std::size_t MeasureTo = 55;
    AllocationCount steady{~std::size_t{0}, ~std::size_t{0}};
    const double deadline = client->NowMs() + 30000;
    while (snapshots.size() < frames && client->NowMs() < deadline && client->Status() != ClientStatus::GaveUp)
    {
        if (!measuring && snapshots.size() == MeasureFrom)
        {
            measuring = true;
            StartCounting();
        }

        client->Poll(50);
        if (measuring && snapshots.size() >= MeasureTo)
        {
            steady = StopCounting();
            measuring = false;
        }

        // The command goes once the world has arrived, and its echo must come back before the run ends.
        if (!sent && snapshots.size() >= 10 && client->Status() == ClientStatus::Open)
        {
            const double value = 4242;
            const double code = 7;
            const std::uint64_t token = EchoToken;
            const NamedValue values[] = {{"Value", FieldValue::OfNumbers({&value, 1})},
                                         {"Code", FieldValue::OfNumbers({&code, 1})},
                                         {"Token", FieldValue::OfIntegers({&token, 1})}};
            const MessagePlan* echo = client->Applier()->Plan().CommandByName("E2eEcho");
            Expect(echo != nullptr, "the catalog declares E2eEcho");
            if (echo != nullptr)
            {
                Expect(client->Commands()->Enqueue(*echo, values) >= 0, "E2eEcho was queued");
                Expect(client->FlushCommands() == 1, "E2eEcho went out in one message");
            }

            sent = true;
        }
    }

    Expect(snapshots.size() >= frames, "received " + std::to_string(snapshots.size()) + " of " + std::to_string(frames) + " frames");
    Expect(echoed, "the E2eEchoed event carried the command's values back, its u64 token exact");
    Expect(defaultGroupUpdates > 0 && vitalsGroupUpdates > 0, "state records of both groups arrived: " + std::to_string(defaultGroupUpdates) +
                                                                  " default, " + std::to_string(vitalsGroupUpdates) + " vitals");
    Expect(steady.news == 0 && steady.hookAllocs == 0, "no allocation over frames " + std::to_string(MeasureFrom) + "-" + std::to_string(MeasureTo) +
                                                           ": " + std::to_string(steady.news) + " operator new, " +
                                                           std::to_string(steady.hookAllocs) + " hook allocations");
    if (client->Applier() != nullptr)
    {
        const FrameApplier& applier = *client->Applier();
        Expect(applier.World().anomalies == 0, "no anomalies, got " + std::to_string(applier.World().anomalies));
        const auto live = [&](const char* name)
        { return applier.World().Archetype(static_cast<std::size_t>(applier.Plan().ArchetypeByName(name)->idx)).LiveCount(); };
        Expect(live("E2eMover") == 12, "12 movers held, got " + std::to_string(live("E2eMover")));
        Expect(live("E2eRock") == 4, "4 rocks held, got " + std::to_string(live("E2eRock")));

        // The exact wire live (W32, W33): every mover's credits sit above 2^53 in a u64 column, its debt below -2^53 in an i64 one, its aim is
        // three floats — the store's own typed columns, not a double that would have rounded them.
        const ArchetypePlan& moverPlan = *applier.Plan().ArchetypeByName("E2eMover");
        const ArchetypeStore& movers = applier.World().Archetype(static_cast<std::size_t>(moverPlan.idx));
        const int credits = movers.FieldIndex("credits");
        const int debt = movers.FieldIndex("debt");
        const int aim = movers.FieldIndex("aim");
        Expect(credits >= 0 && movers.Schema().fields[static_cast<std::size_t>(credits)].kind == FieldKind::U64, "credits is a u64 column");
        Expect(debt >= 0 && movers.Schema().fields[static_cast<std::size_t>(debt)].kind == FieldKind::I64, "debt is an i64 column");
        Expect(aim >= 0 && movers.Schema().fields[static_cast<std::size_t>(aim)].components == 3, "aim is three components");
        for (const std::uint32_t slot : movers.Live())
        {
            if (credits < 0 || debt < 0)
            {
                break;
            }

            Expect(movers.IntegerAt(credits, slot) > (std::uint64_t{1} << 53), "credits above 2^53");
            Expect(static_cast<std::int64_t>(movers.IntegerAt(debt, slot)) < -(std::int64_t{1} << 53), "debt below -2^53");
        }

        // Text (13 § 6): every mover's label, out of a wide group, intact through its two-byte characters.
        const int label = movers.FieldIndex("label");
        Expect(label >= 0 && movers.Schema().fields[static_cast<std::size_t>(label)].kind == FieldKind::Text, "label is a text field");
        for (const std::uint32_t slot : movers.Live())
        {
            if (label < 0)
            {
                break;
            }

            const std::string_view text = movers.TextAt(label, slot);
            Expect(text.starts_with("mover ") && text.ends_with(", d\xC3\xA9j\xC3\xA0 vu"), "a mover's label, got '" + std::string(text) + "'");
        }
    }
    else
    {
        Expect(false, "no session ever opened");
    }

    client->Stop();
    for (int i = 0; i < 4; i++)
    {
        client->Poll(10);
    }

    {
        std::ofstream file(out / "record.bin", std::ios::binary);
        file.write(reinterpret_cast<const char*>(record.data()), static_cast<std::streamsize>(record.size()));
    }

    {
        std::ofstream file(out / "snapshots.json", std::ios::binary);
        file << Value::MakeArray(std::move(snapshots)).Dump();
    }

    std::cout << (failures == 0 ? "native live: ok" : "native live: FAILED") << "\n";
    return failures == 0 ? 0 : 1;
}
