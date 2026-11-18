namespace PocketCalculator.Render.Css;

/// <summary>
/// Type-checks a CSS math function (<c>calc()</c>, <c>min()</c>, <c>max()</c>,
/// <c>clamp()</c>) as a <c>&lt;length&gt;</c>, without evaluating it.
/// </summary>
/// <remarks>
/// <see cref="CssLength.ResolveContextual"/> is deliberately lenient: it skips <c>min()</c>
/// arguments it cannot read and does not track types, so <c>calc(30)</c> and
/// <c>calc(2px * 3px)</c> both resolve. A property that must drop such a declaration, as
/// Chromium does, checks it here first. Percentages are not accepted.
/// </remarks>
internal static class CssMathTypes
{
    private enum Kind : byte
    {
        Invalid,
        Number,
        Length,
    }

    /// <summary>Whether <paramref name="value"/> is one math function of type length.</summary>
    internal static bool IsLengthFunction(string value)
    {
        if (!CssLength.CssMathNestingIsSafe(value))
        {
            return false;
        }

        int position = 0;
        SkipWhitespace(value, ref position);
        if (position >= value.Length || !IsIdentStart(value[position]))
        {
            return false;
        }

        Kind kind = Function(value, ref position);
        SkipWhitespace(value, ref position);
        return kind == Kind.Length && position == value.Length;
    }

    private static Kind Function(string value, ref int position)
    {
        int start = position;
        while (position < value.Length && (IsIdentStart(value[position]) || value[position] == '-'))
        {
            position++;
        }

        if (position >= value.Length || value[position] != '(')
        {
            return Kind.Invalid;
        }

        string name = CssText.AsciiLower(value[start..position]);
        position++;
        int expected = name switch
        {
            "calc" => 1,
            "clamp" => 3,
            "min" or "max" => -1,
            _ => 0,
        };
        if (expected == 0)
        {
            return Kind.Invalid;
        }

        Kind kind = Kind.Invalid;
        int count = 0;
        while (true)
        {
            Kind argument = Sum(value, ref position);
            if (argument == Kind.Invalid || (count > 0 && argument != kind))
            {
                return Kind.Invalid;
            }

            kind = argument;
            count++;
            SkipWhitespace(value, ref position);
            if (position < value.Length && value[position] == ',' && expected != 1)
            {
                position++;
                continue;
            }

            break;
        }

        if (position >= value.Length || value[position] != ')' || (expected > 0 && count != expected))
        {
            return Kind.Invalid;
        }

        position++;
        return kind;
    }

    private static Kind Sum(string value, ref int position)
    {
        Kind kind = Product(value, ref position);
        while (kind != Kind.Invalid)
        {
            // `+` and `-` must be surrounded by whitespace (CSS Values 4, 10.8).
            int save = position;
            int before = position;
            SkipWhitespace(value, ref position);
            if (position == before
                || position + 1 >= value.Length
                || value[position] is not ('+' or '-')
                || !CssText.IsWhitespace(value[position + 1]))
            {
                position = save;
                return kind;
            }

            position++;
            if (Product(value, ref position) != kind)
            {
                return Kind.Invalid;
            }
        }

        return kind;
    }

    private static Kind Product(string value, ref int position)
    {
        Kind kind = Value(value, ref position);
        while (kind != Kind.Invalid)
        {
            int save = position;
            SkipWhitespace(value, ref position);
            if (position >= value.Length || value[position] is not ('*' or '/'))
            {
                position = save;
                return kind;
            }

            bool divide = value[position] == '/';
            position++;
            Kind right = Value(value, ref position);
            kind = (kind, right, divide) switch
            {
                (_, Kind.Invalid, _) => Kind.Invalid,
                (_, Kind.Length, true) => Kind.Invalid,
                (Kind.Length, Kind.Length, false) => Kind.Invalid,
                (Kind.Number, Kind.Number, _) => Kind.Number,
                _ => Kind.Length,
            };
        }

        return kind;
    }

    private static Kind Value(string value, ref int position)
    {
        SkipWhitespace(value, ref position);
        if (position >= value.Length)
        {
            return Kind.Invalid;
        }

        char first = value[position];
        if (first == '(')
        {
            position++;
            Kind inner = Sum(value, ref position);
            SkipWhitespace(value, ref position);
            if (inner == Kind.Invalid || position >= value.Length || value[position] != ')')
            {
                return Kind.Invalid;
            }

            position++;
            return inner;
        }

        if (IsIdentStart(first))
        {
            return Function(value, ref position);
        }

        if (value[position] is '+' or '-')
        {
            position++;
        }

        int digits = 0;
        while (position < value.Length && char.IsAsciiDigit(value[position]))
        {
            position++;
            digits++;
        }

        if (position < value.Length && value[position] == '.')
        {
            position++;
            while (position < value.Length && char.IsAsciiDigit(value[position]))
            {
                position++;
                digits++;
            }
        }

        if (digits == 0)
        {
            return Kind.Invalid;
        }

        if (position + 1 < value.Length
            && value[position] is 'e' or 'E'
            && (char.IsAsciiDigit(value[position + 1])
                || (value[position + 1] is '+' or '-'
                    && position + 2 < value.Length
                    && char.IsAsciiDigit(value[position + 2]))))
        {
            position += 2;
            while (position < value.Length && char.IsAsciiDigit(value[position]))
            {
                position++;
            }
        }

        int unitStart = position;
        while (position < value.Length && char.IsAsciiLetter(value[position]))
        {
            position++;
        }

        if (position < value.Length && value[position] == '%')
        {
            return Kind.Invalid;
        }

        if (unitStart == position)
        {
            return Kind.Number;
        }

        return CssText.AsciiLower(value[unitStart..position]) switch
        {
            "px" or "em" or "rem" or "ex" or "ch" or "vw" or "vh" or "vmin" or "vmax"
                or "dvw" or "svw" or "lvw" or "dvh" or "svh" or "lvh" or "pt" => Kind.Length,
            _ => Kind.Invalid,
        };
    }

    private static bool IsIdentStart(char character) => char.IsAsciiLetter(character);

    private static void SkipWhitespace(string value, ref int position)
    {
        while (position < value.Length && CssText.IsWhitespace(value[position]))
        {
            position++;
        }
    }
}
