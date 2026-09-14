using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

class Program
{
    static string settingsFile = "settings.json";

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== 7DTD Translator ===");
        Console.WriteLine();

        var settings = LoadSettings();

        while (true)
        {
            Console.WriteLine("--- МЕНЮ ---");
            Console.WriteLine("1. Разбить на части");
            Console.WriteLine("2. Собрать обратно");
            Console.WriteLine("3. Перевести через Ollama");
            Console.WriteLine("4. Настройки");
            Console.WriteLine("0. Выход");
            Console.Write("Выбор: ");
            string choice = Console.ReadLine()?.Trim() ?? "";

            if (choice == "0") break;
            if (choice == "1") SplitToParts(settings);
            else if (choice == "2") CollectBack(settings);
            else if (choice == "3") await TranslateWithOllama(settings);
            else if (choice == "4") EditSettings(settings);

            Console.WriteLine();
        }
    }

    static Settings LoadSettings()
    {
        if (File.Exists(settingsFile))
        {
            try
            {
                var json = File.ReadAllText(settingsFile, Encoding.UTF8);
                var s = JsonSerializer.Deserialize<Settings>(json);
                if (s != null) return s;
            }
            catch { }
        }
        return new Settings();
    }

    static void SaveSettings(Settings s)
    {
        var json = JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(settingsFile, json, Encoding.UTF8);
    }

    static void EditSettings(Settings s)
    {
        Console.WriteLine();
        Console.Write($"Папка мода [{s.ModDir}]: ");
        string v = Console.ReadLine()?.Trim('"') ?? "";
        if (!string.IsNullOrWhiteSpace(v)) s.ModDir = v;

        Console.Write($"Размер части [{s.PartSize}]: ");
        v = Console.ReadLine()?.Trim() ?? "";
        if (int.TryParse(v, out int ps) && ps > 0) s.PartSize = ps;

        Console.Write($"Модель Ollama [{s.OllamaModel}]: ");
        v = Console.ReadLine()?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(v)) s.OllamaModel = v;

        Console.Write($"Адрес Ollama [{s.OllamaUrl}]: ");
        v = Console.ReadLine()?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(v)) s.OllamaUrl = v;

        SaveSettings(s);
        Console.WriteLine("Настройки сохранены.");
    }

    static void SplitToParts(Settings s)
    {
        if (string.IsNullOrWhiteSpace(s.ModDir) || !Directory.Exists(s.ModDir))
        {
            Console.WriteLine("Папка мода не задана или не найдена. Зайди в Настройки.");
            return;
        }

        string srcFile = Path.Combine(s.ModDir, "Localization.csv");
        if (!File.Exists(srcFile))
        {
            Console.WriteLine("Localization.csv не найден.");
            return;
        }

        var alreadyTranslated = new HashSet<string>();
        string translatedFile = Path.Combine(s.ModDir, "translated.csv");
        if (File.Exists(translatedFile))
        {
            var tl = File.ReadAllLines(translatedFile, Encoding.UTF8);
            if (tl.Length > 0)
            {
                var cols = ParseCsvLine(tl[0]);
                int kIdx = cols.IndexOf("Key");
                int rIdx = cols.IndexOf("russian");
                if (kIdx >= 0 && rIdx >= 0)
                {
                    for (int i = 1; i < tl.Length; i++)
                    {
                        var f = ParseCsvLine(tl[i]);
                        if (f.Count > Math.Max(kIdx, rIdx) && !string.IsNullOrWhiteSpace(f[rIdx]))
                            alreadyTranslated.Add(f[kIdx]);
                    }
                }
            }
        }

        var lines = File.ReadAllLines(srcFile, Encoding.UTF8);
        if (lines.Length < 2) { Console.WriteLine("Файл пустой."); return; }

        var header = ParseCsvLine(lines[0]);
        int keyIdx = header.IndexOf("Key");
        int engIdx = header.IndexOf("english");
        int noTranslateIdx = header.IndexOf("NoTranslate");
        if (keyIdx < 0 || engIdx < 0) { Console.WriteLine("В шапке нет Key или english."); return; }

        var toTranslate = new List<(string key, string eng)>();
        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var f = ParseCsvLine(lines[i]);
            if (f.Count <= Math.Max(keyIdx, engIdx)) continue;
            string key = f[keyIdx];
            string eng = f[engIdx];
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(eng)) continue;

            if (noTranslateIdx >= 0 && f.Count > noTranslateIdx && f[noTranslateIdx].Trim().ToLower() == "x")
                continue;

            if (alreadyTranslated.Contains(key)) continue;

            toTranslate.Add((key, eng));
        }

        string partsDir = Path.Combine(s.ModDir, "parts");
        if (Directory.Exists(partsDir)) Directory.Delete(partsDir, true);
        Directory.CreateDirectory(partsDir);

        int partSize = s.PartSize;
        int totalParts = (toTranslate.Count + partSize - 1) / partSize;
        string instruction = "=== ИНСТРУКЦИЯ ===\n" +
            "Переведи строки ниже с английского на русский.\n" +
            "Формат ответа строго:\n" +
            "номер\n" +
            "K: оригинальный Key\n" +
            "R: перевод\n\n" +
            "Не меняй Key.\n" +
            "Не меняй порядок строк.\n" +
            "Не добавляй и не удаляй строки.\n" +
            "Не переводи теги вида [FF0000], [action:local:Activate], [-], {0}, {poi.name}.\n" +
            "Сохраняй \\n там, где они были.\n" +
            "Верни только формат: номер, K:, R:\n" +
            "=== КОНЕЦ ИНСТРУКЦИИ ===\n\n";

        for (int p = 0; p < totalParts; p++)
        {
            var sb = new StringBuilder();
            sb.Append(instruction);
            int start = p * partSize;
            int end = Math.Min(start + partSize, toTranslate.Count);
            for (int i = start; i < end; i++)
            {
                int n = i - start + 1;
                sb.AppendLine(n.ToString());
                sb.AppendLine("K: " + toTranslate[i].key);
                sb.AppendLine("E: " + toTranslate[i].eng);
                sb.AppendLine();
            }
            string fname = Path.Combine(partsDir, $"part_{p + 1:D3}.txt");
            File.WriteAllText(fname, sb.ToString(), new UTF8Encoding(false));
        }

        Console.WriteLine($"Готово. Создано частей: {totalParts}. Строк: {toTranslate.Count}.");
        Console.WriteLine($"Папка: {partsDir}");
    }

    static void CollectBack(Settings s)
    {
        if (string.IsNullOrWhiteSpace(s.ModDir) || !Directory.Exists(s.ModDir))
        {
            Console.WriteLine("Папка мода не задана или не найдена.");
            return;
        }

        string srcFile = Path.Combine(s.ModDir, "Localization.csv");
        string partsDir = Path.Combine(s.ModDir, "parts");
        if (!File.Exists(srcFile)) { Console.WriteLine("Localization.csv не найден."); return; }
        if (!Directory.Exists(partsDir)) { Console.WriteLine("Папка parts не найдена."); return; }

        var translations = new Dictionary<string, string>();
        foreach (var file in Directory.GetFiles(partsDir, "part_*.txt").OrderBy(x => x))
        {
            var lines = File.ReadAllLines(file, Encoding.UTF8);
            string currentKey = null;
            foreach (var line in lines)
            {
                if (line.StartsWith("K: ")) currentKey = line.Substring(3).Trim();
                else if (line.StartsWith("R: ") && currentKey != null)
                {
                    translations[currentKey] = line.Substring(3).Trim();
                    currentKey = null;
                }
            }
        }

        Console.WriteLine($"Найдено переводов: {translations.Count}");

        var srcLines = File.ReadAllLines(srcFile, Encoding.UTF8);
        var header = ParseCsvLine(srcLines[0]);
        int keyIdx = header.IndexOf("Key");
        int rusIdx = header.IndexOf("russian");
        bool hasRussian = rusIdx >= 0;

        var outLines = new List<string>();
        outLines.Add(hasRussian ? srcLines[0] : srcLines[0] + ",russian");

        for (int i = 1; i < srcLines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(srcLines[i])) { outLines.Add(srcLines[i]); continue; }
            var f = ParseCsvLine(srcLines[i]);
            if (f.Count <= keyIdx) { outLines.Add(srcLines[i]); continue; }
            string key = f[keyIdx];
            if (translations.ContainsKey(key))
            {
                if (hasRussian)
                {
                    while (f.Count <= rusIdx) f.Add("");
                    f[rusIdx] = translations[key];
                    outLines.Add(JoinCsvLine(f));
                }
                else
                {
                    outLines.Add(srcLines[i] + "," + EscapeCsv(translations[key]));
                }
            }
            else
            {
                outLines.Add(srcLines[i]);
            }
        }

        string outFile = Path.Combine(s.ModDir, "translated.csv");
        File.WriteAllLines(outFile, outLines, new UTF8Encoding(false));
        Console.WriteLine($"Готово. Результат: {outFile}");
    }

    static async Task TranslateWithOllama(Settings s)
    {
        if (string.IsNullOrWhiteSpace(s.ModDir) || !Directory.Exists(s.ModDir))
        {
            Console.WriteLine("Папка мода не задана или не найдена.");
            return;
        }

        string srcFile = Path.Combine(s.ModDir, "Localization.csv");
        if (!File.Exists(srcFile)) { Console.WriteLine("Localization.csv не найден."); return; }

        var lines = File.ReadAllLines(srcFile, Encoding.UTF8);
        var header = ParseCsvLine(lines[0]);
        int keyIdx = header.IndexOf("Key");
        int engIdx = header.IndexOf("english");
        int noTranslateIdx = header.IndexOf("NoTranslate");

        var toTranslate = new List<(int lineNo, string key, string eng)>();
        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var f = ParseCsvLine(lines[i]);
            if (f.Count <= Math.Max(keyIdx, engIdx)) continue;
            string key = f[keyIdx];
            string eng = f[engIdx];
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(eng)) continue;
            if (noTranslateIdx >= 0 && f.Count > noTranslateIdx && f[noTranslateIdx].Trim().ToLower() == "x") continue;
            toTranslate.Add((i, key, eng));
        }

        Console.WriteLine($"Строк для перевода: {toTranslate.Count}");
        Console.WriteLine("Начинаю перевод через Ollama...");

        var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(10);
        var translations = new Dictionary<int, string>();

        const int batchSize = 20;
        for (int start = 0; start < toTranslate.Count; start += batchSize)
        {
            int end = Math.Min(start + batchSize, toTranslate.Count);
            var sb = new StringBuilder();
            sb.AppendLine("Переведи строки с английского на русский. Формат ответа:");
            sb.AppendLine("номер");
            sb.AppendLine("K: оригинальный Key");
            sb.AppendLine("R: перевод");
            sb.AppendLine();
            for (int i = start; i < end; i++)
            {
                int n = i - start + 1;
                sb.AppendLine(n.ToString());
                sb.AppendLine("K: " + toTranslate[i].key);
                sb.AppendLine("E: " + toTranslate[i].eng);
                sb.AppendLine();
            }

            var body = new
            {
                model = s.OllamaModel,
                prompt = sb.ToString(),
                stream = false
            };
            var json = JsonSerializer.Serialize(body);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                var resp = await http.PostAsync(s.OllamaUrl + "/api/generate", content);
                if (!resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Пакет {start / batchSize + 1}: ошибка {resp.StatusCode}");
                    continue;
                }
                string respText = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(respText);
                string answer = doc.RootElement.GetProperty("response").GetString() ?? "";

                int got = 0;
                var blocks = answer.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var block in blocks)
                {
                    var bl = block.Split('\n');
                    string k = null, r = null;
                    foreach (var l in bl)
                    {
                        if (l.StartsWith("K:")) k = l.Substring(2).Trim();
                        else if (l.StartsWith("R:")) r = l.Substring(2).Trim();
                    }
                    if (k != null && r != null)
                    {
                        for (int i = start; i < end; i++)
                        {
                            if (toTranslate[i].key == k) { translations[toTranslate[i].lineNo] = r; got++; break; }
                        }
                    }
                }
                Console.WriteLine($"Пакет {start / batchSize + 1}: получено {got} из {end - start}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Пакет {start / batchSize + 1}: ошибка — {ex.Message}");
            }
        }

        int rusIdx = header.IndexOf("russian");
        bool hasRussian = rusIdx >= 0;
        var outLines = new List<string>();
        outLines.Add(hasRussian ? lines[0] : lines[0] + ",russian");
        for (int i = 1; i < lines.Length; i++)
        {
            if (translations.ContainsKey(i))
            {
                if (hasRussian)
                {
                    var f = ParseCsvLine(lines[i]);
                    while (f.Count <= rusIdx) f.Add("");
                    f[rusIdx] = translations[i];
                    outLines.Add(JoinCsvLine(f));
                }
                else outLines.Add(lines[i] + "," + EscapeCsv(translations[i]));
            }
            else outLines.Add(lines[i]);
        }

        string outFile = Path.Combine(s.ModDir, "translated.csv");
        File.WriteAllLines(outFile, outLines, new UTF8Encoding(false));
        Console.WriteLine($"Готово. Результат: {outFile}");
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
            else parts.Add(f);
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

class Settings
{
    public string ModDir { get; set; } = "";
    public int PartSize { get; set; } = 100;
    public string OllamaModel { get; set; } = "qwen3:8b";
    public string OllamaUrl { get; set; } = "http://localhost:11434";
}
