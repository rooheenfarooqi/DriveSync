using System.Text.RegularExpressions;

namespace DriveSync.Core.Parsing;

/// <summary>
/// Reads source code and lists its namespaces, types and members for the Outline tab and the
/// structural overview ("Declares 2 types: IndexEngine, SearchHit. Members: Add, Remove…").
/// </summary>
public static partial class CodeOutline
{
    public static string LanguageOf(string ext) => ext switch
    {
        "cs" => "C#", "java" => "Java", "kt" or "kts" => "Kotlin", "scala" => "Scala", "dart" => "Dart",
        "py" or "pyw" or "ipynb" => "Python", "js" or "mjs" or "cjs" or "jsx" => "JavaScript", "ts" or "tsx" => "TypeScript",
        "go" => "Go", "rs" => "Rust", "php" => "PHP", "rb" => "Ruby", "swift" => "Swift", "c" or "h" => "C",
        "cpp" or "cc" or "cxx" or "hpp" or "hh" => "C++", "m" or "mm" => "Objective-C", "r" => "R", "lua" => "Lua",
        "sql" => "SQL", "html" or "htm" => "HTML", "css" or "scss" or "sass" or "less" => "CSS", "xaml" or "axaml" => "XAML",
        "json" or "jsonc" => "JSON", "yaml" or "yml" => "YAML", "xml" or "csproj" or "vbproj" or "fsproj" or "props" or "targets" => "XML",
        "sh" or "bash" or "zsh" => "Shell", "ps1" or "psm1" => "PowerShell", "bat" or "cmd" => "Batch", "vb" => "Visual Basic",
        "fs" or "fsx" => "F#", "toml" => "TOML", "sln" => "Solution", "vue" => "Vue", "svelte" => "Svelte",
        _ => ext.ToUpperInvariant(),
    };

    [GeneratedRegex(@"^\s*(?:namespace|package|module)\s+([\w.:]+)")]
    private static partial Regex NamespacePattern();

    [GeneratedRegex(@"^\s*(?:import|using|#include|require|from)\b\s*(.+?);?\s*$")]
    private static partial Regex ImportPattern();

    [GeneratedRegex(@"^\s*(?:(?:public|private|protected|internal|static|abstract|sealed|final|partial|export|default|open|data|readonly|unsafe|pub(?:\(crate\))?)\s+)*(class|interface|struct|record|enum|trait|impl|object|protocol|extension|mixin|type)\s+([A-Za-z_]\w*)")]
    private static partial Regex TypePattern();

    [GeneratedRegex(@"^type\s+([A-Za-z_]\w*)\s+(?:struct|interface)")]
    private static partial Regex GoTypePattern();

    [GeneratedRegex(@"^\s*(?:async\s+)?def\s+([A-Za-z_]\w*)\s*\(|^\s*(?:pub(?:\(crate\))?\s+)?(?:async\s+)?fn\s+([A-Za-z_]\w*)|^\s*func\s+(?:\([^)]*\)\s*)?([A-Za-z_]\w*)\s*\(|^\s*(?:export\s+)?(?:async\s+)?function\s*\*?\s*([A-Za-z_$][\w$]*)\s*\(|^\s*(?:(?:public|private|protected|internal|override|suspend|inline)\s+)*fun\s+(?:<[^>]+>\s*)?([A-Za-z_]\w*)\s*\(|^\s*(?:export\s+)?(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*=\s*(?:async\s*)?(?:\([^)]*\)|[A-Za-z_$][\w$]*)\s*=>")]
    private static partial Regex FunctionPattern();

    [GeneratedRegex(@"^\s*(?:(?:public|private|protected|internal|static|virtual|override|abstract|async|sealed|extern|unsafe|new|partial|readonly|final|synchronized|const|inline|explicit|constexpr|external|factory|@\w+)\s+)*(?:[\w<>\[\],.?*&:]+\s+)+([A-Za-z_]\w*)\s*(?:<[^>]*>)?\s*\((?![^)]*;\s*$)")]
    private static partial Regex MethodPattern();

    [GeneratedRegex(@"^\s*(?:(?:static|async|get|set|public|private|protected|override)\s+)*([A-Za-z_$][\w$]*)\s*\([^)]*\)\s*(?::\s*[\w<>\[\]|, .?]+)?\s*\{\s*$")]
    private static partial Regex ClassMethodPattern();

    private static readonly HashSet<string> NotMembers = new(StringComparer.Ordinal)
    {
        "if", "for", "foreach", "while", "switch", "return", "new", "catch", "using", "lock", "else", "do", "try",
        "throw", "sizeof", "typeof", "nameof", "await", "yield", "when", "case", "fixed", "checked", "unchecked", "default", "base", "this", "super", "print",
    };

