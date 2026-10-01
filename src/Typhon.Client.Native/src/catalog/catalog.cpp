#include "catalog/catalog.hpp"

#include <cmath>

#include "json/json.hpp"
#include "wire/utf8.hpp"

namespace typhon::client {

namespace {

using json::Value;

// Reads the JSON into the typed model, checking every value's JSON type as the C# model would deserialize it: a required array or
// object that is missing or null is refused (canonical form writes []), an absent integer reads as 0 (the C# default), and every
// integer must fit a C# int. The TypeScript SDK's readCatalog, rule for rule.
class Reader {
public:
    explicit Reader(std::vector<std::string>& problems) : p_(problems) {}

    Catalog ReadCatalog(const Value& raw)
    {
        Catalog c;
        const Value& o = AsObject(&raw, "catalog");
        const Value& protocol = AsObject(o.Find("protocol"), "protocol");
        const Value& app = AsObject(o.Find("app"), "app");
        const Value& tick = AsObject(o.Find("tick"), "tick");
        const Value& limits = AsObject(o.Find("limits"), "limits");
        const Value& enums = AsObject(o.Find("enums"), "enums");
        for (const auto& [key, value] : enums.Members())
        {
            auto names = StringArray(&value, "enums." + key);
            bool replaced = false;
            for (auto& entry : c.enums)
            {
                if (entry.first == key)
                {
                    entry.second = std::move(names);
                    replaced = true;
                    break;
                }
            }

            if (!replaced)
            {
                c.enums.emplace_back(key, std::move(names));
            }
        }

        c.protocolMajor = Int(protocol, "major", "protocol");
        c.protocolMinor = Int(protocol, "minor", "protocol");
        c.appName = Str(app, "name", "app");
        c.appRevision = Int(app, "revision", "app");
        c.tickPeriodUs = Int(tick, "periodUs", "tick");
        c.pingHz = Int(tick, "pingHz", "tick");
        c.frameBytes = Int(limits, "frameBytes", "limits");
        c.clientMessageBytes = Int(limits, "clientMessageBytes", "limits");
        c.resumeGraceMs = Int(limits, "resumeGraceMs", "limits");
        c.sessionKinds = StringArray(o.Find("sessionKinds"), "sessionKinds");
        if (o.Find("realmKinds") != nullptr)
        {
            c.realmKinds = StringArray(o.Find("realmKinds"), "realmKinds");
        }

        List(o.Find("archetypes"), "archetypes", [&](const Value& a, const std::string& where) { c.archetypes.push_back(ReadArchetype(a, where)); });
        List(o.Find("events"), "events",
             [&](const Value& e, const std::string& where)
             {
                 CatalogEvent event;
                 event.idx = Int(e, "idx", where);
                 event.name = Str(e, "name", where);
                 event.scope = Str(e, "scope", where);
                 List(e.Find("fields"), where + ".fields", [&](const Value& f, const std::string& at) { event.fields.push_back(ReadField(f, at)); });
                 c.events.push_back(std::move(event));
             });
        List(o.Find("commands"), "commands", [&](const Value& cmd, const std::string& where) { c.commands.push_back(ReadCommand(cmd, where)); });
        List(o.Find("grids"), "grids",
             [&](const Value& g, const std::string& where)
             {
                 CatalogGrid grid;
                 grid.idx = Int(g, "idx", where);
                 grid.tileCells = Int(g, "tileCells", where);
                 grid.archetypes = IntArray(g.Find("archetypes"), where + ".archetypes");
                 c.grids.push_back(std::move(grid));
             });
        List(o.Find("metrics"), "metrics", [&](const Value& m, const std::string& where) { c.metrics.push_back(ReadMetric(m, where)); });
        return c;
    }

private:
    CatalogArchetype ReadArchetype(const Value& a, const std::string& where)
    {
        CatalogArchetype archetype;
        archetype.idx = Int(a, "idx", where);
        archetype.name = Str(a, "name", where);
        archetype.groups = StringArray(a.Find("groups"), where + ".groups");
        List(a.Find("fields"), where + ".fields", [&](const Value& f, const std::string& at) { archetype.fields.push_back(ReadField(f, at)); });

        if (Present(a.Find("position")))
        {
            const std::string at = where + ".position";
            const Value& o = AsObject(a.Find("position"), at);
            CatalogPosition position;
            position.kind = Str(o, "kind", at);
            position.pos = ReadCodec(o.Find("pos"), at + ".pos", false);
            position.model = OptionalString(o, "model", at);
            if (Present(o.Find("vel")))
            {
                position.vel = ReadCodec(o.Find("vel"), at + ".vel", false);
            }

            archetype.position = std::move(position);
        }

        if (Present(a.Find("owner")))
        {
            const std::string at = where + ".owner";
            const Value& o = AsObject(a.Find("owner"), at);
            CatalogOwner owner;
            owner.groups = StringArray(o.Find("groups"), at + ".groups");
            List(o.Find("fields"), at + ".fields", [&](const Value& f, const std::string& fieldAt) { owner.fields.push_back(ReadField(f, fieldAt)); });
            archetype.owner = std::move(owner);
        }

        return archetype;
    }

