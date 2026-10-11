// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Setting a dependency property from the panel.
//
// Reachable by editing an ATTRIBUTE on a node in the Elements panel, which is
// the editing path that works without pretending to be a stylesheet: the Styles
// pane's editor wants source ranges in a document it can rewrite, and there is
// no such document behind a dependency property. Attributes are the node's local
// values, which is exactly the set worth changing by hand.
//
// Values arrive as the strings a XAML author would have written ("Red",
// "10,20,30,40", "Collapsed"), so conversion is the property type's own
// TypeConverter -- the same one the XAML parser would have used.
//

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Windows;

namespace Microsoft.Wpf.DevTools
{
    internal static class PropertyWriter
    {
        /// <summary>
        /// Find a settable dependency property by the name the panel shows, which is
        /// either bare ("Width") or owner-qualified ("Grid.Row") for an attached one.
        /// </summary>
        internal static bool TryFind(DependencyObject node, string displayName, out DependencyProperty? property)
        {
            property = null;
            if (string.IsNullOrEmpty(displayName))
                return false;

            string owner = string.Empty;
            string name = displayName;

            int dot = displayName.LastIndexOf('.');
            if (dot > 0)
            {
                owner = displayName.Substring(0, dot);
                name = displayName.Substring(dot + 1);
            }

            foreach (DependencyProperty candidate in PropertyReader.CandidatesFor(node))
            {
                if (!string.Equals(candidate.Name, name, StringComparison.Ordinal))
                    continue;

                if (owner.Length > 0 && !string.Equals(candidate.OwnerType?.Name, owner, StringComparison.Ordinal))
                    continue;

                property = candidate;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Set it, converting from text. Returns false with a reason rather than
        /// throwing: a bad value typed into the panel is an ordinary event, and the
        /// frontend wants an error message, not a dead session.
        /// </summary>
        [UnconditionalSuppressMessage("Trimming", "IL2026",
            Justification = "Diagnostics-only. TypeDescriptor.GetConverter may be trimmed away on the " +
                            "AOT heads, in which case conversion fails and the edit is reported as " +
                            "unsupported -- which is the correct answer there.")]
        internal static bool TrySet(DependencyObject node, DependencyProperty property, string? text, out string error)
        {
            error = string.Empty;

            if (property.ReadOnly)
            {
                error = $"{property.Name} is read-only.";
                return false;
            }

            object? value;
            try
            {
                if (text == null)
                {
                    value = null;
                }
                else if (property.PropertyType == typeof(string))
                {
                    value = text;
                }
                else
                {
                    TypeConverter converter = TypeDescriptor.GetConverter(property.PropertyType);
                    if (!converter.CanConvertFrom(typeof(string)))
                    {
                        error = $"no conversion from text to {property.PropertyType.Name}.";
                        return false;
                    }

                    value = converter.ConvertFromString(null, CultureInfo.InvariantCulture, text);
                }
            }
            catch (Exception ex)
            {
                error = $"'{text}' is not a valid {property.PropertyType.Name}: {ex.Message}";
                return false;
            }

            try
            {
                node.SetValue(property, value);
                return true;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        /// <summary>Put a property back to whatever it would have been without a local value.</summary>
        internal static bool TryClear(DependencyObject node, DependencyProperty property, out string error)
        {
            error = string.Empty;

            if (property.ReadOnly)
            {
                error = $"{property.Name} is read-only.";
                return false;
            }

            try
            {
                node.ClearValue(property);
                return true;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Split an edited declaration block ("Width: 120; Background: Red;") into
        /// name/value pairs. Tolerant of a missing trailing semicolon and of the
        /// panel's habit of sending a comment-only block when a property is disabled.
        /// </summary>
        internal static List<KeyValuePair<string, string>> ParseDeclarations(string text)
        {
            var result = new List<KeyValuePair<string, string>>();

            foreach (string part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string declaration = part.Trim();
                if (declaration.Length == 0 || declaration.StartsWith("/*", StringComparison.Ordinal))
                    continue;

                int colon = declaration.IndexOf(':');
                if (colon <= 0)
                    continue;

                string name = declaration.Substring(0, colon).Trim();
                string value = declaration.Substring(colon + 1).Trim();

                // Values are annotated with the modifiers that applied when they were
                // read; an unedited round trip must not try to set that back.
                int comment = value.IndexOf("/*", StringComparison.Ordinal);
                if (comment >= 0)
                    value = value.Substring(0, comment).Trim();

                if (name.Length > 0)
                    result.Add(new KeyValuePair<string, string>(name, value));
            }

            return result;
        }
    }
}