    /// <summary>Reads a code file and builds its outline.</summary>
    public static ParsedDocument Read(Stream stream, string ext)
    {
        var doc = new ParsedDocument { IsCode = true, Language = LanguageOf(ext) };
        string text = TextReaders.ReadAllText(stream);
        doc.RawText = text;
        doc.Characters = text.Length;
        if (ext is "json" or "jsonc" or "yaml" or "yml" or "xml" or "csproj" or "props" or "targets" or "toml" or "sln" or "css" or "scss" or "less" or "sass")
            return doc; // configuration and styles: no members, the overview counts lines instead

        bool python = ext is "py" or "pyw";
        int imports = 0;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length > 400 || line.TrimStart().StartsWith("//", StringComparison.Ordinal) || line.TrimStart().StartsWith('*')) continue;
            Match m;
            if ((m = NamespacePattern().Match(line)).Success)
            {
                doc.Outline.Add(new OutlineItem(1, line.Trim().TrimEnd('{', ';', ' '), "ns"));
            }
            else if (imports < 6 && (ext is "dart" or "py" or "pyw" or "js" or "ts" or "jsx" or "tsx" or "mjs" or "go" or "swift") &&
                     (m = ImportPattern().Match(line)).Success && !line.StartsWith(' ') && !line.StartsWith('\t'))
            {
                imports++;
                doc.Outline.Add(new OutlineItem(1, line.Trim().TrimEnd(';'), "ns"));
            }
            else if ((m = TypePattern().Match(line)).Success || (m = GoTypePattern().Match(line)).Success)
            {
                string decl = line.Trim();
                int brace = decl.IndexOf('{');
                if (brace > 0) decl = decl[..brace].Trim();
                if (decl.EndsWith(':') && python) decl = decl[..^1];
                doc.Outline.Add(new OutlineItem(2, Truncate(decl, 140), "type"));
            }
            else if ((m = FunctionPattern().Match(line)).Success)
            {
                string? name = m.Groups.Cast<Group>().Skip(1).FirstOrDefault(g => g.Success)?.Value;
                if (name != null && !NotMembers.Contains(name))
                    doc.Outline.Add(new OutlineItem(3, Truncate(Signature(line), 140), "fn"));
            }
            else if (!python && ((m = MethodPattern().Match(line)).Success || (m = ClassMethodPattern().Match(line)).Success))
            {
                string name = m.Groups[1].Value;
                if (!NotMembers.Contains(name) && !line.TrimStart().StartsWith("return", StringComparison.Ordinal) && !line.Contains(" = new ", StringComparison.Ordinal))
                    doc.Outline.Add(new OutlineItem(3, Truncate(Signature(line), 140), "fn"));
            }
            if (doc.Outline.Count >= 400) break;
        }
        return doc;
    }

    /// <summary>The declaration up to the end of its parameter list.</summary>
    private static string Signature(string line)
    {
        string s = line.Trim();
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')' && --depth == 0) return s[..(i + 1)];
        }
        return s.TrimEnd('{', ':', ' ');
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>The first doc comment or leading comment, as a sentence.</summary>
    public static string? DocComment(string text)
    {
        Match m = Regex.Match(text, @"///\s*(?:<summary>)?\s*(.+)");
        if (m.Success && !m.Groups[1].Value.TrimStart().StartsWith('<'))
            return Sentence(Regex.Replace(m.Groups[1].Value, "<[^>]+>", ""));
        m = Regex.Match(text, @"///\s*<summary>\s*\r?\n\s*///\s*(.+)");
        if (m.Success) return Sentence(Regex.Replace(m.Groups[1].Value, "<[^>]+>", ""));
        m = Regex.Match(text, "(?:\"\"\"|''')\\s*(.+?)\\s*(?:\"\"\"|'''|\\r?\\n)", RegexOptions.Singleline);
        if (m.Success && m.Index < 2000) return Sentence(m.Groups[1].Value);
        m = Regex.Match(text, @"/\*\*\s*\r?\n?\s*\*?\s*([^@\r\n][^\r\n]*)");
        if (m.Success && m.Index < 4000) return Sentence(m.Groups[1].Value.TrimEnd('*', '/', ' '));
        foreach (string raw in text.Split('\n').Take(15))
        {
            string line = raw.Trim();
            if (line.StartsWith("#!", StringComparison.Ordinal)) continue;
            Match c = Regex.Match(line, @"^(?://|#|--|;)\s*(.{12,})$");
            if (c.Success && !Regex.IsMatch(c.Groups[1].Value, @"(?i)copyright|license|SPDX|eslint|sourceMappingURL|jshint|prettier|pragma|-\*-|^region|^endregion|^include|^define|^if")) return Sentence(c.Groups[1].Value);
        }
        return null;
    }

    private static string Sentence(string s)
    {
        s = s.Trim().TrimEnd(':', ',', ';', '-').TrimEnd();
        return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..] + (s.EndsWith('.') || s.EndsWith('!') || s.EndsWith('?') ? "" : ".");
    }
}
