using System.Globalization;
using System.Text;
using Typhon.Workbench.Dtos.Query;

namespace Typhon.Workbench.Services.Querying;

/// <summary>
/// Recursive-descent parser for the Query Console DSL. Produces a <see cref="QuerySpecDto"/> + a diagnostic array. Never
/// throws on user input — every malformed construct surfaces as a <see cref="ParseErrorDto"/> with line + column. On
/// errors the parser performs stage-keyword recovery (skip tokens until the next FROM/WITH/...) so chip mode can still
/// rebuild from a partial parse.
/// </summary>
/// <remarks>
/// Grammar source: <c>claude/design/Apps/Workbench/views/query-console.md</c> §5.1. Phase-1 scope: every clause parses,
/// but <c>NAVIGATE</c> and <c>SPATIAL</c> are stored in the spec without compiler emission (forward-compatible for
/// Phase 3+). Keyword matching is case-insensitive ("FROM" == "from"); identifiers are case-sensitive (CLR convention).
/// </remarks>
public static class DslParser
{
    /// <summary>Default <c>TAKE</c> when the DSL omits one — Query Console policy (design §4.6).</summary>
    public const int DefaultTake = 1000;

    /// <summary>Parses a DSL string. The returned spec is always non-null; <c>errors</c> is empty on a clean parse.</summary>
    public static QueryParseResponse Parse(string dsl)
    {
        var errors = new List<ParseErrorDto>();
        var tokens = Lexer.Tokenize(dsl ?? string.Empty, errors);
        var spec = new ParseContext(tokens, errors).ParseQuery();
        return new QueryParseResponse(spec, errors.ToArray());
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Tokens
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    internal enum TokenKind
    {
        Eof,
        Identifier,
        Keyword,
        IntLiteral,
        FloatLiteral,
        StringLiteral,
        BoolLiteral,
        OpEq,
        OpNeq,
        OpGt,
        OpLt,
        OpGte,
        OpLte,
        Minus,
        Arrow,
        Dot,
        Comma,
        LParen,
        RParen,
        Hash,                                // '#' — only meaningful prefix to an archetype id (e.g. '#2001')
    }

    internal readonly struct Token
    {
        public readonly TokenKind Kind;
        public readonly string Text;
        public readonly object Value;
        public readonly int Line;
        public readonly int Column;

        public Token(TokenKind kind, string text, object value, int line, int column)
        {
            Kind = kind;
            Text = text;
            Value = value;
            Line = line;
            Column = column;
        }

        public override string ToString() => $"{Kind}('{Text}') @{Line}:{Column}";
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Lexer
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    private static class Lexer
    {
        // Sorted by descending length so the lexer matches the longest operator first ('>=' before '>').
        private static readonly (string Text, TokenKind Kind)[] OperatorTable =
        [
            ("==", TokenKind.OpEq),
            ("!=", TokenKind.OpNeq),
            (">=", TokenKind.OpGte),
            ("<=", TokenKind.OpLte),
            ("->", TokenKind.Arrow),
            (">", TokenKind.OpGt),
            ("<", TokenKind.OpLt),
            ("-", TokenKind.Minus),
        ];

        // Case-insensitive lookup. All DSL keywords (case-folded) — used so the lexer can re-classify identifiers as
        // keywords for the parser's keyword-aware branches without forcing the user into ALL CAPS.
        private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "FROM", "POLYMORPHIC", "EXACT",
            "WITH", "WITHOUT", "EXCLUDE",
            "ENABLED", "DISABLED",
            "WHERE", "AND", "OR",
            "SELECT",
            "SPATIAL", "NEARBY", "AABB", "RAY", "RADIUS",
            "NAVIGATE",
            "ORDER", "BY", "ASC", "DESC",
            "SKIP", "TAKE",
            "AT", "REVISION", "HEAD", "TICK", "TIME",
        };

        public static List<Token> Tokenize(string src, List<ParseErrorDto> errors)
        {
            var tokens = new List<Token>();
            var line = 1;
            var column = 1;
            var i = 0;

            while (i < src.Length)
            {
                var c = src[i];

                // Whitespace + line tracking (\r\n, \r, \n all count as one newline).
                if (c == '\r' || c == '\n')
                {
                    if (c == '\r' && i + 1 < src.Length && src[i + 1] == '\n')
                    {
                        i++;
                    }
                    line++;
                    column = 1;
                    i++;
                    continue;
                }
                if (char.IsWhiteSpace(c))
                {
                    i++;
                    column++;
                    continue;
                }

                // Line comments — '--' to end of line, matching SQL-style commentary so users can annotate saved queries.
                if (c == '-' && i + 1 < src.Length && src[i + 1] == '-')
                {
                    while (i < src.Length && src[i] != '\r' && src[i] != '\n')
                    {
                        i++;
                        column++;
                    }
                    continue;
                }

                var startLine = line;
                var startColumn = column;

                // String literal (double-quoted, simple escapes).
                if (c == '"')
                {
                    var sb = new StringBuilder();
                    i++;
                    column++;
                    var terminated = false;
                    while (i < src.Length)
                    {
                        var sc = src[i];
                        if (sc == '"')
                        {
                            terminated = true;
                            i++;
                            column++;
                            break;
                        }
                        if (sc == '\\' && i + 1 < src.Length)
                        {
                            var esc = src[i + 1];
                            sb.Append(esc switch
                            {
                                '"' => '"',
                                '\\' => '\\',
                                'n' => '\n',
                                't' => '\t',
                                'r' => '\r',
                                _ => esc,
                            });
                            i += 2;
                            column += 2;
                            continue;
                        }
                        if (sc == '\r' || sc == '\n')
                        {
                            break;
                        }
                        sb.Append(sc);
                        i++;
                        column++;
                    }
                    if (!terminated)
                    {
                        errors.Add(new ParseErrorDto(startLine, startColumn, "Unterminated string literal."));
                    }
                    tokens.Add(new Token(TokenKind.StringLiteral, sb.ToString(), sb.ToString(), startLine, startColumn));
                    continue;
                }

                // Numeric literal — optional leading '-' is handled by the parser (binds to the value position),
                // never by the lexer (to keep '5-3' parseable when we expose arithmetic later).
                if (char.IsDigit(c))
                {
                    var start = i;
                    var sawDot = false;
                    while (i < src.Length && (char.IsDigit(src[i]) || (!sawDot && src[i] == '.' && i + 1 < src.Length && char.IsDigit(src[i + 1]))))
                    {
                        if (src[i] == '.') sawDot = true;
                        i++;
                        column++;
                    }
                    var numText = src[start..i];
                    if (sawDot)
                    {
                        if (double.TryParse(numText, NumberStyles.Float, CultureInfo.InvariantCulture, out var dv))
                        {
                            tokens.Add(new Token(TokenKind.FloatLiteral, numText, dv, startLine, startColumn));
                        }
                        else
                        {
                            errors.Add(new ParseErrorDto(startLine, startColumn, $"Invalid float literal '{numText}'."));
                        }
                    }
                    else
                    {
                        if (long.TryParse(numText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iv))
                        {
                            tokens.Add(new Token(TokenKind.IntLiteral, numText, iv, startLine, startColumn));
                        }
                        else
                        {
                            errors.Add(new ParseErrorDto(startLine, startColumn, $"Invalid integer literal '{numText}'."));
                        }
                    }
                    continue;
                }

                // Identifier / keyword / bool literal.
                if (char.IsLetter(c) || c == '_')
                {
                    var start = i;
                    while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_'))
                    {
                        i++;
                        column++;
                    }
                    var idText = src[start..i];
                    if (string.Equals(idText, "true", StringComparison.OrdinalIgnoreCase))
                    {
                        tokens.Add(new Token(TokenKind.BoolLiteral, idText, true, startLine, startColumn));
                    }
                    else if (string.Equals(idText, "false", StringComparison.OrdinalIgnoreCase))
                    {
                        tokens.Add(new Token(TokenKind.BoolLiteral, idText, false, startLine, startColumn));
                    }
                    else if (Keywords.Contains(idText))
                    {
                        tokens.Add(new Token(TokenKind.Keyword, idText.ToUpperInvariant(), null, startLine, startColumn));
                    }
                    else
                    {
                        tokens.Add(new Token(TokenKind.Identifier, idText, null, startLine, startColumn));
                    }
                    continue;
                }

                // Punctuation.
                if (c == '#')
                {
                    // Reserved purely for the '#<archetypeId>' shorthand the Workbench's schema browser uses
                    // (engine identifies archetypes by their [Archetype] id; there is no friendly name).
                    tokens.Add(new Token(TokenKind.Hash, "#", null, startLine, startColumn));
                    i++;
                    column++;
                    continue;
                }
                if (c == '.')
                {
                    tokens.Add(new Token(TokenKind.Dot, ".", null, startLine, startColumn));
                    i++;
                    column++;
                    continue;
                }
                if (c == ',')
                {
                    tokens.Add(new Token(TokenKind.Comma, ",", null, startLine, startColumn));
                    i++;
                    column++;
                    continue;
                }
                if (c == '(')
                {
                    tokens.Add(new Token(TokenKind.LParen, "(", null, startLine, startColumn));
                    i++;
                    column++;
                    continue;
                }
                if (c == ')')
                {
                    tokens.Add(new Token(TokenKind.RParen, ")", null, startLine, startColumn));
                    i++;
                    column++;
                    continue;
                }

                // Multi-char operators (== / != / >= / <= / ->) — match longest first via OperatorTable.
                var matchedOp = false;
                for (var op = 0; op < OperatorTable.Length; op++)
                {
                    var opText = OperatorTable[op].Text;
                    if (i + opText.Length <= src.Length && src.AsSpan(i, opText.Length).SequenceEqual(opText.AsSpan()))
                    {
                        tokens.Add(new Token(OperatorTable[op].Kind, opText, null, startLine, startColumn));
                        i += opText.Length;
                        column += opText.Length;
                        matchedOp = true;
                        break;
                    }
                }
                if (matchedOp)
                {
                    continue;
                }

                // Unknown character — emit a diagnostic and skip so parsing can continue.
                errors.Add(new ParseErrorDto(startLine, startColumn, $"Unexpected character '{c}'."));
                i++;
                column++;
            }

            tokens.Add(new Token(TokenKind.Eof, string.Empty, null, line, column));
            return tokens;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Parse context — token cursor + error list, with stage-keyword recovery.
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    private sealed class ParseContext
    {
        private readonly List<Token> _tokens;
        private readonly List<ParseErrorDto> _errors;
        private int _pos;

        // Stage keywords used as recovery anchors — when an error is encountered we skip tokens until we hit one of
        // these (or EOF), then resume parsing from the next stage. Keeps the parser productive for chip-mode rebuild
        // even on heavily-malformed input.
        private static readonly HashSet<string> StageKeywords = new(StringComparer.Ordinal)
        {
            "FROM", "WITH", "WITHOUT", "EXCLUDE", "ENABLED", "DISABLED",
            "WHERE", "SELECT", "SPATIAL", "NAVIGATE", "ORDER", "SKIP", "TAKE", "AT",
        };

        public ParseContext(List<Token> tokens, List<ParseErrorDto> errors)
        {
            _tokens = tokens;
            _errors = errors;
            _pos = 0;
        }

        private Token Peek(int offset = 0) => _pos + offset < _tokens.Count ? _tokens[_pos + offset] : _tokens[^1];

        private Token Consume() => _tokens[_pos++];

        private bool IsKeyword(string kw, int offset = 0)
        {
            var t = Peek(offset);
            return t.Kind == TokenKind.Keyword && t.Text == kw;
        }

        private bool TryConsumeKeyword(string kw)
        {
            if (IsKeyword(kw))
            {
                _pos++;
                return true;
            }
            return false;
        }

        private void Error(Token at, string message) =>
            _errors.Add(new ParseErrorDto(at.Line, at.Column, message));

        private void RecoverToStage()
        {
            while (Peek().Kind != TokenKind.Eof &&
                   !(Peek().Kind == TokenKind.Keyword && StageKeywords.Contains(Peek().Text)))
            {
                _pos++;
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
        // Top-level: query
        // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════

        public QuerySpecDto ParseQuery()
        {
            var archetype = string.Empty;
            var polymorphic = true;
            var with = new List<string>();
            var without = new List<string>();
            var exclude = new List<string>();
            var enabled = new List<string>();
            var disabled = new List<string>();
            PredicateNodeDto where = null;
            var select = new List<string>();
            var spatial = new List<SpatialClauseDto>();
            var navigate = new List<NavigateClauseDto>();
            OrderByDto orderBy = null;
            var skip = 0;
            var take = DefaultTake;
            var revision = new RevisionDto("head", 0, null);

            // FROM is required.
            if (IsKeyword("FROM"))
            {
                ParseFrom(out archetype, out polymorphic);
            }
            else if (Peek().Kind != TokenKind.Eof)
            {
                Error(Peek(), "Query must start with FROM <archetype>.");
                RecoverToStage();
            }

            while (Peek().Kind != TokenKind.Eof)
            {
                var stageStart = _pos;

                if (IsKeyword("WITH"))
                {
                    Consume();
                    ParseComponentList(with);
                }
                else if (IsKeyword("WITHOUT"))
                {
                    Consume();
                    ParseComponentList(without);
                }
                else if (IsKeyword("EXCLUDE"))
                {
                    Consume();
                    ParseComponentList(exclude);
                }
                else if (IsKeyword("ENABLED"))
                {
                    Consume();
                    ParseComponentList(enabled);
                }
                else if (IsKeyword("DISABLED"))
                {
                    Consume();
                    ParseComponentList(disabled);
                }
                else if (IsKeyword("WHERE"))
                {
                    Consume();
                    var predicate = ParsePredicateChain();
                    where = where == null ? predicate : new PredicateNodeDto("and", [where, predicate], null, null, null, null);
                }
                else if (IsKeyword("SELECT"))
                {
                    // SELECT is a component-list stage (Phase 1: component-level projection). It reuses the same
                    // comma-separated dotted-identifier list as WITH/WITHOUT — each entry is a component typeName whose
                    // fields become result columns. Field-level selection is deferred (see query-console.md §13).
                    Consume();
                    ParseComponentList(select);
                }
                else if (IsKeyword("SPATIAL"))
                {
                    Consume();
                    var clause = ParseSpatial();
                    if (clause != null)
                    {
                        spatial.Add(clause);
                    }
                }
                else if (IsKeyword("NAVIGATE"))
                {
                    Consume();
                    var clause = ParseNavigate();
                    if (clause != null)
                    {
                        navigate.Add(clause);
                    }
                }
                else if (IsKeyword("ORDER"))
                {
                    Consume();
                    orderBy = ParseOrderBy();
                }
                else if (IsKeyword("SKIP"))
                {
                    Consume();
                    skip = ParseIntegerClause("SKIP");
                }
                else if (IsKeyword("TAKE"))
                {
                    Consume();
                    take = ParseIntegerClause("TAKE");
                }
                else if (IsKeyword("AT"))
                {
                    Consume();
                    revision = ParseAtRevision();
                }
                else
                {
                    Error(Peek(), $"Unexpected token '{Peek().Text}'. Expected one of: WITH, WITHOUT, EXCLUDE, ENABLED, DISABLED, WHERE, SPATIAL, NAVIGATE, ORDER BY, SKIP, TAKE, AT.");
                    RecoverToStage();
                }

                // Defensive: if the stage parser made no progress, advance one token so we can't loop forever on a
                // pathological input.
                if (_pos == stageStart)
                {
                    _pos++;
                }
            }

            return new QuerySpecDto(
                archetype,
                polymorphic,
                with.ToArray(),
                without.ToArray(),
                exclude.ToArray(),
                enabled.ToArray(),
                disabled.ToArray(),
                where,
                select.ToArray(),
                spatial.ToArray(),
                navigate.ToArray(),
                orderBy,
                skip,
                take,
                revision);
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
        // Stages
        // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════

        private void ParseFrom(out string archetype, out bool polymorphic)
        {
            Consume();                              // FROM
            archetype = string.Empty;
            polymorphic = true;

            // Three accepted forms for the archetype reference:
            //   FROM MyArch         — CLR class name (Type.Name / Type.FullName)
            //   FROM 2001           — bare numeric ArchetypeId
            //   FROM #2001          — explicit '#'-prefixed ArchetypeId (matches the Workbench schema-browser display)
            // The first form is stored as-is; the latter two are canonicalised to "#<id>" so the compiler has a
            // single deterministic shape to dispatch on (numeric vs name resolution).
            var t = Peek();
            if (t.Kind == TokenKind.Identifier)
            {
                archetype = Consume().Text;
            }
            else if (t.Kind == TokenKind.IntLiteral)
            {
                Consume();
                archetype = "#" + t.Text;
            }
            else if (t.Kind == TokenKind.Hash)
            {
                Consume();                          // '#'
                if (Peek().Kind != TokenKind.IntLiteral)
                {
                    Error(Peek(), "'#' must be followed by an archetype id (e.g. '#2001').");
                    RecoverToStage();
                    return;
                }
                var id = Consume();
                archetype = "#" + id.Text;
            }
            else
            {
                Error(t, "FROM requires an archetype identifier (e.g. 'MyArch') or id (e.g. '#2001' / '2001').");
                RecoverToStage();
                return;
            }

            if (TryConsumeKeyword("POLYMORPHIC"))
            {
                polymorphic = true;
            }
            else if (TryConsumeKeyword("EXACT"))
            {
                polymorphic = false;
            }
        }

        private void ParseComponentList(List<string> sink)
        {
            // Each stage takes one or more comma-separated component identifiers. Identifiers may be dotted
            // (e.g. "Typhon.Workbench.Fixture.CompA") — components in the engine are identified by their
            // [Component("...")] attribute string, which is frequently namespaced. The parser greedily reads
            // identifier(.identifier)* segments per component to match what the schema endpoint exposes.
            while (true)
            {
                var name = ParseDottedIdentifier();
                if (name == null)
                {
                    RecoverToStage();
                    return;
                }
                sink.Add(name);
                if (Peek().Kind != TokenKind.Comma)
                {
                    return;
                }
                Consume();                          // ,
            }
        }

        /// <summary>
        /// Read identifier (.identifier)* and return the joined dotted name. Used wherever a component name
        /// appears in the grammar — accepts both short ("CompA") and qualified ("Typhon.Foo.CompA") forms.
        /// Returns null and emits a diagnostic when no identifier is found at the cursor.
        /// </summary>
        private string ParseDottedIdentifier()
        {
            if (Peek().Kind != TokenKind.Identifier)
            {
                Error(Peek(), "Expected identifier.");
                return null;
            }
            var sb = new StringBuilder();
            sb.Append(Consume().Text);
            while (Peek().Kind == TokenKind.Dot && Peek(1).Kind == TokenKind.Identifier)
            {
                Consume();                          // '.'
                sb.Append('.');
                sb.Append(Consume().Text);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Read identifier (.identifier)* and split off the last segment as the "field"; everything before
        /// joined by '.' is the component name. Used by WHERE predicates and ORDER BY, both of which always
        /// have a Component.Field shape — the last dot is the field boundary. Returns null on a missing
        /// component or missing field segment.
        /// </summary>
        private (string Component, string Field) ParseDottedComponentField(string contextLabel)
        {
            if (Peek().Kind != TokenKind.Identifier)
            {
                Error(Peek(), $"{contextLabel} requires <component>.<field>.");
                return (null, null);
            }
            var segments = new List<string> { Consume().Text };
            while (Peek().Kind == TokenKind.Dot && Peek(1).Kind == TokenKind.Identifier)
            {
                Consume();                          // '.'
                segments.Add(Consume().Text);
            }
            if (segments.Count < 2)
            {
                // Only one segment + no trailing dot — the field is missing.
                Error(Peek(), $"Expected '.' after component name in {contextLabel}.");
                return (null, null);
            }
            var field = segments[^1];
            var component = string.Join('.', segments.GetRange(0, segments.Count - 1));
            return (component, field);
        }

        private PredicateNodeDto ParsePredicateChain()
        {
            // predicate (('AND' | 'OR') predicate)*
            var left = ParsePrimaryPredicate();
            while (left != null && (IsKeyword("AND") || IsKeyword("OR")))
            {
                var op = Consume().Text;
                var right = ParsePrimaryPredicate();
                if (right == null)
                {
                    return left;
                }
                var kind = op == "AND" ? "and" : "or";
                left = new PredicateNodeDto(kind, [left, right], null, null, null, null);
            }
            return left;
        }

        private PredicateNodeDto ParsePrimaryPredicate()
        {
            if (Peek().Kind == TokenKind.LParen)
            {
                Consume();
                var inner = ParsePredicateChain();
                if (Peek().Kind == TokenKind.RParen)
                {
                    Consume();
                }
                else
                {
                    Error(Peek(), "Expected ')'.");
                }
                return inner;
            }

            // Cmp: <dotted-component>.<field> op value — component name may be dotted (e.g. "A.B.C") to match
            // the engine's registered name; the last dot-segment is always the field.
            if (Peek().Kind != TokenKind.Identifier)
            {
                Error(Peek(), "Expected predicate (component.field op value) or '('.");
                return null;
            }
            var (component, field) = ParseDottedComponentField("predicate");
            if (component == null)
            {
                return null;
            }
            var opText = ParseComparisonOp();
            if (opText == null)
            {
                return null;
            }
            var value = ParseValue();
            return new PredicateNodeDto("cmp", null, component, field, opText, value);
        }

        private string ParseComparisonOp()
        {
            return Peek().Kind switch
            {
                TokenKind.OpEq => Take("=="),
                TokenKind.OpNeq => Take("!="),
                TokenKind.OpGt => Take(">"),
                TokenKind.OpLt => Take("<"),
                TokenKind.OpGte => Take(">="),
                TokenKind.OpLte => Take("<="),
                _ => OpError(),
            };

            string Take(string op)
            {
                _pos++;
                return op;
            }
            string OpError()
            {
                Error(Peek(), $"Expected comparison operator (==, !=, >, <, >=, <=). Got '{Peek().Text}'.");
                return null;
            }
        }

        private object ParseValue()
        {
            // Optional leading '-' for negative numerics. We tokenize '-' as TokenKind.Minus and consume it here so the
            // next token is the bare numeric literal (the lexer never folds the sign into the literal — that would
            // break a future infix '-').
            var negate = false;
            if (Peek().Kind == TokenKind.Minus)
            {
                Consume();
                negate = true;
            }

            var t = Peek();
            switch (t.Kind)
            {
                case TokenKind.IntLiteral:
                    Consume();
                    var iv = (long)t.Value;
                    return negate ? -iv : iv;
                case TokenKind.FloatLiteral:
                    Consume();
                    var dv = (double)t.Value;
                    return negate ? -dv : dv;
                case TokenKind.StringLiteral:
                    Consume();
                    return t.Value;
                case TokenKind.BoolLiteral:
                    Consume();
                    return t.Value;
                case TokenKind.Identifier:
                    // Identifier-as-value: closure-captured const / enum member name. Compiler resolves against the
                    // target field type at compile time.
                    Consume();
                    return t.Text;
                default:
                    Error(t, $"Expected literal or identifier. Got '{t.Text}'.");
                    return null;
            }
        }

        private SpatialClauseDto ParseSpatial()
        {
            if (Peek().Kind != TokenKind.Identifier)
            {
                Error(Peek(), "SPATIAL requires a component identifier.");
                RecoverToStage();
                return null;
            }
            // Component names are frequently namespaced (e.g. "Typhon.Workbench.Fixture.PlayerPosition"); read the
            // full dotted identifier like every other component-ident stage (WITH / WHERE / ORDER BY) per the §5.1
            // grammar (`spatial-stage ::= 'SPATIAL' component-ident ...`). A bare Consume() stopped at the first dot.
            var component = ParseDottedIdentifier();

            if (TryConsumeKeyword("NEARBY"))
            {
                var p = ParsePoint();
                if (p == null) return null;
                if (!TryConsumeKeyword("RADIUS"))
                {
                    Error(Peek(), "SPATIAL NEARBY requires RADIUS <number>.");
                    return null;
                }
                var r = ParseNumber();
                return new SpatialClauseDto(component, "nearby", [p[0], p[1], p[2], r]);
            }
            if (TryConsumeKeyword("AABB"))
            {
                var p1 = ParsePoint();
                if (p1 == null) return null;
                if (Peek().Kind != TokenKind.Comma)
                {
                    Error(Peek(), "SPATIAL AABB requires ',' between min and max points.");
                    return null;
                }
                Consume();
                var p2 = ParsePoint();
                if (p2 == null) return null;
                return new SpatialClauseDto(component, "aabb", [p1[0], p1[1], p1[2], p2[0], p2[1], p2[2]]);
            }
            if (TryConsumeKeyword("RAY"))
            {
                var origin = ParsePoint();
                if (origin == null) return null;
                if (Peek().Kind != TokenKind.Comma)
                {
                    Error(Peek(), "SPATIAL RAY requires ',' between origin and direction.");
                    return null;
                }
                Consume();
                var dir = ParsePoint();
                if (dir == null) return null;
                if (Peek().Kind != TokenKind.Comma)
                {
                    Error(Peek(), "SPATIAL RAY requires ',' before max distance.");
                    return null;
                }
                Consume();
                var maxDist = ParseNumber();
                return new SpatialClauseDto(component, "ray", [origin[0], origin[1], origin[2], dir[0], dir[1], dir[2], maxDist]);
            }

            Error(Peek(), "Expected NEARBY, AABB, or RAY after SPATIAL <component>.");
            return null;
        }

        private double[] ParsePoint()
        {
            // <x>, <y>, <z>  — three comma-separated numerics.
            var x = ParseNumber();
            if (Peek().Kind != TokenKind.Comma) { Error(Peek(), "Expected ',' between point components."); return null; }
            Consume();
            var y = ParseNumber();
            if (Peek().Kind != TokenKind.Comma) { Error(Peek(), "Expected ',' between point components."); return null; }
            Consume();
            var z = ParseNumber();
            return [x, y, z];
        }

        private double ParseNumber()
        {
            var negate = false;
            if (Peek().Kind == TokenKind.Minus)
            {
                Consume();
                negate = true;
            }
            var t = Peek();
            if (t.Kind == TokenKind.IntLiteral)
            {
                Consume();
                var v = (double)(long)t.Value;
                return negate ? -v : v;
            }
            if (t.Kind == TokenKind.FloatLiteral)
            {
                Consume();
                var v = (double)t.Value;
                return negate ? -v : v;
            }
            Error(t, $"Expected number. Got '{t.Text}'.");
            return 0d;
        }

        private NavigateClauseDto ParseNavigate()
        {
            if (Peek().Kind != TokenKind.Identifier)
            {
                Error(Peek(), "NAVIGATE requires a FK field identifier.");
                return null;
            }
            var field = Consume().Text;
            if (Peek().Kind != TokenKind.Arrow)
            {
                Error(Peek(), "NAVIGATE requires '->' after the FK field.");
                return null;
            }
            Consume();
            if (Peek().Kind != TokenKind.Identifier)
            {
                Error(Peek(), "NAVIGATE requires a target component identifier after '->'.");
                return null;
            }
            var target = Consume().Text;

            PredicateNodeDto where = null;
            if (TryConsumeKeyword("WHERE"))
            {
                where = ParsePredicateChain();
            }
            return new NavigateClauseDto(field, target, where);
        }

        private OrderByDto ParseOrderBy()
        {
            // ORDER already consumed; BY is required.
            if (!TryConsumeKeyword("BY"))
            {
                Error(Peek(), "Expected BY after ORDER.");
                return null;
            }
            var (component, field) = ParseDottedComponentField("ORDER BY");
            if (component == null)
            {
                return null;
            }
            var descending = false;
            if (TryConsumeKeyword("DESC"))
            {
                descending = true;
            }
            else
            {
                TryConsumeKeyword("ASC");           // ASC is the default — accepting it is just being explicit
            }
            return new OrderByDto(component, field, descending);
        }

        private int ParseIntegerClause(string clauseName)
        {
            if (Peek().Kind != TokenKind.IntLiteral)
            {
                Error(Peek(), $"{clauseName} requires an integer.");
                return 0;
            }
            var t = Consume();
            return (int)Math.Clamp((long)t.Value, 0, int.MaxValue);
        }

        private RevisionDto ParseAtRevision()
        {
            if (TryConsumeKeyword("REVISION"))
            {
                if (Peek().Kind == TokenKind.IntLiteral)
                {
                    var t = Consume();
                    return new RevisionDto("revision", (long)t.Value, null);
                }
                if (TryConsumeKeyword("HEAD"))
                {
                    return new RevisionDto("head", 0, null);
                }
                Error(Peek(), "AT REVISION requires an integer or HEAD.");
                return new RevisionDto("head", 0, null);
            }
            if (TryConsumeKeyword("TICK"))
            {
                if (Peek().Kind != TokenKind.IntLiteral)
                {
                    Error(Peek(), "AT TICK requires an integer.");
                    return new RevisionDto("head", 0, null);
                }
                var t = Consume();
                return new RevisionDto("tick", (long)t.Value, null);
            }
            if (TryConsumeKeyword("TIME"))
            {
                if (Peek().Kind != TokenKind.StringLiteral)
                {
                    Error(Peek(), "AT TIME requires an ISO-8601 timestamp string.");
                    return new RevisionDto("head", 0, null);
                }
                var t = Consume();
                return new RevisionDto("time", 0, (string)t.Value);
            }
            if (TryConsumeKeyword("HEAD"))
            {
                return new RevisionDto("head", 0, null);
            }
            Error(Peek(), "AT requires REVISION, TICK, TIME, or HEAD.");
            return new RevisionDto("head", 0, null);
        }
    }
}