    CatalogCommand ReadCommand(const Value& c, const std::string& where)
    {
        CatalogCommand command;
        command.idx = Int(c, "idx", where);
        command.name = Str(c, "name", where);
        command.delivery = Str(c, "delivery", where);
        List(c.Find("fields"), where + ".fields", [&](const Value& f, const std::string& at) { command.fields.push_back(ReadField(f, at)); });
        if (Present(c.Find("rate")))
        {
            const Value& rate = AsObject(c.Find("rate"), where + ".rate");
            command.rate = CatalogCommandRate{Int(rate, "perSec", where + ".rate"), Int(rate, "burst", where + ".rate")};
        }

        return command;
    }

    CatalogMetric ReadMetric(const Value& m, const std::string& where)
    {
        CatalogMetric metric;
        metric.idx = Int(m, "idx", where);
        metric.name = Str(m, "name", where);
        metric.unit = Str(m, "unit", where);
        metric.codec = ReadCodec(m.Find("codec"), where + ".codec", false);
        metric.scope = OptionalString(m, "scope", where);
        metric.kind = OptionalString(m, "kind", where);
        if (Present(m.Find("labels")))
        {
            metric.labels = StringArray(m.Find("labels"), where + ".labels");
        }

        return metric;
    }

    CatalogField ReadField(const Value& f, const std::string& where)
    {
        CatalogField field;
        field.name = Str(f, "name", where);
        field.codec = ReadCodec(f.Find("codec"), where + ".codec", false);
        field.group = OptionalString(f, "group", where);
        if (Present(f.Find("onEnter")))
        {
            if (f.Find("onEnter")->IsBool())
            {
                field.onEnter = f.Find("onEnter")->AsBool();
            }
            else
            {
                p_.push_back(where + ".onEnter must be a boolean");
            }
        }

        field.enumName = OptionalString(f, "enum", where);
        field.smoothing = OptionalString(f, "smoothing", where);
        return field;
    }

    // A codec, and for a list its element. An element's own `of` is refused rather than read, so the recursion is one level deep
    // however far the JSON nests.
public:
    CatalogCodec ReadCodec(const Value* raw, const std::string& where, bool element)
    {
        const Value& c = AsObject(raw, where);
        CatalogCodec codec;
        codec.t = Str(c, "t", where);
        const auto integer = [&](const char* key, std::optional<int>& slot)
        {
            if (c.Find(key) != nullptr)
            {
                slot = Int(c, key, where);
            }
        };
        integer("bits", codec.bits);
        integer("unitExp", codec.unitExp);
        integer("n", codec.n);
        integer("maxBytes", codec.maxBytes);
        integer("minCount", codec.minCount);
        integer("maxCount", codec.maxCount);
        integer("fixedBytes", codec.fixedBytes);
        if (c.Find("scale") != nullptr)
        {
            codec.scale = Num(c, "scale", where);
        }

        if (Present(c.Find("min")))
        {
            codec.min = NumberArray(c.Find("min"), where + ".min");
        }

        if (Present(c.Find("max")))
        {
            codec.max = NumberArray(c.Find("max"), where + ".max");
        }

        if (Present(c.Find("of")))
        {
            if (element)
            {
                p_.push_back(where + ".of: a list element cannot itself be a list");
            }
            else
            {
                codec.of = std::make_shared<const CatalogCodec>(ReadCodec(c.Find("of"), where + ".of", true));
            }
        }

        return codec;
    }

private:
    static bool Present(const Value* v) { return v != nullptr && !v->IsNull(); }

    const Value& AsObject(const Value* v, const std::string& where)
    {
        static const Value Empty = Value::MakeObject();
        if (v != nullptr && v->IsObject())
        {
            return *v;
        }

        p_.push_back(where + " must be an object");
        return Empty;
    }

    template <typename F>
    void List(const Value* v, const std::string& where, F item)
    {
        if (v == nullptr || !v->IsArray())
        {
            p_.push_back(where + " must be an array");
            return;
        }

        const auto& items = v->Items();
        for (std::size_t i = 0; i < items.size(); i++)
        {
            const std::string at = where + "[" + std::to_string(i) + "]";
            item(AsObject(&items[i], at), at);
        }
    }

    std::string Str(const Value& o, const char* key, const std::string& where)
    {
        const Value* v = o.Find(key);
        if (v != nullptr && v->IsString())
        {
            return v->AsString();
        }

        p_.push_back(where + "." + key + " must be a string");
        return {};
    }

