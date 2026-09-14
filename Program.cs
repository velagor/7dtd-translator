using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

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

    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        File.WriteAllText(logFile, "", Encoding.UTF8);

        Log("=== 7DTD Translator (ручной режим) ===");
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
            Console.WriteLine("3. Собрать обратно");
            Console.WriteLine("4. Проверить части");
            Console.WriteLine("0. Выход");
            Console.Write("Выбор: ");
            string choice = Console.ReadLine()?.Trim() ?? "";

            if (choice == "0") break;
            if (choice == "1") EditSettings();
            else if (choice == "2") SplitToParts();
            else if (choice == "3") CollectBack();
            else if (choice == "4") CheckParts();
        }
    }

    static Settings LoadSettings()
    {
        if (File.Exists(settingsFile))
        {
            try
            {
                var json = File.ReadAllText(settingsFile, Encoding.UTF8);
                var s = System.Text.Json.JsonSerializer.Deserialize<Settings>(json);
                if (s != null) return s;
            }
            catch { }
        }
        return new Settings();
    }

    static void SaveSettings()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(settings,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
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

    static string Instruction()
    {
        return "=== ИНСТРУКЦИЯ ДЛЯ ПЕРЕВОДЧИКА ===\n" +
            "Переведи строки между маркерами ниже с английского на русский.\n\n" +
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
            "=== КОНЕЦ ИНСТРУКЦИИ ===\n\n" +
            "### ПЕРЕВОДИТЬ ОТСЮДА ###\n";
    }

    static void SplitToParts()
    {
        if (!Directory.Exists(settings.ModDir)) { Log("Папка мода не найдена."); return; }
        string srcFile = Path.Combine(settings.ModDir, "Localization.csv");
        if (!File.Exists(srcFile)) { Log("Localization.csv не найден."); return; }

        var toTranslate = ReadToTranslate();
        Log($"Строк для перевода: {toTranslate.Count}");

        string partsDir = Path.Combine(settings.ModDir, "parts");
        if (Directory.Exists(partsDir))
        {
            Console.Write("Папка parts уже существует. Удалить? (y/n): ");
            string a = Console.ReadLine()?.Trim().ToLower() ?? "n";
            if (a != "y") { Log("Отменено."); return; }
            Directory.Delete(partsDir, true);
        }
        Directory.CreateDirectory(partsDir);

        int partSize = settings.PartSize;
        int totalParts = (toTranslate.Count + partSize - 1) / partSize;

        for (int p = 0; p < totalParts; p++)
        {
            var sb = new StringBuilder();
            sb.Append(Instruction());
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
            sb.AppendLine("### ПЕРЕВОДИТЬ ДО СЮДА ###");
            string fname = Path.Combine(partsDir, $"part_{p + 1:D3}.txt");
            File.WriteAllText(fname, sb.ToString(), new UTF8Encoding(false));
        }

        Log($"Готово. Создано частей: {totalParts}. Папка: {partsDir}");
    }

    static Dictionary<string, string> ReadTranslationsFromParts()
    {
        var translations = new Dictionary<string, string>();
        string partsDir = Path.Combine(settings.ModDir, "parts");
        if (!Directory.Exists(partsDir)) return translations;

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
                else if (line.StartsWith("E: ")) currentKey = null;
            }
        }
        return translations;
    }

    static void CheckParts()
    {
        if (!Directory.Exists(settings.ModDir)) { Log("Папка мода не найдена."); return; }
        var toTranslate = ReadToTranslate();
        var translations = ReadTranslationsFromParts();

        int found = 0, missing = 0, extra = 0;
        foreach (var (key, eng) in toTranslate)
        {
            if (translations.ContainsKey(key)) found++;
            else missing++;
        }

        var originalKeys = new HashSet<string>(toTranslate.Select(x => x.key));
        foreach (var k in translations.Keys)
        {
            if (!originalKeys.Contains(k)) extra++;
        }

        Log($"Всего строк для перевода: {toTranslate.Count}");
        Log($"Переведено: {found}");
        Log($"Не переведено: {missing}");
        Log($"Лишних переводов (чужих Key): {extra}");
        int pct = toTranslate.Count == 0 ? 0 : (found * 100 / toTranslate.Count);
        Log($"Прогресс: {pct}%");
    }

    static void CollectBack()
    {
        if (!Directory.Exists(settings.ModDir)) { Log("Папка мода не найдена."); return; }
        string srcFile = Path.Combine(settings.ModDir, "Localization.csv");
        if (!File.Exists(srcFile)) { Log("Localization.csv не найден."); return; }

        var translations = ReadTranslationsFromParts();
        Log($"Найдено переводов: {translations.Count}");

        var srcLines = File.ReadAllLines(srcFile, Encoding.UTF8);
        var header = ParseCsvLine(srcLines[0]);
        int keyIdx = header.IndexOf("Key");
        int rusIdx = header.IndexOf("russian");
        bool hasRussian = rusIdx >= 0;

        var outLines = new List<string>();
        outLines.Add(hasRussian ? srcLines[0] : srcLines[0] + ",russian");

        int written = 0, skipped = 0;
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
                written++;
            }
            else
            {
                outLines.Add(srcLines[i]);
                skipped++;
            }
        }

        string outFile = Path.Combine(settings.ModDir, "translated.csv");
        File.WriteAllLines(outFile, outLines, new UTF8Encoding(false));
        Log($"Готово. Записано переводов: {written}. Без перевода: {skipped}.");
        Log($"Результат: {outFile}");
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
    public int PartSize { get; set; } = 20;
}
