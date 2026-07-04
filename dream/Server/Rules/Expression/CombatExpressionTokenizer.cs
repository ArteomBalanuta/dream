using System.Globalization;
using System.Text;

namespace Dream.Server.Rules.Expression;

internal static class CombatExpressionTokenizer
{
    public static IReadOnlyList<CombatExpressionToken> Tokenize(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);

        List<CombatExpressionToken> tokens = [];
        int index = 0;
        while (index < expression.Length)
        {
            char current = expression[index];
            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (char.IsDigit(current) || current == '.')
            {
                int start = index;
                bool seenDot = current == '.';
                index++;
                while (index < expression.Length)
                {
                    char next = expression[index];
                    if (char.IsDigit(next))
                    {
                        index++;
                        continue;
                    }

                    if (next == '.')
                    {
                        if (seenDot)
                        {
                            throw new InvalidOperationException($"Invalid numeric literal in '{expression}'.");
                        }

                        seenDot = true;
                        index++;
                        continue;
                    }

                    break;
                }

                string numberText = expression[start..index];
                if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    throw new InvalidOperationException($"Invalid numeric literal '{numberText}' in '{expression}'.");
                }

                tokens.Add(new CombatExpressionToken(CombatExpressionTokenKind.Number, numberText));
                continue;
            }

            if (char.IsLetter(current) || current == '_')
            {
                StringBuilder builder = new();
                builder.Append(current);
                index++;
                while (index < expression.Length && (char.IsLetterOrDigit(expression[index]) || expression[index] == '_'))
                {
                    builder.Append(expression[index]);
                    index++;
                }

                tokens.Add(new CombatExpressionToken(CombatExpressionTokenKind.Identifier, builder.ToString()));
                continue;
            }

            tokens.Add(current switch
            {
                '+' => new CombatExpressionToken(CombatExpressionTokenKind.Plus, "+"),
                '-' => new CombatExpressionToken(CombatExpressionTokenKind.Minus, "-"),
                '*' => new CombatExpressionToken(CombatExpressionTokenKind.Star, "*"),
                '/' => new CombatExpressionToken(CombatExpressionTokenKind.Slash, "/"),
                '(' => new CombatExpressionToken(CombatExpressionTokenKind.LeftParen, "("),
                ')' => new CombatExpressionToken(CombatExpressionTokenKind.RightParen, ")"),
                _ => throw new InvalidOperationException($"Unsupported token '{current}' in '{expression}'.")
            });
            index++;
        }

        tokens.Add(new CombatExpressionToken(CombatExpressionTokenKind.End, string.Empty));
        return tokens;
    }
}
