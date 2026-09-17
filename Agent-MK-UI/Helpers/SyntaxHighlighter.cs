using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Agent_MK_UI.Helpers
{
    /// <summary>
    /// Lightweight syntax highlighter for Agent-MK code blocks.
    ///
    /// This is intentionally dependency-free and designed for displaying
    /// LLM-generated source code rather than acting as a full code editor.
    /// </summary>
    public static class SyntaxHighlighter
    {
        public sealed record Token(TokenType Type, string Text);

        public enum TokenType
        {
            Plain,
            Keyword,
            Type,
            String,
            Comment,
            Number,
            Preprocessor,
            Operator,
            Attribute,
            Function,
        }

        // ------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------

        public static IReadOnlyList<Token> Tokenize(string? code, string? language)
        {
            if (string.IsNullOrEmpty(code))
                return Array.Empty<Token>();

            string lang = NormalizeLanguage(language);

            return lang switch
            {
                "cs" or "csharp" or "c#" or "razor" or "cshtml" => TokenizeCSharp(code),

                "js" or "javascript" or "jsx" => TokenizeJavaScript(code),

                "ts" or "typescript" or "tsx" => TokenizeTypeScript(code),

                "py" or "python" => TokenizePython(code),

                "json" => TokenizeJson(code),

                "html" or "htm" or "xml" => TokenizeMarkup(code),

                "css" or "scss" => TokenizeCss(code),

                "bash" or "sh" or "shell" => TokenizeShell(code),

                "sql" => TokenizeSql(code),

                _ => TokenizeGeneric(code),
            };
        }

        // ------------------------------------------------------------
        // Colors
        // ------------------------------------------------------------

        public static Brush BrushForToken(TokenType type)
        {
            // Visual Studio-inspired palette.
            //
            // These are intentionally centralized so you can later expose
            // editor themes without changing the tokenizer.

            return type switch
            {
                TokenType.Keyword => Brush("#569CD6"),

                TokenType.Type => Brush("#4EC9B0"),

                TokenType.String => Brush("#CE9178"),

                TokenType.Comment => Brush("#6A9955"),

                TokenType.Number => Brush("#B5CEA8"),

                TokenType.Preprocessor => Brush("#C586C0"),

                TokenType.Operator => Brush("#D4D4D4"),

                TokenType.Attribute => Brush("#9CDCFE"),

                TokenType.Function => Brush("#DCDCAA"),

                _ => Brush("#D4D4D4"),
            };
        }

        // ------------------------------------------------------------
        // Tokenizers
        // ------------------------------------------------------------

        private static List<Token> TokenizeCSharp(string code)
        {
            var keywords = new HashSet<string>(StringComparer.Ordinal)
            {
                "abstract",
                "as",
                "async",
                "await",
                "base",
                "break",
                "case",
                "catch",
                "checked",
                "class",
                "const",
                "continue",
                "default",
                "delegate",
                "do",
                "else",
                "enum",
                "event",
                "explicit",
                "extern",
                "false",
                "finally",
                "fixed",
                "for",
                "foreach",
                "goto",
                "if",
                "implicit",
                "in",
                "interface",
                "internal",
                "is",
                "lock",
                "namespace",
                "new",
                "null",
                "object",
                "operator",
                "out",
                "override",
                "params",
                "private",
                "protected",
                "public",
                "readonly",
                "ref",
                "return",
                "sealed",
                "sizeof",
                "stackalloc",
                "static",
                "string",
                "struct",
                "switch",
                "this",
                "throw",
                "true",
                "try",
                "typeof",
                "unchecked",
                "unsafe",
                "using",
                "virtual",
                "void",
                "volatile",
                "while",
                "var",
                "dynamic",
                "record",
                "required",
                "init",
                "get",
                "set",
                "add",
                "remove",
                "value",
                "with",
                "yield",
                "global",
            };

            var types = new HashSet<string>(StringComparer.Ordinal)
            {
                "bool",
                "byte",
                "char",
                "decimal",
                "double",
                "float",
                "int",
                "long",
                "nint",
                "nuint",
                "object",
                "sbyte",
                "short",
                "string",
                "uint",
                "ulong",
                "ushort",
                "void",
                "Task",
                "Task<T>",
                "List",
                "Dictionary",
                "HashSet",
                "IEnumerable",
                "IList",
                "String",
                "Console",
                "Exception",
                "DateTime",
                "TimeSpan",
                "Guid",
                "Action",
                "Func",
            };

            return TokenizeProgrammingLanguage(code, keywords, types, allowHashComments: false);
        }

        private static List<Token> TokenizeJavaScript(string code)
        {
            var keywords = new HashSet<string>(StringComparer.Ordinal)
            {
                "as",
                "async",
                "await",
                "break",
                "case",
                "catch",
                "class",
                "const",
                "continue",
                "debugger",
                "default",
                "delete",
                "do",
                "else",
                "export",
                "extends",
                "false",
                "finally",
                "for",
                "from",
                "function",
                "get",
                "if",
                "import",
                "in",
                "instanceof",
                "let",
                "new",
                "null",
                "of",
                "return",
                "set",
                "static",
                "super",
                "switch",
                "this",
                "throw",
                "true",
                "try",
                "typeof",
                "undefined",
                "var",
                "void",
                "while",
                "with",
                "yield",
            };

            var types = new HashSet<string>(StringComparer.Ordinal)
            {
                "Array",
                "Boolean",
                "Date",
                "Error",
                "Function",
                "Map",
                "Object",
                "Promise",
                "RegExp",
                "Set",
                "String",
                "Number",
                "JSON",
                "Math",
                "console",
            };

            return TokenizeProgrammingLanguage(code, keywords, types, allowHashComments: false);
        }

        private static List<Token> TokenizeTypeScript(string code)
        {
            var keywords = new HashSet<string>(StringComparer.Ordinal)
            {
                "abstract",
                "any",
                "as",
                "asserts",
                "async",
                "await",
                "boolean",
                "break",
                "case",
                "catch",
                "class",
                "const",
                "constructor",
                "continue",
                "declare",
                "default",
                "delete",
                "do",
                "else",
                "enum",
                "export",
                "extends",
                "false",
                "finally",
                "for",
                "from",
                "function",
                "get",
                "if",
                "implements",
                "import",
                "in",
                "infer",
                "interface",
                "is",
                "keyof",
                "let",
                "module",
                "namespace",
                "never",
                "new",
                "null",
                "number",
                "object",
                "of",
                "package",
                "private",
                "protected",
                "public",
                "readonly",
                "require",
                "return",
                "set",
                "static",
                "string",
                "super",
                "switch",
                "symbol",
                "this",
                "throw",
                "true",
                "try",
                "type",
                "typeof",
                "undefined",
                "unknown",
                "var",
                "void",
                "while",
                "with",
                "yield",
            };

            var types = new HashSet<string>(StringComparer.Ordinal)
            {
                "Array",
                "Boolean",
                "Date",
                "Error",
                "Map",
                "Object",
                "Promise",
                "Record",
                "Set",
                "String",
                "Number",
                "React",
            };

            return TokenizeProgrammingLanguage(code, keywords, types, allowHashComments: false);
        }

        private static List<Token> TokenizePython(string code)
        {
            var keywords = new HashSet<string>(StringComparer.Ordinal)
            {
                "and",
                "as",
                "assert",
                "async",
                "await",
                "break",
                "case",
                "class",
                "continue",
                "def",
                "del",
                "elif",
                "else",
                "except",
                "False",
                "finally",
                "for",
                "from",
                "global",
                "if",
                "import",
                "in",
                "is",
                "lambda",
                "match",
                "None",
                "nonlocal",
                "not",
                "or",
                "pass",
                "raise",
                "return",
                "True",
                "try",
                "while",
                "with",
                "yield",
            };

            var types = new HashSet<string>(StringComparer.Ordinal)
            {
                "bool",
                "bytes",
                "complex",
                "dict",
                "float",
                "int",
                "list",
                "object",
                "set",
                "str",
                "tuple",
                "Exception",
            };

            return TokenizeProgrammingLanguage(code, keywords, types, allowHashComments: true);
        }

        private static List<Token> TokenizeJson(string code)
        {
            var tokens = new List<Token>();

            int i = 0;

            while (i < code.Length)
            {
                char c = code[i];

                if (c == '"')
                {
                    int start = i++;

                    while (i < code.Length)
                    {
                        if (code[i] == '\\')
                        {
                            i += Math.Min(2, code.Length - i);
                            continue;
                        }

                        if (code[i] == '"')
                        {
                            i++;
                            break;
                        }

                        i++;
                    }

                    string value = code[start..i];

                    // JSON property names are usually before ':'.
                    int lookAhead = i;

                    while (lookAhead < code.Length && char.IsWhiteSpace(code[lookAhead]))
                    {
                        lookAhead++;
                    }

                    tokens.Add(
                        new Token(
                            lookAhead < code.Length && code[lookAhead] == ':'
                                ? TokenType.Attribute
                                : TokenType.String,
                            value
                        )
                    );

                    continue;
                }

                if (
                    char.IsDigit(c)
                    || (c == '-' && i + 1 < code.Length && char.IsDigit(code[i + 1]))
                )
                {
                    int start = i++;

                    while (i < code.Length && "0123456789.eE+-".IndexOf(code[i]) >= 0)
                    {
                        i++;
                    }

                    tokens.Add(new Token(TokenType.Number, code[start..i]));

                    continue;
                }

                string? keyword = TryReadWord(code, ref i);

                if (keyword != null)
                {
                    TokenType type = keyword switch
                    {
                        "true" or "false" or "null" => TokenType.Keyword,

                        _ => TokenType.Plain,
                    };

                    tokens.Add(new Token(type, keyword));

                    continue;
                }

                tokens.Add(new Token(TokenType.Operator, c.ToString()));

                i++;
            }

            return tokens;
        }

        private static List<Token> TokenizeMarkup(string code)
        {
            var tokens = new List<Token>();

            int i = 0;

            while (i < code.Length)
            {
                if (
                    i + 3 < code.Length
                    && code[i] == '<'
                    && code[i + 1] == '!'
                    && code[i + 2] == '-'
                    && code[i + 3] == '-'
                )
                {
                    int end = code.IndexOf("-->", i + 4, StringComparison.Ordinal);

                    if (end < 0)
                        end = code.Length - 3;

                    end += 3;

                    tokens.Add(new Token(TokenType.Comment, code[i..Math.Min(end, code.Length)]));

                    i = Math.Min(end, code.Length);

                    continue;
                }

                if (code[i] == '<')
                {
                    int end = code.IndexOf('>', i + 1);

                    if (end < 0)
                        end = code.Length - 1;

                    string tag = code[i..(end + 1)];

                    tokens.Add(new Token(TokenType.Keyword, tag));

                    i = end + 1;

                    continue;
                }

                int start = i;

                while (i < code.Length && code[i] != '<')
                {
                    i++;
                }

                if (i > start)
                {
                    tokens.Add(new Token(TokenType.Plain, code[start..i]));
                }
            }

            return tokens;
        }

        private static List<Token> TokenizeCss(string code)
        {
            var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "important",
                "inherit",
                "initial",
                "unset",
                "none",
                "auto",
                "block",
                "inline",
                "flex",
                "grid",
                "absolute",
                "relative",
                "fixed",
            };

            return TokenizeProgrammingLanguage(
                code,
                keywords,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                allowHashComments: false
            );
        }

        private static List<Token> TokenizeShell(string code)
        {
            var keywords = new HashSet<string>(StringComparer.Ordinal)
            {
                "if",
                "then",
                "else",
                "elif",
                "fi",
                "for",
                "while",
                "do",
                "done",
                "case",
                "esac",
                "in",
                "function",
                "select",
                "time",
            };

            return TokenizeProgrammingLanguage(
                code,
                keywords,
                new HashSet<string>(StringComparer.Ordinal),
                allowHashComments: true
            );
        }

        private static List<Token> TokenizeSql(string code)
        {
            var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "SELECT",
                "FROM",
                "WHERE",
                "INSERT",
                "INTO",
                "VALUES",
                "UPDATE",
                "SET",
                "DELETE",
                "CREATE",
                "ALTER",
                "DROP",
                "TABLE",
                "DATABASE",
                "INDEX",
                "JOIN",
                "INNER",
                "LEFT",
                "RIGHT",
                "FULL",
                "OUTER",
                "ON",
                "AS",
                "AND",
                "OR",
                "NOT",
                "NULL",
                "IS",
                "IN",
                "LIKE",
                "GROUP",
                "BY",
                "ORDER",
                "ASC",
                "DESC",
                "HAVING",
                "LIMIT",
                "OFFSET",
                "DISTINCT",
            };

            return TokenizeProgrammingLanguage(
                code,
                keywords,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                allowHashComments: false
            );
        }

        private static List<Token> TokenizeGeneric(string code)
        {
            return TokenizeProgrammingLanguage(
                code,
                new HashSet<string>(),
                new HashSet<string>(),
                allowHashComments: true
            );
        }

        // ------------------------------------------------------------
        // Generic programming tokenizer
        // ------------------------------------------------------------

        private static List<Token> TokenizeProgrammingLanguage(
            string code,
            HashSet<string> keywords,
            HashSet<string> types,
            bool allowHashComments
        )
        {
            var tokens = new List<Token>();

            int i = 0;

            while (i < code.Length)
            {
                char c = code[i];

                // ----------------------------------------------------
                // Whitespace
                // ----------------------------------------------------

                if (char.IsWhiteSpace(c))
                {
                    int start = i;

                    while (i < code.Length && char.IsWhiteSpace(code[i]))
                    {
                        i++;
                    }

                    tokens.Add(new Token(TokenType.Plain, code[start..i]));

                    continue;
                }

                // ----------------------------------------------------
                // // comment
                // ----------------------------------------------------

                if (c == '/' && i + 1 < code.Length && code[i + 1] == '/')
                {
                    int start = i;

                    i += 2;

                    while (i < code.Length && code[i] != '\r' && code[i] != '\n')
                    {
                        i++;
                    }

                    tokens.Add(new Token(TokenType.Comment, code[start..i]));

                    continue;
                }

                // ----------------------------------------------------
                // /* comment */
                // ----------------------------------------------------

                if (c == '/' && i + 1 < code.Length && code[i + 1] == '*')
                {
                    int start = i;

                    i += 2;

                    while (i + 1 < code.Length && !(code[i] == '*' && code[i + 1] == '/'))
                    {
                        i++;
                    }

                    if (i + 1 < code.Length)
                        i += 2;

                    tokens.Add(new Token(TokenType.Comment, code[start..i]));

                    continue;
                }

                // ----------------------------------------------------
                // # comment
                // ----------------------------------------------------

                if (allowHashComments && c == '#')
                {
                    int start = i++;

                    while (i < code.Length && code[i] != '\r' && code[i] != '\n')
                    {
                        i++;
                    }

                    tokens.Add(new Token(TokenType.Comment, code[start..i]));

                    continue;
                }

                // ----------------------------------------------------
                // Strings
                // ----------------------------------------------------

                if (c == '"' || c == '\'' || c == '`')
                {
                    char quote = c;
                    int start = i++;

                    while (i < code.Length)
                    {
                        if (code[i] == '\\')
                        {
                            i += Math.Min(2, code.Length - i);
                            continue;
                        }

                        if (code[i] == quote)
                        {
                            i++;
                            break;
                        }

                        i++;
                    }

                    tokens.Add(new Token(TokenType.String, code[start..i]));

                    continue;
                }

                // ----------------------------------------------------
                // Number
                // ----------------------------------------------------

                if (
                    char.IsDigit(c)
                    || (c == '.' && i + 1 < code.Length && char.IsDigit(code[i + 1]))
                )
                {
                    int start = i++;

                    while (
                        i < code.Length
                        && (
                            char.IsLetterOrDigit(code[i])
                            || code[i] == '.'
                            || code[i] == '_'
                            || code[i] == 'x'
                            || code[i] == 'X'
                        )
                    )
                    {
                        i++;
                    }

                    tokens.Add(new Token(TokenType.Number, code[start..i]));

                    continue;
                }

                // ----------------------------------------------------
                // Preprocessor / directives
                // ----------------------------------------------------

                if (c == '#' && (i == 0 || code[i - 1] == '\n' || code[i - 1] == '\r'))
                {
                    int start = i++;

                    while (i < code.Length && code[i] != '\r' && code[i] != '\n')
                    {
                        i++;
                    }

                    tokens.Add(new Token(TokenType.Preprocessor, code[start..i]));

                    continue;
                }

                // ----------------------------------------------------
                // Identifier / keyword / type / function
                // ----------------------------------------------------

                if (char.IsLetter(c) || c == '_' || c == '$')
                {
                    int start = i++;

                    while (
                        i < code.Length
                        && (char.IsLetterOrDigit(code[i]) || code[i] == '_' || code[i] == '$')
                    )
                    {
                        i++;
                    }

                    string word = code[start..i];

                    if (keywords.Contains(word))
                    {
                        tokens.Add(new Token(TokenType.Keyword, word));
                    }
                    else if (types.Contains(word))
                    {
                        tokens.Add(new Token(TokenType.Type, word));
                    }
                    else
                    {
                        int lookAhead = i;

                        while (lookAhead < code.Length && char.IsWhiteSpace(code[lookAhead]))
                        {
                            lookAhead++;
                        }

                        TokenType type =
                            lookAhead < code.Length && code[lookAhead] == '('
                                ? TokenType.Function
                                : TokenType.Plain;

                        tokens.Add(new Token(type, word));
                    }

                    continue;
                }

                // ----------------------------------------------------
                // Operators / punctuation
                // ----------------------------------------------------

                tokens.Add(new Token(TokenType.Operator, c.ToString()));

                i++;
            }

            return tokens;
        }

        // ------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------

        private static string NormalizeLanguage(string? language)
        {
            if (string.IsNullOrWhiteSpace(language))
                return string.Empty;

            return language.Trim().TrimStart('.').ToLowerInvariant();
        }

        private static string? TryReadWord(string code, ref int index)
        {
            if (index >= code.Length || !(char.IsLetter(code[index]) || code[index] == '_'))
            {
                return null;
            }

            int start = index++;

            while (index < code.Length && (char.IsLetterOrDigit(code[index]) || code[index] == '_'))
            {
                index++;
            }

            return code[start..index];
        }

        private static Brush Brush(string hex)
        {
            return new SolidColorBrush(
                ColorHelper.FromArgb(
                    255,
                    Convert.ToByte(hex.Substring(1, 2), 16),
                    Convert.ToByte(hex.Substring(3, 2), 16),
                    Convert.ToByte(hex.Substring(5, 2), 16)
                )
            );
        }
    }
}
