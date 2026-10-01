// catalog-*: the canonical JSON parses, validates and compiles; and what a client cannot decode is refused, every problem listed.

#include <cmath>

#include "golden_support.hpp"
#include "test_framework.hpp"
#include "wire/errors.hpp"
#include "wire/math.hpp"

using namespace typhon::client;
using namespace typhon::test;

namespace {

std::vector<std::string> Names(const auto& items)
{
    std::vector<std::string> names;
    for (const auto& item : items)
    {
        names.push_back(item->name);
    }

    return names;
}

std::vector<std::string> NamesOf(const std::vector<FieldPlan*>& fields)
{
    std::vector<std::string> names;
    for (const FieldPlan* f : fields)
    {
        names.push_back(f->name);
    }

    return names;
}

Value KitchenSink()
{
    const auto bin = GoldenBin("catalog-kitchen-sink");
    return json::Parse(std::string_view(reinterpret_cast<const char*>(bin.data()), bin.size()));
}

// Every problem the edited catalog breaks, joined; fails the test when it is accepted.
std::string ProblemsOf(const Value& catalog)
{
    try
    {
        (void)ParseCatalog(std::string_view(catalog.Dump()));
    }
    catch (const CatalogError& e)
    {
        std::string all;
        for (const std::string& p : e.Problems())
        {
            all += p + "\n";
        }

        return all;
    }

    ::typhon::test::Fail(__FILE__, __LINE__, "the catalog was accepted");
}

#define CHECK_CONTAINS(text, part) CHECK_MSG((text).find(part) != std::string::npos, "expected '" << (part) << "' in:\n" << (text))

}  // namespace

TEST(GoldenCatalogs_SwgParsesAndCompilesInWireOrder)
{
    const auto bin = GoldenBin("catalog-swg");
    CHECK_EQ(static_cast<double>(bin.size()), GoldenJson("catalog-swg").Find("byteCount")->AsNumber());
    const auto plan = PlanOf("catalog-swg");

    CHECK(Names(plan->Archetypes()) == (std::vector<std::string>{"Creature", "Player"}));
    const ArchetypePlan& creature = *plan->ArchetypeByName("Creature");
    CHECK_EQ(creature.position->dims, 2);
    CHECK(creature.position->linear);
    CHECK(NamesOf(creature.onEnter->fields) == (std::vector<std::string>{"template"}));
    CHECK_EQ(creature.groupSections.size(), 2u);
    CHECK(NamesOf(creature.groupSections[0].fields) == (std::vector<std::string>{"mode"}));
    CHECK(NamesOf(creature.groupSections[1].fields) == (std::vector<std::string>{"hp"}));
    CHECK_EQ(creature.groupSections[0].packBytes, 1);
    for (std::size_t i = 0; i < creature.fields.size(); i++)
    {
        CHECK_EQ(creature.fields[i]->index, static_cast<int>(i));
    }

    CHECK(creature.fields[1]->enumNames != nullptr);
    CHECK(*creature.fields[1]->enumNames == (std::vector<std::string>{"Idle", "Wander", "Pursue", "Fighting", "Leashing", "Dead"}));
    // The velocity unit is absolute (typhon.3): 2^-13 m per tick, whatever the position's bounds.
    CHECK_EQ(creature.position->vel->velocityUnit, math::Pow2(-13));

    CHECK(NamesOf(plan->ClientRegion()->body->fields) == (std::vector<std::string>{"altitudeM", "budgetKiBps", "vertices"}));
    CHECK_EQ(plan->Command(16).name, std::string("MoveTo"));
    CHECK_EQ(plan->Event(16).name, std::string("Attack"));
    CHECK_THROWS(WireFormatError, (void)plan->Event(0));
    CHECK_EQ(plan->Grids()[0]->tileCells, 1);
    CHECK_EQ(plan->Metrics().size(), 1u);
    CHECK_EQ(plan->Metrics()[0]->name, std::string("typhon.tick.p99"));
}

