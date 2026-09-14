using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

class Program
{
    static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== 7DTD Translator (Groq) ===");
        Console.WriteLine();

        Console.Write("Папка мода: ");
        string modDir = Console.ReadLine()?.Trim('"') ?? "";
        if (!Directory.Exists(modDir))
        {
            Console.WriteLine("Папка не найдена.");
            return;
        }

        string srcFile = Path.Combine(modDir, "Localization.csv");
        if (!File.Exists(srcFile))
        {
            Console.WriteLine("Localization.csv не найден в папке.");
            return;
        }

        Console.Write("Groq API key: ");
        string apiKey = Console.ReadLine()?.Trim() ?? "";
        if (string.IsNullOrEmpty(apiKey))
        {
            Console.WriteLine("Ключ не введён.");
            return;
        }

        string outFile = Path.Combine(modDir, "translated.csv");
        string memoryFile = Path.Combine(modDir, "memory.json");
        string progressFile = Path.Combine(modDir, "progress.json");

        var lines = File.ReadAllLines(srcFile, Encoding.UTF8);
        if (lines.Length < 2)
        {
            Console.WriteLine("Файл пустой.");
            return;
        }

        string header = lines[0];
        var columns = header.Split(',');
        int keyIdx = Array.IndexOf(columns, "Key");
        int engIdx = Array.IndexOf(columns, "english");
        int rusIdx = Array.IndexOf(columns, "russian");

        if (keyIdx < 0 || engIdx < 0)
        {
            Console.WriteLine("В шапке нет Key или english.");
            return;
        }

        bool hasRussian = rusIdx >= 0;

        var entries = new List<(int lineNo, string key, string english)>();
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            var fields = ParseCsvLine(line);
            if (fields.Count <= Math.Max(keyIdx, engIdx)) continue;

            string key = fields[keyIdx];
            string eng = fields[engIdx];
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(eng)) continue;
            if (key == "Key" && eng == "english") continue;

            entries.Add((i, key, eng));
        }

        Console.WriteLine($"Строк для перевода: {entries.Count}");
        Console.WriteLine("Начинаю перевод пакетами по 20 строк...");
        Console.WriteLine();

        var http = new HttpClient();
        http.DefaultRequestHeaders.Add("Authorization", "Bearer " + apiKey);

        const int batchSize = 20;
        var translations = new Dictionary<int, string>();

        for (int start = 0; start < entries.Count; start += batchSize)
        {
            int end = Math.Min(start + batchSize, entries.Count);
            var sb = new StringBuilder();
            sb.AppendLine("Переведи следующие строки с английского на русский.");
            sb.AppendLine("Формат ответа строго:");
            sb.AppendLine("номер");
            sb.AppendLine("K: оригинальный Key");
            sb.AppendLine("R: перевод");
            sb.AppendLine();
            sb.AppendLine("Не меняй Key. Не меняй теги вида [FF0000], [action:local:Activate], [-], {0}, {poi.name}. Сохраняй \\n там, где они были.");
            sb.AppendLine();

            for (int i = start; i < end; i++)
            {
                int n = i - start + 1;
                sb.AppendLine(n.ToString());
                sb.AppendLine("K: " + entries[i].key);
                sb.AppendLine("E: " + entries[i].english);
                sb.AppendLine();
            }

            var body = new
            {
                model = "openai/gpt-oss-120b",
                messages = new[]
                {
                    new { role = "user", content = sb.ToString() }
                },
                temperature = 0.2
            };

            var json = JsonSerializer.Serialize(body);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            HttpResponseMessage resp;
            try
            {
                resp = await http.PostAsync("https://api.groq.com/openai/v1/chat/completions", content);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Пакет {start / batchSize + 1}: ошибка сети — {ex.Message}");
                continue;
            }

            if (!resp.IsSuccessStatusCode)
            {
                string err = await resp.Content.ReadAsStringAsync();
                Console.WriteLine($"Пакет {start / batchSize + 1}: сервер вернул {resp.StatusCode}");
                Console.WriteLine(err);
                continue;
            }

            string respText = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(respText);
            string answer = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";

            int got = 0;
            var blocks = answer.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var block in blocks)
            {
                var blockLines = block.Split('\n');
                string? k = null, r = null;
                foreach (var bl in blockLines)
                {
                    if (bl.StartsWith("K:")) k = bl.Substring(2).Trim();
                    else if (bl.StartsWith("R:")) r = bl.Substring(2).Trim();
                }
                if (k == null || r == null) continue;

                for (int i = start; i < end; i++)
                {
                    if (entries[i].key == k)
                    {
                        translations[entries[i].lineNo] = r;
                        got++;
                        break;
                    }
                }
            }

            Console.WriteLine($"Пакет {start / batchSize + 1}: получено {got} из {end - start}");
        }

        var outLines = new List<string>();
        outLines.Add(hasRussian ? header : header + ",russian");

        for (int i = 1; i < lines.Length; i++)
        {
            if (translations.ContainsKey(i))
            {
                if (hasRussian)
                {
                    var fields = ParseCsvLine(lines[i]);
                    while (fields.Count <= rusIdx) fields.Add("");
                    fields[rusIdx] = translations[i];
                    outLines.Add(JoinCsvLine(fields));
                }
                else
                {
                    outLines.Add(lines[i] + "," + EscapeCsv(translations[i]));
                }
            }
            else
            {
                outLines.Add(lines[i]);
            }
        }

        File.WriteAllLines(outFile, outLines, new UTF8Encoding(false));
        File.WriteAllText(progressFile, "{\"done\":true}", Encoding.UTF8);

        Console.WriteLine();
        Console.WriteLine($"Готово. Результат: {outFile}");
        Console.WriteLine($"Переведено строк: {translations.Count}");
        Console.WriteLine("Нажми Enter для выхода.");
        Console.ReadLine();
    }

    static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { result.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
        }
        result.Add(sb.ToString());
        return result;
    }

    static string JoinCsvLine(List<string> fields)
    {
        var parts = new List<string>();
        foreach (var f in fields)
        {
            if (f.Contains(',') || f.Contains('"') || f.Contains('\n'))
                parts.Add("\"" + f.Replace("\"", "\"\"") + "\"");
            else
                parts.Add(f);
        }
        return string.Join(",", parts);
    }

    static string EscapeCsv(string s)
    {
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }
}