    std::optional<std::string> OptionalString(const Value& o, const char* key, const std::string& where)
    {
        const Value* v = o.Find(key);
        if (v != nullptr && v->IsString())
        {
            return v->AsString();
        }

        if (Present(v))
        {
            p_.push_back(where + "." + key + " must be a string");
        }

        return std::nullopt;
    }

    // A number; absent reads as 0, the C# default. Null is refused: C# does not deserialize it into a non-nullable one.
    double Num(const Value& o, const char* key, const std::string& where)
    {
        const Value* v = o.Find(key);
        if (v != nullptr && v->IsNumber())
        {
            return v->AsNumber();
        }

        if (v != nullptr)
        {
            p_.push_back(where + "." + key + " must be a number");
        }

        return 0;
    }

    static bool IsInt32(double v) { return std::isfinite(v) && std::floor(v) == v && v >= -2147483648.0 && v <= 2147483647.0; }

    int Int(const Value& o, const char* key, const std::string& where)
    {
        const double v = Num(o, key, where);
        if (!IsInt32(v))
        {
            p_.push_back(where + "." + key + " must be an integer in [-2^31, 2^31 - 1]");
            return 0;
        }

        return static_cast<int>(v);
    }

    std::vector<std::string> StringArray(const Value* v, const std::string& where)
    {
        std::vector<std::string> result;
        bool ok = v != nullptr && v->IsArray();
        if (ok)
        {
            for (const Value& item : v->Items())
            {
                ok = ok && item.IsString();
            }
        }

        if (!ok)
        {
            p_.push_back(where + " must be an array of strings");
            return result;
        }

        for (const Value& item : v->Items())
        {
            result.push_back(item.AsString());
        }

        return result;
    }

    std::vector<double> NumberArray(const Value* v, const std::string& where)
    {
        std::vector<double> result;
        bool ok = v != nullptr && v->IsArray();
        if (ok)
        {
            for (const Value& item : v->Items())
            {
                ok = ok && item.IsNumber();
            }
        }

        if (!ok)
        {
            p_.push_back(where + " must be an array of numbers");
            return result;
        }

        for (const Value& item : v->Items())
        {
            result.push_back(item.AsNumber());
        }

        return result;
    }

    std::vector<int> IntArray(const Value* v, const std::string& where)
    {
        std::vector<int> result;
        bool ok = v != nullptr && v->IsArray();
        if (ok)
        {
            for (const Value& item : v->Items())
            {
                ok = ok && item.IsNumber() && IsInt32(item.AsNumber());
            }
        }

        if (!ok)
        {
            p_.push_back(where + " must be an array of integers in [-2^31, 2^31 - 1]");
            return result;
        }

        for (const Value& item : v->Items())
        {
            result.push_back(static_cast<int>(item.AsNumber()));
        }

        return result;
    }

    std::vector<std::string>& p_;
};

std::string Join(const std::vector<std::string>& problems)
{
    std::string text = "the catalog breaks " + std::to_string(problems.size()) + " rule(s):";
    for (const std::string& problem : problems)
    {
        text += "\n  - " + problem;
    }

    return text;
}

}  // namespace

const std::vector<std::string>* Catalog::Enum(std::string_view name) const
{
    for (const auto& [key, names] : enums)
    {
        if (key == name)
        {
            return &names;
        }
    }

    return nullptr;
}

CatalogError::CatalogError(std::vector<std::string> problems) : std::runtime_error(Join(problems)), problems_(std::move(problems)) {}

CatalogCodec CodecFromJson(const json::Value& codec)
{
    std::vector<std::string> problems;
    CatalogCodec result = Reader(problems).ReadCodec(&codec, "codec", false);
    if (!problems.empty())
    {
        throw CatalogError(std::move(problems));
    }

    return result;
}

Catalog ParseCatalog(std::span<const std::uint8_t> utf8Json)
{
    if (!utf8::IsValid(utf8Json))
    {
        throw CatalogError({"catalog JSON is not valid UTF-8"});
    }

    return ParseCatalog(std::string_view(reinterpret_cast<const char*>(utf8Json.data()), utf8Json.size()));
}

Catalog ParseCatalog(std::string_view utf8Json)
{
    if (!utf8::IsValid({reinterpret_cast<const std::uint8_t*>(utf8Json.data()), utf8Json.size()}))
    {
        throw CatalogError({"catalog JSON is not valid UTF-8"});
    }

    Value raw;
    try
    {
        raw = json::Parse(utf8Json);
    }
    catch (const json::ParseError& e)
    {
        throw CatalogError({std::string("catalog JSON does not parse: ") + e.what()});
    }

    std::vector<std::string> problems;
    Catalog catalog = Reader(problems).ReadCatalog(raw);
    if (problems.empty())
    {
        ValidateCatalog(catalog, problems);
    }

    if (problems.empty())
    {
        CheckCanonical(catalog, problems);
    }

    if (!problems.empty())
    {
        throw CatalogError(std::move(problems));
    }

    return catalog;
}

}  // namespace typhon::client