TEST(GoldenCatalogs_KitchenSinkCompilesEveryConstruct)
{
    const auto plan = PlanOf("catalog-kitchen-sink");
    const auto& a = plan->Archetypes();
    CHECK(Names(a) == (std::vector<std::string>{"Beacon", "Buoy", "Drone", "Ledger"}));
    CHECK(!a[0]->position->moving && a[0]->position->dims == 2);
    CHECK(a[1]->position->moving && !a[1]->position->linear);
    CHECK(a[2]->position->linear && a[2]->position->dims == 3);
    CHECK(a[3]->position == nullptr);

    const ArchetypePlan& drone = *plan->ArchetypeByName("Drone");
    const SectionPlan& flags = drone.groupSections[0];
    CHECK(NamesOf(flags.fields) == (std::vector<std::string>{"armed", "lights", "stance"}));
    CHECK(flags.fields[0]->bitOffset == 0 && flags.fields[0]->bitCount == 1);
    CHECK(flags.fields[1]->bitOffset == 1 && flags.fields[1]->bitCount == 1);
    CHECK(flags.fields[2]->bitOffset == 2 && flags.fields[2]->bitCount == 6);
    CHECK_EQ(flags.packBytes, 1);
    const std::vector<std::pair<std::string, int>> components = {
        {"label", 0},   {"serial", 0},   {"armed", 1},   {"lights", 1},      {"stance", 1}, {"heading", 1}, {"rotation", 4},
        {"thrust", 3},  {"battery", 1},  {"lastHit", 1}, {"target", 1},      {"temperature", 1}, {"tilt", 1},
    };
    CHECK_EQ(drone.fields.size(), components.size());
    for (std::size_t i = 0; i < components.size(); i++)
    {
        CHECK_EQ(drone.fields[i]->name, components[i].first);
        CHECK_EQ(drone.fields[i]->components, components[i].second);
    }

    CHECK(drone.ownerGroups == (std::vector<std::string>{"cargo", "secrets"}));
    CHECK(NamesOf(drone.ownerSections[0].fields) == (std::vector<std::string>{"fuel", "manifest"}));
    CHECK(NamesOf(drone.ownerSections[1].fields) == (std::vector<std::string>{"vault", "pin"}));

    const ArchetypePlan& ledger = *plan->ArchetypeByName("Ledger");
    const FieldPlan* future = nullptr;
    for (const auto& f : ledger.fields)
    {
        future = f->name == "future" ? f.get() : future;
    }

    CHECK(future != nullptr && future->kind == CodecKind::Unknown && future->valueKind == ValueKind::Skipped && future->fixedBytes == 3);

    const FieldPlan* path = nullptr;
    for (const FieldPlan* f : plan->EventByName("Ping")->body->fields)
    {
        path = f->name == "path" ? f : path;
    }

    CHECK(path != nullptr && path->minCount == 0 && path->maxCount == 4 && path->components == 2 && path->element->kind == CodecKind::Pos2);

    const auto& server = plan->ServerMetrics();
    CHECK_EQ(server.size(), 3u);
    CHECK(server[0]->name == "typhon.tick.p50" && server[0]->offset == 0 && server[0]->valueCount == 1);
    CHECK(server[1]->name == "typhon.system.mean" && server[1]->offset == 1 && server[1]->valueCount == 3);
    CHECK(server[2]->name == "app.load" && server[2]->offset == 5 && server[2]->valueCount == 1);
    const auto& session = plan->SessionMetrics();
    CHECK(session.size() == 2 && session[0]->name == "typhon.session.skippedFrames" && session[0]->offset == 4);
    CHECK(session[1]->name == "app.queue" && session[1]->offset == 6);
    CHECK_EQ(plan->MetricValueCount(), 7);
}

TEST(GoldenCatalogs_WideCompilesAtTheLimits)
{
    const auto bin = GoldenBin("catalog-wide");
    const auto plan = PlanOf("catalog-wide");
    const Value json = json::Parse(std::string_view(reinterpret_cast<const char*>(bin.data()), bin.size()));
    const auto& archetypes = json.Find("archetypes")->Items();
    CHECK_EQ(plan->Archetypes().size(), archetypes.size());
    CHECK(plan->Archetypes().size() > 128);
    for (std::size_t i = 0; i < archetypes.size(); i++)
    {
        CHECK_EQ(plan->Archetypes()[i]->name, archetypes[i].Find("name")->AsString());
    }

    double maxEvent = 0;
    for (const Value& e : json.Find("events")->Items())
    {
        CHECK_EQ(plan->Event(static_cast<std::uint32_t>(e.Find("idx")->AsNumber())).name, e.Find("name")->AsString());
        maxEvent = std::max(maxEvent, e.Find("idx")->AsNumber());
    }

    CHECK(maxEvent >= 128);
    bool full = false;
    for (const auto& [name, names] : json.Find("enums")->Members())
    {
        CHECK_EQ(plan->GetCatalog().Enum(name)->size(), names.Items().size());
        full = full || names.Items().size() == 256;
    }

    CHECK(full);
    bool eightGroups = false;
    for (const auto& a : plan->Archetypes())
    {
        eightGroups = eightGroups || a->groups.size() == 8;
    }

    CHECK(eightGroups);
}

