namespace Dream.Simulation.Rules.Expression;

internal enum CombatExpressionTokenKind
{
    Number,
    Identifier,
    Plus,
    Minus,
    Star,
    Slash,
    LeftParen,
    RightParen,
    End
}

internal readonly record struct CombatExpressionToken(
    CombatExpressionTokenKind Kind,
    string Text);
