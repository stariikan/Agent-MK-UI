using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Agent_MK_UI.Helpers
{
    /// Splits assistant/user messages into plain-text and fenced-code
    /// segments and extracts useful metadata such as language and a
    /// suggested filename from nearby Markdown headings.

    public static class CodeSnippetHelper
    {
        // ================================================================
        // Data types
        // ================================================================

        public record Segment(
            bool IsCode,
            string Language,
            string Text,
            string? SuggestedFileName = null
        );

        private readonly record struct Fence(char Character, int Length, string Language);

        // ================================================================
        // Public parser
        // ================================================================

        public static List<Segment> Split(string? text)
        {
            var segments = new List<Segment>();

            if (string.IsNullOrEmpty(text))
            {
                segments.Add(new Segment(false, string.Empty, text ?? string.Empty));

                return segments;
            }

            int position = 0;
            int plainTextStart = 0;

            while (position < text.Length)
            {
                int lineStart = position;

                ReadLine(text, position, out int lineEnd, out int nextPosition);

                string line = text.Substring(lineStart, lineEnd - lineStart);

                // --------------------------------------------------------
                // Look for an opening fence.
                // --------------------------------------------------------

                if (!TryParseOpeningFence(line, out Fence openingFence))
                {
                    position = nextPosition;
                    continue;
                }

                // --------------------------------------------------------
                // Search for matching closing fence.
                // --------------------------------------------------------

                int codeStart = nextPosition;

                int scanPosition = nextPosition;

                int closingFenceStart = -1;
                int closingFenceLineEnd = -1;

                while (scanPosition < text.Length)
                {
                    int candidateStart = scanPosition;

                    ReadLine(
                        text,
                        scanPosition,
                        out int candidateLineEnd,
                        out int candidateNextPosition
                    );

                    string candidateLine = text.Substring(
                        candidateStart,
                        candidateLineEnd - candidateStart
                    );

                    if (IsClosingFence(candidateLine, openingFence))
                    {
                        closingFenceStart = candidateStart;

                        closingFenceLineEnd = candidateLineEnd;

                        break;
                    }

                    scanPosition = candidateNextPosition;
                }

                // --------------------------------------------------------
                // No closing fence.
                //
                // Leave the opening fence as normal text instead of
                // destroying the rest of the message.
                // --------------------------------------------------------

                if (closingFenceStart < 0)
                {
                    closingFenceStart = text.Length;
                    closingFenceLineEnd = text.Length;
                }

                // --------------------------------------------------------
                // Plain text before the code block.
                // --------------------------------------------------------

                string plainText = string.Empty;

                if (lineStart > plainTextStart)
                {
                    plainText = text[plainTextStart..lineStart];

                    segments.Add(new Segment(false, string.Empty, plainText));
                }

                // --------------------------------------------------------
                // Code.
                // --------------------------------------------------------

                string code = text.Substring(codeStart, closingFenceStart - codeStart);

                code = RemoveSingleTrailingNewline(code);

                string language = NormalizeLanguage(openingFence.Language);

                if (string.IsNullOrEmpty(language))
                {
                    language = GuessLanguageFromContent(code);
                }

                // --------------------------------------------------------
                // Detect filename from the text immediately before
                // this code block.
                // --------------------------------------------------------

                string? suggestedFileName = ExtractSuggestedFileName(plainText);

                segments.Add(new Segment(true, language, code, suggestedFileName));

                // --------------------------------------------------------
                // Continue after closing fence.
                // --------------------------------------------------------

                plainTextStart = GetPositionAfterLine(text, closingFenceLineEnd);

                position = plainTextStart;
            }

            // ------------------------------------------------------------
            // Remaining text after final code block.
            // ------------------------------------------------------------

            if (plainTextStart < text.Length)
            {
                segments.Add(new Segment(false, string.Empty, text[plainTextStart..]));
            }

            // ------------------------------------------------------------
            // Empty/non-code message.
            // ------------------------------------------------------------

            if (segments.Count == 0)
            {
                segments.Add(new Segment(false, string.Empty, text));
            }

            return MergeAdjacentTextSegments(segments);
        }
        // New method, anywhere in the class:
        private static string GuessLanguageFromContent(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return string.Empty;
            string c = code.TrimStart();

            if (c.StartsWith("#!/usr/bin/env python") || c.StartsWith("#!/usr/bin/python")) return "python";
            if (c.StartsWith("#!/bin/bash") || c.StartsWith("#!/bin/sh")) return "bash";
            if (c.StartsWith("<?php")) return "php";
            if (c.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(c, @"^\s*<html[\s>]", RegexOptions.IgnoreCase)) return "html";
            if (Regex.IsMatch(c, @"^\s*using\s+System[;.]") || Regex.IsMatch(c, @"\bnamespace\s+\w+")) return "csharp";
            if (Regex.IsMatch(c, @"^\s*(import|from)\s+\w+.*\n") && c.Contains("def ")) return "python";
            if (c.Contains("def ") && c.Contains(":") && !c.Contains(";")) return "python";
            if (c.Contains("#include <")) return c.Contains("std::") || c.Contains("cout") ? "cpp" : "c";
            if (Regex.IsMatch(c, @"^\s*(package\s+main|func\s+main\()")) return "go";
            if (c.Contains("fn main(") && c.Contains("let ")) return "rust";
            if (Regex.IsMatch(c, @"\b(public|private)\s+(static\s+)?class\s+\w+") && c.Contains(";")) return "java";
            if (Regex.IsMatch(c, @"^\s*(SELECT|INSERT|UPDATE|DELETE)\s", RegexOptions.IgnoreCase)) return "sql";
            if ((c.TrimStart().StartsWith("{") || c.TrimStart().StartsWith("[")) &&
                Regex.IsMatch(c, @"""\w+""\s*:")) return "json";
            if (Regex.IsMatch(c, @"^\s*[\w.-]+\s*:\s*\S") && !c.Contains(";") && !c.Contains("{")) return "yaml";
            if (Regex.IsMatch(c, @"\b(const|let|var)\s+\w+\s*=") && c.Contains("=>")) return "javascript";
            if (c.Contains("function ") && c.Contains("{") && c.Contains(";")) return "javascript";
            if (Regex.IsMatch(c, @"^\s*[.#]?[\w-]+\s*\{[\s\S]*:\s*[\w#%-]") && c.Contains(";}") == false && c.Contains(";")) return "css";

            return string.Empty;
        }
        // ================================================================
        // Opening fence
        // ================================================================

        private static bool TryParseOpeningFence(string line, out Fence fence)
        {
            fence = default;

            if (string.IsNullOrEmpty(line))
                return false;

            int index = 0;
            int indentation = 0;

            // Markdown allows up to three leading spaces.
            while (index < line.Length && line[index] == ' ' && indentation < 3)
            {
                index++;
                indentation++;
            }

            if (index >= line.Length)
                return false;

            char fenceCharacter = line[index];

            if (fenceCharacter != '`' && fenceCharacter != '~')
            {
                return false;
            }

            int fenceLength = 0;

            while (index < line.Length && line[index] == fenceCharacter)
            {
                fenceLength++;
                index++;
            }

            // Minimum Markdown fence length.
            if (fenceLength < 3)
                return false;

            string info = line[index..].Trim();

            // A backtick fence's info string may not contain a backtick.
            if (fenceCharacter == '`' && info.Contains('`'))
            {
                return false;
            }

            string language = ExtractLanguage(info);

            fence = new Fence(fenceCharacter, fenceLength, language);

            return true;
        }

        // ================================================================
        // Closing fence
        // ================================================================

        private static bool IsClosingFence(string line, Fence openingFence)
        {
            if (string.IsNullOrEmpty(line))
                return false;

            int index = 0;
            int indentation = 0;

            while (index < line.Length && line[index] == ' ' && indentation < 3)
            {
                index++;
                indentation++;
            }

            if (index >= line.Length)
                return false;

            // Same fence character required.
            if (line[index] != openingFence.Character)
            {
                return false;
            }

            int fenceLength = 0;

            while (index < line.Length && line[index] == openingFence.Character)
            {
                fenceLength++;
                index++;
            }

            // Closing fence cannot be shorter.
            if (fenceLength < openingFence.Length)
            {
                return false;
            }

            // Only spaces/tabs after a closing fence.
            while (index < line.Length)
            {
                char c = line[index];

                if (c != ' ' && c != '\t')
                {
                    return false;
                }

                index++;
            }

            return true;
        }

        // ================================================================
        // Language
        // ================================================================

        private static string ExtractLanguage(string info)
        {
            if (string.IsNullOrWhiteSpace(info))
                return string.Empty;

            string firstToken =
                info.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault()
                ?? string.Empty;

            return firstToken;
        }

        private static string NormalizeLanguage(string language)
        {
            if (string.IsNullOrWhiteSpace(language))
                return string.Empty;

            return language.Trim().TrimStart('.').ToLowerInvariant();
        }

        // ================================================================
        // Filename extraction
        // ================================================================

        private static string? ExtractSuggestedFileName(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var matches = Regex.Matches(
                text,
                @"(?m)^[ \t]{0,3}#{1,6}[ \t]+[`*_\[]*([A-Za-z0-9_.\-\\/]+\.[A-Za-z0-9]+)[`*_ \]]*[ \t]*$",
                RegexOptions.Multiline
            );

            if (matches.Count == 0)
                return null;

            // Grab the heading closest to the code block
            Match lastMatch = matches.Last();

            string fileName = lastMatch.Groups[1].Value.Trim();

            if (string.IsNullOrWhiteSpace(fileName))
                return null;

            // Extract just the filename if a path was provided (e.g., src/components/Button.jsx -> Button.jsx)
            fileName = Path.GetFileName(fileName);

            if (string.IsNullOrWhiteSpace(fileName))
                return null;

            // Make sure this is a legal Windows filename.
            foreach (char invalidChar in Path.GetInvalidFileNameChars())
            {
                if (fileName.Contains(invalidChar))
                    return null;
            }

            return fileName;
        }

        // ================================================================
        // Line handling
        // ================================================================

        private static void ReadLine(string text, int start, out int lineEnd, out int nextPosition)
        {
            int position = start;

            while (position < text.Length)
            {
                char c = text[position];

                if (c == '\r' || c == '\n')
                {
                    break;
                }

                position++;
            }

            lineEnd = position;

            if (position >= text.Length)
            {
                nextPosition = position;
                return;
            }

            // CRLF
            if (text[position] == '\r' && position + 1 < text.Length && text[position + 1] == '\n')
            {
                nextPosition = position + 2;

                return;
            }

            // CR or LF
            nextPosition = position + 1;
        }

        private static int GetPositionAfterLine(string text, int lineEnd)
        {
            if (lineEnd >= text.Length)
                return lineEnd;

            // CRLF
            if (text[lineEnd] == '\r' && lineEnd + 1 < text.Length && text[lineEnd + 1] == '\n')
            {
                return lineEnd + 2;
            }

            return lineEnd + 1;
        }

        private static string RemoveSingleTrailingNewline(string value)
        {
            if (value.EndsWith("\r\n", StringComparison.Ordinal))
            {
                return value[..^2];
            }

            if (
                value.EndsWith("\n", StringComparison.Ordinal)
                || value.EndsWith("\r", StringComparison.Ordinal)
            )
            {
                return value[..^1];
            }

            return value;
        }

        // ================================================================
        // Segment cleanup
        // ================================================================

        private static List<Segment> MergeAdjacentTextSegments(List<Segment> segments)
        {
            if (segments.Count < 2)
                return segments;

            var result = new List<Segment>();

            foreach (var segment in segments)
            {
                if (result.Count > 0 && !segment.IsCode && !result[^1].IsCode)
                {
                    Segment previous = result[^1];

                    result[^1] = new Segment(false, string.Empty, previous.Text + segment.Text);
                }
                else
                {
                    result.Add(segment);
                }
            }

            return result;
        }

        // ================================================================
        // File extensions
        // ================================================================

        private static readonly Dictionary<string, string> LanguageToExtension = new(
            StringComparer.OrdinalIgnoreCase
        )
        {
            // C#
            ["cs"] = ".cs",
            ["csharp"] = ".cs",
            ["c#"] = ".cs",

            // Razor / Blazor
            ["razor"] = ".razor",
            ["cshtml"] = ".cshtml",

            // Python
            ["py"] = ".py",
            ["python"] = ".py",

            // JavaScript
            ["js"] = ".js",
            ["javascript"] = ".js",

            // TypeScript
            ["ts"] = ".ts",
            ["typescript"] = ".ts",

            // React
            ["jsx"] = ".jsx",
            ["tsx"] = ".tsx",

            // Java
            ["java"] = ".java",

            // C / C++
            ["c"] = ".c",
            ["cpp"] = ".cpp",
            ["c++"] = ".cpp",
            ["cc"] = ".cpp",
            ["h"] = ".h",
            ["hpp"] = ".hpp",

            // Web
            ["html"] = ".html",
            ["htm"] = ".html",
            ["css"] = ".css",
            ["scss"] = ".scss",
            ["sass"] = ".sass",
            ["less"] = ".less",

            // Data
            ["json"] = ".json",
            ["xml"] = ".xml",
            ["yaml"] = ".yaml",
            ["yml"] = ".yaml",
            ["toml"] = ".toml",
            ["ini"] = ".ini",

            // SQL
            ["sql"] = ".sql",

            // Shell
            ["sh"] = ".sh",
            ["bash"] = ".sh",
            ["shell"] = ".sh",

            // Windows
            ["powershell"] = ".ps1",
            ["pwsh"] = ".ps1",
            ["ps1"] = ".ps1",
            ["bat"] = ".bat",
            ["batch"] = ".bat",
            ["cmd"] = ".cmd",

            // Other
            ["go"] = ".go",
            ["rust"] = ".rs",
            ["rs"] = ".rs",
            ["php"] = ".php",
            ["rb"] = ".rb",
            ["ruby"] = ".rb",
            ["swift"] = ".swift",
            ["kt"] = ".kt",
            ["kotlin"] = ".kt",
            ["dart"] = ".dart",
            ["lua"] = ".lua",

            // Frameworks
            ["vue"] = ".vue",
            ["svelte"] = ".svelte",

            // Documentation
            ["md"] = ".md",
            ["markdown"] = ".md",
            ["txt"] = ".txt",
            ["text"] = ".txt",
            ["plaintext"] = ".txt",

            // Misc
            ["dockerfile"] = ".dockerfile",
            ["graphql"] = ".graphql",
            ["proto"] = ".proto",
        };

        // ================================================================
        // Public extension helper
        // ================================================================

        public static string ExtensionForLanguage(string? language)
        {
            if (string.IsNullOrWhiteSpace(language))
                return ".txt";

            string normalized = NormalizeLanguage(language);

            // Handle cases where the entire info string was passed.
            normalized =
                normalized
                    .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault()
                ?? string.Empty;

            if (LanguageToExtension.TryGetValue(normalized, out string? extension))
            {
                return extension;
            }

            return ".txt";
        }
    }
}