TEST(GoldenCatalogs_RefusalsVectorAcceptsAndRefusesAsItSays)
{
    const Value vector = GoldenJson("catalog-refusals");
    CHECK(!vector.Find("cases")->Items().empty());
    for (const Value& c : vector.Find("cases")->Items())
    {
        const std::string& name = c.Find("name")->AsString();
        bool refused = false;
        std::string why;
        try
        {
            (void)ParseCatalog(std::string_view(c.Find("json")->AsString()));
        }
        catch (const CatalogError& e)
        {
            refused = true;
            why = e.what();
        }

        CHECK_MSG(refused != c.Find("accept")->AsBool(), name << (refused ? " refused: " + why : std::string(" accepted")));
    }
}

TEST(GoldenCatalogs_RefusesWhatAClientCannotDecodeListingEveryProblem)
{
    Value catalog = KitchenSink();
    Value& ledger = catalog.FindMutable("archetypes")->Items()[3];
    Value& future = ledger.FindMutable("fields")->Items()[2];
    future = Value::MakeObject({{"name", Value::MakeString("future")},
                                {"codec", Value::MakeObject({{"t", Value::MakeString("future")}})},
                                {"group", Value::MakeString("balance")}});
    std::vector<Value> nine;
    for (const char* g : {"a", "b", "c", "d", "e", "f", "g", "h", "i"})
    {
        nine.push_back(Value::MakeString(g));
    }

    *ledger.FindMutable("groups") = Value::MakeArray(std::move(nine));
    Value& steer = catalog.FindMutable("commands")->Items()[1];
    *steer.FindMutable("fields") = Value::MakeArray({Value::MakeObject(
        {{"name", Value::MakeString("many")},
         {"codec", Value::MakeObject({{"t", Value::MakeString("list")},
                                      {"of", Value::MakeObject({{"t", Value::MakeString("u8")}})},
                                      {"minCount", Value::MakeNumber(5)},
                                      {"maxCount", Value::MakeNumber(4)}})}})});

    const std::string problems = ProblemsOf(catalog);
    CHECK_CONTAINS(problems, "'future' is unknown and declares no usable fixedBytes");
    CHECK_CONTAINS(problems, "9 groups; 0..8 allowed");
    CHECK_CONTAINS(problems, "list counts must satisfy");
    CHECK_CONTAINS(problems, "names group 'balance', which the archetype does not declare");
}

TEST(GoldenCatalogs_RefusesANonCanonicalCatalog)
{
    Value swapped = KitchenSink();
    auto& archetypes = swapped.FindMutable("archetypes")->Items();
    std::swap(archetypes[0], archetypes[1]);
    *archetypes[0].FindMutable("idx") = Value::MakeNumber(0);
    *archetypes[1].FindMutable("idx") = Value::MakeNumber(1);
    CHECK_CONTAINS(ProblemsOf(swapped), "archetype 'Beacon' is not at its ordinal position");

    Value command = KitchenSink();
    *command.FindMutable("commands")->Items()[1].FindMutable("idx") = Value::MakeNumber(2);
    CHECK_CONTAINS(ProblemsOf(command), "command 'Steer' is not at its ordinal index from 16");

    Value metric = KitchenSink();
    metric.FindMutable("metrics")->Items()[3].Set("scope", Value::MakeString("server"));
    CHECK_CONTAINS(ProblemsOf(metric), "metric 'app.load' spells out a default scope or kind");
}

TEST(GoldenCatalogs_RefusesJsonThatIsNotACatalog)
{
    CHECK_THROWS(CatalogError, (void)ParseCatalog(std::string_view("{")));
    CHECK_THROWS(CatalogError, (void)ParseCatalog(std::string_view("[]")));
    const std::uint8_t invalid[] = {0xFF};
    CHECK_THROWS(CatalogError, (void)ParseCatalog(std::span<const std::uint8_t>(invalid)));

    const auto bin = GoldenBin("catalog-swg");
    Value wrongMajor = json::Parse(std::string_view(reinterpret_cast<const char*>(bin.data()), bin.size()));
    wrongMajor.FindMutable("protocol")->Set("major", Value::MakeNumber(4));
    CHECK_CONTAINS(ProblemsOf(wrongMajor), "protocol 4.0; this client speaks major 3");
}
