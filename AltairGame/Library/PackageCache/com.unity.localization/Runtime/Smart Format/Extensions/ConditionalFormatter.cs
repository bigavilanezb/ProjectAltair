using System;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine.Localization.SmartFormat.Core.Extensions;
using UnityEngine.Localization.SmartFormat.Core.Parsing;
using UnityEngine.Localization.SmartFormat.Net.Utilities;

namespace UnityEngine.Localization.SmartFormat.Extensions
{
    /// <summary>
    /// Provides conditional formatting on any placeholder that contains a "|" (pipe character) after the ":".
    /// For example: <c>{0:cond:>10?Greater Than 10|=10?Equals to 10|Less than 10}</c>
    /// </summary>
    [Serializable]
    public class ConditionalFormatter : FormatterBase, IFormatterLiteralExtractor
    {
        /// <summary>
        /// Creates a new instance of the formatter.
        /// </summary>
        public ConditionalFormatter()
        {
            Names = DefaultNames;
        }

        private static readonly Regex _complexConditionPattern
            = new Regex(@"^  (?:   ([&/]?)   ([<>=!]=?)   ([0-9.-]+)   )+   \?",
            //   Description:      and/or    comparator     value
            RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled);

        /// <inheritdoc/>
        public override string[] DefaultNames => new[] {"conditional", "cond", ""};

        /// <inheritdoc/>
        public override bool TryEvaluateFormat(IFormattingInfo formattingInfo)
        {
            var format = formattingInfo.Format;
            var current = formattingInfo.CurrentValue;

            if (format == null) return false;
            // Ignore a leading ":", which is used to bypass the PluralLocalizationExtension
            if (format.baseString[format.startIndex] == ':') format = format.Substring(1);

            // See if the format string contains un-nested "|":
            var parameters = format.Split('|');
            if (parameters.Count == 1) return false; // There are no parameters found.

            // See if the value is a number:

            var currentIsConvertible = current is IConvertible &&
                !(current is DateTime) && !(current is string) && !(current is bool); // We have special handling for these types

            var currentNumber = currentIsConvertible ? Convert.ToDecimal(current) : 0;

            int paramIndex; // Determines which parameter to use for output

            // First, we'll see if we are using "complex conditions":
            if (currentIsConvertible)
            {
                paramIndex = -1;
                while (true)
                {
                    paramIndex++;
                    if (paramIndex == parameters.Count) return true;

                    if (!TryEvaluateCondition(parameters[paramIndex], currentNumber, out var conditionWasTrue,
                        out var outputItem))
                    {
                        // This parameter doesn't have a
                        // complex condition (making it a "else" condition)

                        // Only do "complex conditions" if the first item IS a "complex condition".
                        if (paramIndex == 0) break;
                        // Otherwise, output the "else" section:
                        conditionWasTrue = true;
                    }

                    // If the conditional statement was true, then we can break.
                    if (conditionWasTrue)
                    {
                        formattingInfo.Write(outputItem, current);
                        return true;
                    }
                }

                // We don't have any "complex conditions",
                // so let's do the normal conditional formatting:
            }

            var paramCount = parameters.Count;

            // Determine the Current item's Type:
            if (currentIsConvertible)
            {
                if (currentNumber < 0)
                    paramIndex = paramCount - 1;
                else
                    paramIndex = Math.Min((int)Math.Floor(currentNumber), paramCount - 1);
            }
            else
                switch (current)
                {
                    case bool boolArg:
                        // Bool: True|False
                        paramIndex = boolArg ? 0 : 1;
                        break;
                    // Date: Past|Present|Future   or   Past/Present|Future
                    case DateTime dateTimeArg when paramCount == 3 && dateTimeArg.ToUniversalTime().Date == SystemTime.Now().ToUniversalTime().Date:
                        paramIndex = 1;
                        break;
                    case DateTime dateTimeArg when dateTimeArg.ToUniversalTime() <= SystemTime.Now().ToUniversalTime():
                        paramIndex = 0;
                        break;
                    case DateTime dateTimeArg:
                        paramIndex = paramCount - 1;
                        break;
                    // Date: Past|Present|Future   or   Past/Present|Future
                    case DateTimeOffset dateTimeOffsetArg when paramCount == 3 && dateTimeOffsetArg.UtcDateTime.Date == SystemTime.OffsetNow().UtcDateTime.Date:
                        paramIndex = 1;
                        break;
                    case DateTimeOffset dateTimeOffsetArg when dateTimeOffsetArg.UtcDateTime <= SystemTime.OffsetNow().UtcDateTime:
                        paramIndex = 0;
                        break;
                    case DateTimeOffset dateTimeOffsetArg:
                        paramIndex = paramCount - 1;
                        break;
                    // TimeSpan: Negative|Zero|Positive  or  Negative/Zero|Positive
                    case TimeSpan timeSpanArg when paramCount == 3 && timeSpanArg == TimeSpan.Zero:
                        paramIndex = 1;
                        break;
                    case TimeSpan timeSpanArg when timeSpanArg.CompareTo(TimeSpan.Zero) <= 0:
                        paramIndex = 0;
                        break;
                    case TimeSpan timeSpanArg:
                        paramIndex = paramCount - 1;
                        break;
                    case string stringArg:
                        // String: Value|NullOrEmpty
                        paramIndex = !string.IsNullOrEmpty(stringArg) ? 0 : 1;
                        break;
                    default:
                    {
                        // Object: Something|Nothing
                        var arg = current;
                        paramIndex = arg != null ? 0 : 1;
                        break;
                    }
                }

            // Now, output the selected parameter:
            var selectedParameter = parameters[paramIndex];

            // Output the selectedParameter:
            formattingInfo.Write(selectedParameter, current);
            return true;
        }

