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
    static string logFile = "log.txt";
    static Settings settings;

    static void Log(string msg)
    {
        string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg;
        Console.WriteLine(line);
        try { File.AppendAllText(logFile, line + Environment.NewLine, Encoding.UTF8); } catch { }
    }

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        File.WriteAllText(logFile, "", Encoding.UTF8);

        Log("=== 7DTD Translator ===");
        Log("");

        settings = LoadSettings();

        if (string.IsNullOrWhiteSpace(settings.ModDir) || !Directory.Exists(settings.ModDir))
        {
            Log("Настройки пустые. Открываю настройки.");
            EditSettings();
        }

        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("--- МЕНЮ ---");
            Console.WriteLine("1. Настройки");
            Console.WriteLine("2. Разбить на части");
            Console.WriteLine("3. Перевести через Ollama");
            Console.WriteLine("4. Собрать обратно");
            Console.WriteLine("0. Выход");
            Console.Write("Выбор: ");
            string choice = Console.ReadLine()?.Trim() ?? "";

            if (choice == "0") break;
            if (choice == "1") EditSettings();
            else if (choice == "2") SplitToParts();
            else if (choice == "3") await TranslateWithOllama();
            else if (choice == "4") CollectBack();
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

    static void SaveSettings()
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(settingsFile, json, Encoding.UTF8);
    }

    static void EditSettings()
    {
        Console.WriteLine();
        Log("--- Настройки ---");

        Console.Write($"Папка мода [{settings.ModDir}]: ");
        string v = Console.ReadLine()?.Trim().Trim('"') ?? "";
        if (!string.IsNullOrWhiteSpace(v)) settings.ModDir = v;

        Console.Write($"Размер части [{settings.PartSize}]: ");
        v = Console.ReadLine()?.Trim() ?? "";
        if (int.TryParse(v, out int ps) && ps > 0) settings.PartSize = ps;

        Console.Write($"Модель Ollama [{settings.OllamaModel}]: ");
        v = Console.ReadLine()?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(v)) settings.OllamaModel = v;

        Console.Write($"Адрес Ollama [{settings.OllamaUrl}]: ");
        v = Console.ReadLine()?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(v)) settings.OllamaUrl = v;

        SaveSettings();
        Log("Настройки сохранены.");
    }

    static List<(string key, string eng)> ReadToTranslate()
    {
        string srcFile = Path.Combine(settings.ModDir, "Localization.csv");
        var lines = File.ReadAllLines(srcFile, Encoding.UTF8);
        if (lines.Length < 2) return new List<(string, string)>();

        var header = ParseCsvLine(lines[0]);
        int keyIdx = header.IndexOf("Key");
        int engIdx = header.IndexOf("english");
        int noTranslateIdx = header.IndexOf("NoTranslate");

        var result = new List<(string, string)>();
        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var f = ParseCsvLine(lines[i]);
            if (f.Count <= Math.Max(keyIdx, engIdx)) continue;
            string key = f[keyIdx];
            string eng = f[engIdx];
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(eng)) continue;
            if (noTranslateIdx >= 0 && f.Count > noTranslateIdx && f[noTranslateIdx].Trim().ToLower() == "x") continue;
            result.Add((key, eng));
        }
        return result;
    }

    static void SplitToParts()
    {
        if (!Directory.Exists(settings.ModDir)) { Log("Папка мода не найдена."); return; }
        string srcFile = Path.Combine(settings.ModDir, "Localization.csv");
        if (!File.Exists(srcFile)) { Log("Localization.csv не найден."); return; }

        var toTranslate = ReadToTranslate();
        Log($"Строк для перевода: {toTranslate.Count}");

        string partsDir = Path.Combine(settings.ModDir, "parts");
        if (Directory.Exists(partsDir)) Directory.Delete(partsDir, true);
        Directory.CreateDirectory(partsDir);

        int partSize = settings.PartSize;
        int totalParts = (toTranslate.Count + partSize - 1) / partSize;

        string instruction = "=== ИНСТРУКЦИЯ ===\n" +
            "Ты — переводчик. Переведи строки ниже с английского на русский.\n\n" +
            "Формат ответа строго такой:\n\n" +
            "1\nK: оригинальный Key\nR: перевод\n\n" +
            "2\nK: оригинальный Key\nR: перевод\n\n" +
            "Правила:\n" +
            "- Не меняй Key.\n" +
            "- Не меняй порядок строк.\n" +
            "- Не добавляй и не удаляй строки.\n" +
            "- Не переводи теги вида [FF0000], [action:local:Activate], [-], {0}, {poi.name}.\n" +
            "- Сохраняй \\n там, где они были.\n" +
            "- Возвращай только формат: номер, K:, R:\n" +
            "- Не пиши пояснений.\n" +
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

        Log($"Готово. Создано частей: {totalParts}. Папка: {partsDir}");
    }

    static void CollectBack()
    {
        if (!Directory.Exists(settings.ModDir)) { Log("Папка мода не найдена."); return; }
        string srcFile = Path.Combine(settings.ModDir, "Localization.csv");
        string partsDir = Path.Combine(settings.ModDir, "parts");
        if (!File.Exists(srcFile)) { Log("Localization.csv не найден."); return; }
        if (!Directory.Exists(partsDir)) { Log("Папка parts не найдена."); return; }

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
        Log($"Найдено переводов: {translations.Count}");

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
                else outLines.Add(srcLines[i] + "," + EscapeCsv(translations[key]));
            }
            else outLines.Add(srcLines[i]);
        }

        string outFile = Path.Combine(settings.ModDir, "translated.csv");
        File.WriteAllLines(outFile, outLines, new UTF8Encoding(false));
        Log($"Готово. Результат: {outFile}");
    }

    static async Task TranslateWithOllama()
    {
        if (!Directory.Exists(settings.ModDir)) { Log("Папка мода не найдена."); return; }
        string srcFile = Path.Combine(settings.ModDir, "Localization.csv");
        if (!File.Exists(srcFile)) { Log("Localization.csv не найден."); return; }

        // Проверка Ollama
        Log("Проверяю Ollama...");
        using (var check = new HttpClient())
        {
            check.Timeout = TimeSpan.FromSeconds(10);
            try
            {
                var r = await check.GetAsync(settings.OllamaUrl + "/api/tags");
                if (!r.IsSuccessStatusCode) { Log($"Ollama вернула {r.StatusCode}. Выход."); return; }
                Log("Ollama отвечает.");
            }
            catch (Exception ex)
            {
                Log("Ollama не отвечает: " + ex.Message);
                return;
            }
        }

        var toTranslate = ReadToTranslate();
        Log($"Строк для перевода: {toTranslate.Count}");

        int totalBatches = (toTranslate.Count + 19) / 20;
        Log($"Всего пакетов: {totalBatches}");
        Console.Write($"Сколько пакетов перевести? (1-{totalBatches}, all, или 1,2,3): ");
        string answer = Console.ReadLine()?.Trim().ToLower() ?? "all";

        var batchesToDo = new List<int>();
        if (answer == "all" || answer == "")
        {
            for (int i = 0; i < totalBatches; i++) batchesToDo.Add(i);
        }
        else if (answer.Contains("-"))
        {
            var p = answer.Split('-');
            if (int.TryParse(p[0], out int a) && int.TryParse(p[1], out int b))
                for (int i = a; i <= b && i <= totalBatches; i++) batchesToDo.Add(i - 1);
        }
        else
        {
            foreach (var s in answer.Split(','))
                if (int.TryParse(s.Trim(), out int n) && n >= 1 && n <= totalBatches) batchesToDo.Add(n - 1);
        }

        Log($"Будут переведены пакеты: {string.Join(",", batchesToDo.Select(x => x + 1))}");

        var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(5);
        var translations = new Dictionary<string, string>();

        foreach (int b in batchesToDo)
        {
            int start = b * 20;
            int end = Math.Min(start + 20, toTranslate.Count);

            var sb = new StringBuilder();
            sb.AppendLine("Переведи строки с английского на русский. Формат:");
            sb.AppendLine("1");
            sb.AppendLine("K: Key");
            sb.AppendLine("R: перевод");
            sb.AppendLine();
            for (int i = start; i < end; i++)
            {
                sb.AppendLine((i - start + 1).ToString());
                sb.AppendLine("K: " + toTranslate[i].key);
                sb.AppendLine("E: " + toTranslate[i].eng);
                sb.AppendLine();
            }

            Log($"Пакет {b + 1}: отправляю {end - start} строк...");

            var body = new
            {
                model = settings.OllamaModel,
                prompt = sb.ToString(),
                stream = false
            };
            var json = JsonSerializer.Serialize(body);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                var resp = await http.PostAsync(settings.OllamaUrl + "/api/generate", content);
                if (!resp.IsSuccessStatusCode) { Log($"Пакет {b + 1}: ошибка {resp.StatusCode}"); continue; }
                string respText = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(respText);
                string respAnswer = doc.RootElement.GetProperty("response").GetString() ?? "";

                int got = 0;
                var blocks = respAnswer.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
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
                            if (toTranslate[i].key == k) { translations[toTranslate[i].key] = r; got++; break; }
                        }
                    }
                }
                Log($"Пакет {b + 1}: получено {got} из {end - start}");
            }
            catch (Exception ex)
            {
                Log($"Пакет {b + 1}: ошибка — {ex.Message}");
            }
        }

        // Сохраняем в файл переводов, не в translated.csv
        string outFile = Path.Combine(settings.ModDir, "ollama_translated.txt");
        var sbOut = new StringBuilder();
        foreach (var kv in translations)
        {
            sbOut.AppendLine("K: " + kv.Key);
            sbOut.AppendLine("R: " + kv.Value);
            sbOut.AppendLine();
        }
        File.WriteAllText(outFile, sbOut.ToString(), new UTF8Encoding(false));
        Log($"Готово. Переводов: {translations.Count}. Файл: {outFile}");
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
