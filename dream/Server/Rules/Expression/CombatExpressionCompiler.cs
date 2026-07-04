using System.Globalization;

namespace Dream.Server.Rules.Expression;

internal sealed class CombatExpressionCompiler
{
    private readonly IReadOnlyList<CombatExpressionToken> _tokens;
    private readonly HashSet<string> _allowedIdentifiers;
    private int _index;

    private CombatExpressionCompiler(IReadOnlyList<CombatExpressionToken> tokens, IEnumerable<string> allowedIdentifiers)
    {
        _tokens = tokens;
        _allowedIdentifiers = new HashSet<string>(allowedIdentifiers, StringComparer.OrdinalIgnoreCase);
    }

    public static Func<IReadOnlyDictionary<string, double>, double> Compile(
        string expression,
        IEnumerable<string> allowedIdentifiers)
    {
        CombatExpressionCompiler compiler = new(
            CombatExpressionTokenizer.Tokenize(expression),
            allowedIdentifiers);
        Node root = compiler.ParseExpression();
        compiler.Expect(CombatExpressionTokenKind.End);
        return variables =>
        {
            double value = root.Evaluate(variables);
            if (!double.IsFinite(value))
            {
                throw new InvalidOperationException($"Formula '{expression}' produced a non-finite result.");
            }

            return value;
        };
    }

    private Node ParseExpression()
    {
        Node node = ParseTerm();
        while (Current.Kind is CombatExpressionTokenKind.Plus or CombatExpressionTokenKind.Minus)
        {
            CombatExpressionTokenKind operation = Current.Kind;
            Advance();
            Node right = ParseTerm();
            node = new BinaryNode(node, right, operation);
        }

        return node;
    }

    private Node ParseTerm()
    {
        Node node = ParseFactor();
        while (Current.Kind is CombatExpressionTokenKind.Star or CombatExpressionTokenKind.Slash)
        {
            CombatExpressionTokenKind operation = Current.Kind;
            Advance();
            Node right = ParseFactor();
            node = new BinaryNode(node, right, operation);
        }

        return node;
    }

    private Node ParseFactor()
    {
        if (Current.Kind == CombatExpressionTokenKind.Minus)
        {
            Advance();
            return new UnaryMinusNode(ParseFactor());
        }

        if (Current.Kind == CombatExpressionTokenKind.Number)
        {
            double value = double.Parse(Current.Text, CultureInfo.InvariantCulture);
            Advance();
            return new NumberNode(value);
        }

        if (Current.Kind == CombatExpressionTokenKind.Identifier)
        {
            string identifier = Current.Text;
            if (!_allowedIdentifiers.Contains(identifier))
            {
                throw new InvalidOperationException($"Unknown combat formula variable '{identifier}'.");
            }

            Advance();
            return new IdentifierNode(identifier);
        }

        if (Current.Kind == CombatExpressionTokenKind.LeftParen)
        {
            Advance();
            Node node = ParseExpression();
            Expect(CombatExpressionTokenKind.RightParen);
            return node;
        }

        throw new InvalidOperationException($"Unexpected token '{Current.Text}' in combat formula expression.");
    }

    private CombatExpressionToken Current => _tokens[_index];

    private void Advance()
    {
        if (_index < _tokens.Count - 1)
        {
            _index++;
        }
    }

    private void Expect(CombatExpressionTokenKind expectedKind)
    {
        if (Current.Kind != expectedKind)
        {
            throw new InvalidOperationException($"Expected token '{expectedKind}' but found '{Current.Text}'.");
        }

        Advance();
    }

    private abstract record Node
    {
        public abstract double Evaluate(IReadOnlyDictionary<string, double> variables);
    }

    private sealed record NumberNode(double Value) : Node
    {
        public override double Evaluate(IReadOnlyDictionary<string, double> variables)
        {
            _ = variables;
            return Value;
        }
    }

    private sealed record IdentifierNode(string Name) : Node
    {
        public override double Evaluate(IReadOnlyDictionary<string, double> variables)
        {
            return variables.TryGetValue(Name, out double value)
                ? value
                : throw new InvalidOperationException($"Missing combat formula variable '{Name}'.");
        }
    }

    private sealed record UnaryMinusNode(Node Operand) : Node
    {
        public override double Evaluate(IReadOnlyDictionary<string, double> variables)
        {
            return -Operand.Evaluate(variables);
        }
    }

    private sealed record BinaryNode(Node Left, Node Right, CombatExpressionTokenKind Operation) : Node
    {
        public override double Evaluate(IReadOnlyDictionary<string, double> variables)
        {
            double leftValue = Left.Evaluate(variables);
            double rightValue = Right.Evaluate(variables);
            return Operation switch
            {
                CombatExpressionTokenKind.Plus => leftValue + rightValue,
                CombatExpressionTokenKind.Minus => leftValue - rightValue,
                CombatExpressionTokenKind.Star => leftValue * rightValue,
                CombatExpressionTokenKind.Slash => rightValue == 0d
                    ? throw new InvalidOperationException("Combat formula division by zero.")
                    : leftValue / rightValue,
                _ => throw new InvalidOperationException($"Unsupported combat expression operation '{Operation}'.")
            };
        }
    }
}