        /// <summary>
        /// Evaluates a conditional format.
        /// Each condition must start with a comparor: "&gt;/&gt;=", "&lt;/&lt;=", "=", "!=".
        /// Conditions must be separated by either "&amp;" (AND) or "/" (OR).
        /// The conditional statement must end with a "?".
        /// Examples:
        /// &gt;=21&amp;&lt;30&amp;!=25/=40?
        /// </summary>
        private static bool TryEvaluateCondition(Format parameter, decimal value, out bool conditionResult,
            out Format outputItem)
        {
            conditionResult = false;
            // Let's evaluate the conditions into a boolean value:
            var m = _complexConditionPattern.Match(parameter.baseString, parameter.startIndex,
                parameter.endIndex - parameter.startIndex);
            if (!m.Success)
            {
                // Could not parse the "complex condition"
                outputItem = parameter;
                return false;
            }


            var andOrs = m.Groups[1].Captures;
            var comps = m.Groups[2].Captures;
            var values = m.Groups[3].Captures;

            for (var i = 0; i < andOrs.Count; i++)
            {
                var v = decimal.Parse(values[i].Value, CultureInfo.InvariantCulture);
                var exp = false;
                switch (comps[i].Value)
                {
                    case ">":
                        exp = value > v;
                        break;
                    case "<":
                        exp = value < v;
                        break;
                    case "=":
                    case "==":
                        exp = value == v;
                        break;
                    case "<=":
                        exp = value <= v;
                        break;
                    case ">=":
                        exp = value >= v;
                        break;
                    case "!":
                    case "!=":
                        exp = value != v;
                        break;
                }

                if (i == 0)
                    conditionResult = exp;
                else if (andOrs[i].Value == "/")
                    conditionResult = conditionResult | exp;
                else
                    conditionResult = conditionResult & exp;
            }

            // Successful
            // Output the substring that doesn't contain the "complex condition"
            var newStartIndex = m.Index + m.Length - parameter.startIndex;
            outputItem = parameter.Substring(newStartIndex);
            return true;
        }

        /// <inheritdoc/>
        public void WriteAllLiterals(IFormattingInfo formattingInfo)
        {
            var format = formattingInfo.Format;

            if (format == null)
                return;
            // Ignore a leading ":", which is used to bypass the PluralLocalizationExtension
            if (format.baseString[format.startIndex] == ':')
                format = format.Substring(1);

            // See if the format string contains un-nested "|":
            var parameters = format.Split('|');
            if (parameters.Count == 1)
                return; // There are no parameters found.

            for (int i = 0; i < parameters.Count; ++i)
            {
                var parameter = parameters[i];

                // Let's evaluate the conditions into a boolean value:
                var m = _complexConditionPattern.Match(parameter.baseString, parameter.startIndex,
                    parameter.endIndex - parameter.startIndex);
                if (m.Success)
                {
                    var newStartIndex = m.Index + m.Length - parameter.startIndex;
                    parameter = parameter.Substring(newStartIndex);
                }

                formattingInfo.Write(parameter, null);
            }
        }
    }
}
